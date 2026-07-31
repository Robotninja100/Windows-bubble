using System.Windows;
using CursorBubble.Actions;
using CursorBubble.Config;
using CursorBubble.Native;
using CursorBubble.Overlay;
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

    private AppConfig _config = new();

    protected override void OnStartup(StartupEventArgs e)
    {
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

        _hook = new MouseHook();
        _hook.MenuOpen += p => Dispatcher.InvokeAsync(() => _overlay!.ShowAt(p));
        _hook.MenuMove += p => Dispatcher.InvokeAsync(() => _overlay!.UpdateCursor(p));
        _hook.MenuCommit += () => Dispatcher.InvokeAsync(OnCommit);
        _hook.Install();
    }

    private void OnCommit()
    {
        SegmentConfig? segment = _overlay!.Commit();
        if (segment is null)
            return; // cancelled in the dead zone

        string? error = ActionRunner.Run(segment);
        if (error is not null)
            _tray?.ShowError(error);
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
        _hook?.Dispose();
        _tray?.Dispose();
        _overlay?.Close();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
