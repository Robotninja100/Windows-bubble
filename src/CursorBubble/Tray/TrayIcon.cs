using System.Drawing;
using System.Windows.Forms;

namespace CursorBubble.Tray;

/// <summary>
/// System-tray presence for the app: a context menu with Settings, an
/// auto-start toggle and Exit. The app has no main window, so this is the only
/// always-available entry point.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly ToolStripMenuItem _autostartItem;

    public event Action? SettingsRequested;
    public event Action? ExitRequested;
    public event Action<bool>? AutostartToggled;

    public TrayIcon(bool autostartEnabled)
    {
        var menu = new ContextMenuStrip();

        var settingsItem = new ToolStripMenuItem("Settings…");
        settingsItem.Click += (_, _) => SettingsRequested?.Invoke();

        _autostartItem = new ToolStripMenuItem("Start with Windows")
        {
            CheckOnClick = true,
            Checked = autostartEnabled
        };
        _autostartItem.CheckedChanged += (_, _) => AutostartToggled?.Invoke(_autostartItem.Checked);

        var exitItem = new ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) => ExitRequested?.Invoke();

        menu.Items.Add(settingsItem);
        menu.Items.Add(_autostartItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exitItem);

        _notifyIcon = new NotifyIcon
        {
            Text = "CursorBubble — hold right + click left",
            Icon = CreateIcon(),
            Visible = true,
            ContextMenuStrip = menu
        };

        // Double-click the tray icon to open settings.
        _notifyIcon.DoubleClick += (_, _) => SettingsRequested?.Invoke();
    }

    /// <summary>Reflect the current autostart state without re-firing the toggle event.</summary>
    public void SetAutostartChecked(bool value)
    {
        if (_autostartItem.Checked != value)
        {
            // Temporarily detach is overkill; setting Checked fires CheckedChanged,
            // but AutostartManager.Apply is idempotent so this is harmless.
            _autostartItem.Checked = value;
        }
    }

    public void ShowError(string message)
    {
        _notifyIcon.BalloonTipTitle = "CursorBubble";
        _notifyIcon.BalloonTipIcon = ToolTipIcon.Warning;
        _notifyIcon.BalloonTipText = message;
        _notifyIcon.ShowBalloonTip(4000);
    }

    /// <summary>Show an informational tray notification.</summary>
    public void ShowInfo(string title, string message)
    {
        _notifyIcon.BalloonTipTitle = title;
        _notifyIcon.BalloonTipIcon = ToolTipIcon.Info;
        _notifyIcon.BalloonTipText = message;
        _notifyIcon.ShowBalloonTip(4000);
    }

    /// <summary>Load the bundled app icon; fall back to a runtime-drawn one.</summary>
    private static Icon CreateIcon()
    {
        try
        {
            var uri = new Uri("pack://application:,,,/Assets/app.ico");
            System.Windows.Resources.StreamResourceInfo? info = System.Windows.Application.GetResourceStream(uri);
            if (info is not null)
            {
                using Stream stream = info.Stream;
                return new Icon(stream, new Size(32, 32));
            }
        }
        catch
        {
            // fall through to the drawn icon
        }
        return DrawFallbackIcon();
    }

    /// <summary>Draw a small glassy bubble icon at runtime (used if the asset is missing).</summary>
    private static Icon DrawFallbackIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            using var ring = new SolidBrush(Color.FromArgb(230, 76, 141, 255));
            g.FillEllipse(ring, 3, 3, 26, 26);

            using var hole = new SolidBrush(Color.FromArgb(255, 30, 34, 44));
            g.FillEllipse(hole, 11, 11, 10, 10);

            using var pen = new Pen(Color.FromArgb(200, 255, 255, 255), 1.5f);
            g.DrawEllipse(pen, 3, 3, 26, 26);
        }

        IntPtr hIcon = bmp.GetHicon();
        // Clone into a managed Icon so we can free the GDI handle immediately.
        using var tmp = Icon.FromHandle(hIcon);
        var icon = (Icon)tmp.Clone();
        DestroyIcon(hIcon);
        return icon;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }
}
