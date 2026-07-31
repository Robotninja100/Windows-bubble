using System.IO;
using System.Windows;
using CursorBubble.Actions;
using CursorBubble.ClaudeCode;
using CursorBubble.Config;
using CursorBubble.Diagnostics;
using CursorBubble.Native;
using CursorBubble.Overlay;
using CursorBubble.Responder;
using CursorBubble.Settings;
using CursorBubble.Tray;

namespace CursorBubble;

/// <summary>
/// Application entry point. Runs as a background tray app: it installs the
/// global mouse hook, owns the single reusable overlay window and routes the
/// gesture to the radial menu and its actions.
/// </summary>
public partial class App : Application
{
    private Mutex? _singleInstance;
    private MouseHook? _hook;
    private RadialMenuWindow? _overlay;
    private TrayIcon? _tray;
    private SettingsWindow? _settings;
    private ResponderWindow? _responder;
    private FileSystemWatcher? _inboxWatcher;

    /// <summary>"sessionId|timestamp" of events already surfaced, to avoid duplicate toasts.</summary>
    private readonly HashSet<string> _notifiedEvents = new();

    private AppConfig _config = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        InstallCrashHandlers();

        // Hook mode: launched by Claude Code with piped JSON on stdin. Record the
        // session and exit without any UI (and before the single-instance mutex).
        if (e.Args.Length > 0 && e.Args[0] == "--hook")
        {
            HookHandler.Run();
            Shutdown();
            return;
        }

        base.OnStartup(e);

        Log.Prune();
        Log.Info($"CursorBubble starting (v{typeof(App).Assembly.GetName().Version}).");

        // Only allow one running instance.
        _singleInstance = new Mutex(initiallyOwned: true, "CursorBubble.SingleInstance", out bool created);
        if (!created)
        {
            Shutdown();
            return;
        }

        _config = ConfigStore.Load();

        // Keep the registry autostart entry in sync with the saved preference.
        AutostartManager.Apply(_config.StartWithWindows);

        _overlay = new RadialMenuWindow(_config);

        _tray = new TrayIcon(_config.StartWithWindows);
        _tray.SettingsRequested += OpenSettings;
        _tray.ExitRequested += () => Shutdown();
        _tray.AutostartToggled += OnAutostartToggled;

        _hook = new MouseHook();
        _hook.MenuOpen += p => Dispatcher.InvokeAsync(() => _overlay!.ShowAt(p));
        _hook.MenuMove += p => Dispatcher.InvokeAsync(() => _overlay!.UpdateCursor(p));
        _hook.MenuCommit += () => Dispatcher.InvokeAsync(OnCommit);

        try
        {
            _hook.Install();
        }
        catch (Exception ex)
        {
            // Without the hook the gesture can never fire, but the tray icon and
            // settings still work — tell the user instead of crashing on startup.
            Log.Error("Failed to install the global mouse hook.", ex);
            _tray.ShowError("Could not enable the mouse gesture: " + ex.Message);
        }

        StartInboxWatcher();
    }

    /// <summary>
    /// Catch what would otherwise kill the app silently. A tray app has no
    /// window to show a crash in, so an unhandled exception just makes the icon
    /// disappear with no clue as to why.
    /// </summary>
    private void InstallCrashHandlers()
    {
        // UI-thread exceptions are usually local to one action (a dialog, a
        // click handler). Log it, tell the user, and keep the app alive rather
        // than tearing down a background process they rely on.
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error("Unhandled exception on the UI thread.", args.Exception);
            args.Handled = true;
            try
            {
                _tray?.ShowError("Something went wrong: " + args.Exception.Message);
            }
            catch
            {
                // the tray icon itself may be the thing that failed
            }
        };

        // Nothing can be done about these — the runtime is going down anyway.
        // Getting them on disk first is the whole point.
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Error("Unhandled exception, the process is terminating.", args.ExceptionObject as Exception);

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("Faulted task with nobody observing it.", args.Exception);
            args.SetObserved();
        };
    }

    private void OnCommit()
    {
        SegmentConfig? segment = _overlay!.Commit();
        if (segment is null)
            return; // cancelled in the dead zone

        if (segment.Action == ActionType.ClaudeInbox)
        {
            OpenResponder();
            return;
        }

        string? error = ActionRunner.Run(segment);
        if (error is not null)
        {
            Log.Warn($"Action for '{segment.Label}' failed: {error}");
            _tray?.ShowError(error);
        }
    }

    private void OpenResponder()
    {
        if (_responder is not null)
        {
            _responder.Activate();
            _responder.ReloadInbox();
            return;
        }

        _responder = new ResponderWindow();
        _responder.Closed += (_, _) => _responder = null;
        _responder.Show();
        _responder.Activate();
    }

    /// <summary>Watch the inbox folder and show a tray notification for new sessions.</summary>
    private void StartInboxWatcher()
    {
        try
        {
            Directory.CreateDirectory(InboxStore.Dir);
            _inboxWatcher = new FileSystemWatcher(InboxStore.Dir, "*.json")
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
                EnableRaisingEvents = true
            };
            // A session's file is overwritten when its state changes (e.g. waiting
            // -> finished), which raises Changed rather than Created — watch both.
            _inboxWatcher.Created += (_, ev) => OnInboxFileEvent(ev.FullPath);
            _inboxWatcher.Changed += (_, ev) => OnInboxFileEvent(ev.FullPath);
        }
        catch (Exception ex)
        {
            // watcher is optional — the bubble badge still reflects the count on open
            Log.Warn("Could not watch the inbox folder; tray notifications are off.", ex);
        }
    }

    /// <summary>
    /// Runs on a watcher thread: read the file that actually changed (with a
    /// short retry, since the write may not be flushed yet) and notify once per
    /// distinct session event.
    /// </summary>
    private void OnInboxFileEvent(string path)
    {
        InboxRecord? record = InboxStore.TryLoad(path);
        if (record is null)
            return;

        string key = record.SessionId + "|" + record.Timestamp;
        lock (_notifiedEvents)
        {
            if (!_notifiedEvents.Add(key))
                return; // Created + Changed can both fire for one write
            if (_notifiedEvents.Count > 200)
                _notifiedEvents.Clear();
        }

        Dispatcher.InvokeAsync(() =>
        {
            string what = record.State == SessionState.Waiting ? "is waiting for you" : "has finished";
            string project = string.IsNullOrWhiteSpace(record.ProjectName) ? "A session" : record.ProjectName;
            _tray?.ShowInfo("Claude Code", $"{project} {what}.");
            _responder?.ReloadInbox();
        });
    }

    private void OpenSettings()
    {
        if (_settings is not null)
        {
            _settings.Activate();
            return;
        }

        _settings = new SettingsWindow(_config);
        _settings.Saved += OnSettingsSaved;
        _settings.Closed += (_, _) => _settings = null;
        _settings.Show();
        _settings.Activate();
    }

    private void OnSettingsSaved(AppConfig updated)
    {
        _config = updated;
        ConfigStore.Save(_config);
        _overlay?.Rebuild(_config);
        AutostartManager.Apply(_config.StartWithWindows);
        _tray?.SetAutostartChecked(_config.StartWithWindows);
    }

    private void OnAutostartToggled(bool enabled)
    {
        _config.StartWithWindows = enabled;
        AutostartManager.Apply(enabled);
        ConfigStore.Save(_config);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Info("CursorBubble exiting.");
        _inboxWatcher?.Dispose();
        _hook?.Dispose();
        _tray?.Dispose();
        _overlay?.Close();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
