using System.IO;
using System.Windows;
using CursorBubble.Actions;
using CursorBubble.ClaudeCode;
using CursorBubble.Config;
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
        // Hook mode: launched by Claude Code with piped JSON on stdin. Record the
        // session and exit without any UI (and before the single-instance mutex).
        if (e.Args.Length > 0 && e.Args[0] == "--hook")
        {
            HookHandler.Run();
            Shutdown();
            return;
        }

        base.OnStartup(e);

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

        // If the saved settings could not be read, say so once. Coming back to
        // factory defaults without explanation reads as the app having forgotten
        // everything by itself.
        if (ConfigStore.LastLoadFailure is string failure)
            _tray.ShowError(failure);

        _hook = new MouseHook();
        _hook.MenuOpen += p => Dispatcher.InvokeAsync(() => _overlay!.ShowAt(p));
        _hook.MenuMove += p => Dispatcher.InvokeAsync(() => _overlay!.UpdateCursor(p));
        _hook.MenuCommit += () => Dispatcher.InvokeAsync(OnCommit);
        _hook.Install();

        StartInboxWatcher();
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
            _tray?.ShowError(error);
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
            //
            // Renamed matters just as much: InboxStore writes through a temporary
            // file and moves it into place, so the arrival of a record reaches us
            // as a rename, not as a create. Subscribing to only the first two
            // would leave the tray notifications silent.
            _inboxWatcher.Created += (_, ev) => OnInboxFileEvent(ev.FullPath);
            _inboxWatcher.Changed += (_, ev) => OnInboxFileEvent(ev.FullPath);
            _inboxWatcher.Renamed += (_, ev) => OnInboxFileEvent(ev.FullPath);

            // The watcher stops delivering after a buffer overflow — a burst from
            // several concurrent sessions is exactly that shape — and nothing
            // else would ever re-arm it.
            _inboxWatcher.Error += (_, _) => Dispatcher.InvokeAsync(RestartInboxWatcher);
        }
        catch
        {
            // watcher is optional — the bubble badge still reflects the count on open
        }
    }

    /// <summary>Rebuild the watcher after it reported an error and stopped.</summary>
    private void RestartInboxWatcher()
    {
        _inboxWatcher?.Dispose();
        _inboxWatcher = null;
        StartInboxWatcher();
    }

    /// <summary>
    /// Runs on a watcher thread: read the file that actually changed and notify
    /// once per distinct session event.
    ///
    /// The read is handed to the thread pool rather than done here.
    /// <see cref="InboxStore.TryLoad"/> retries for up to a third of a second,
    /// and every millisecond spent on the watcher's own thread is a millisecond
    /// of further events piling up in its fixed-size buffer.
    /// </summary>
    private void OnInboxFileEvent(string path) => Task.Run(() => ReadAndNotify(path));

    private void ReadAndNotify(string path)
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
            string what = record.State == SessionState.Waiting ? "wacht op je" : "is klaar";
            string project = string.IsNullOrWhiteSpace(record.ProjectName) ? "een sessie" : record.ProjectName;
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
        _inboxWatcher?.Dispose();
        _hook?.Dispose();
        _tray?.Dispose();
        _overlay?.Close();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
