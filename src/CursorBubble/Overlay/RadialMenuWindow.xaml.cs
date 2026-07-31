using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using CursorBubble.ClaudeCode;
using CursorBubble.Config;
using CursorBubble.Native;

namespace CursorBubble.Overlay;

/// <summary>
/// The transparent, top-most overlay window that hosts the glass radial menu.
///
/// It never takes focus and is positioned in physical pixels via SetWindowPos
/// so it stays centred on the cursor across monitors and DPI scales. Hit-testing
/// is driven entirely by the global cursor position (the window itself is
/// hit-test transparent), which keeps it robust while a mouse button is held.
/// </summary>
public partial class RadialMenuWindow : Window
{
    private readonly RadialMenuControl _menu = new();
    private readonly ScaleTransform _scale = new(1, 1);
    private AppConfig _config;

    private bool _shownOnce;
    private double _scale = 1.0;
    private double _centerX; // window centre, physical pixels
    private double _centerY;
    private int _currentIndex = -1;

    public RadialMenuWindow(AppConfig config)
    {
        InitializeComponent();
        _config = config;
        // Start off-screen so the first Show() doesn't flash at the top-left
        // before SetWindowPos moves it onto the cursor.
        Left = -10000;
        Top = -10000;
        RootGrid.Children.Add(_menu);
        RootGrid.RenderTransformOrigin = new Point(0.5, 0.5);
        RootGrid.RenderTransform = _scale;
        _menu.Build(_config);
    }

    /// <summary>Currently highlighted segment index, or -1 for none.</summary>
    public int CurrentIndex => _currentIndex;

    /// <summary>Rebuild the menu after the configuration changed.</summary>
    public void Rebuild(AppConfig config)
    {
        _config = config;
        _menu.Build(_config);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        IntPtr hwnd = new WindowInteropHelper(this).Handle;

        // Tool window, never activates, click-through: it must not disturb the
        // app underneath while the gesture is in progress.
        int ex = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
        ex |= NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TRANSPARENT;
        NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE, ex);
    }

    /// <summary>(Re)apply the circular desktop blur for the current window size.</summary>
    private void ApplyGlass(int sizePx)
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
            return;

        if (_config.Style.UseAcrylicBlur)
            AcrylicHelper.EnableCircularBlur(hwnd, sizePx);
        else
            AcrylicHelper.Disable(hwnd);
    }

    /// <summary>Show the bubble centred on the given (physical pixel) cursor point.</summary>
    public void ShowAt(ScreenPoint cursor)
    {
        // Refresh the Claude Code inbox badge with the current pending count.
        int newCount = InboxStore.UnansweredCount();
        if (newCount != _menu.InboxCount)
        {
            _menu.InboxCount = newCount;
            _menu.Build(_config);
        }

        (double scale, NativeMethods.RECT work) = GetMonitorMetrics(cursor);
        _scale = scale;

        double diameterPx = _menu.Diameter * scale;

        double left = cursor.X - diameterPx / 2.0;
        double top = cursor.Y - diameterPx / 2.0;

        // Keep the whole bubble inside the monitor work area.
        left = Clamp(left, work.left, work.right - diameterPx);
        top = Clamp(top, work.top, work.bottom - diameterPx);

        _centerX = left + diameterPx / 2.0;
        _centerY = top + diameterPx / 2.0;

        if (!_shownOnce)
        {
            Show();
            _shownOnce = true;
        }
        else
        {
            Visibility = Visibility.Visible;
        }

        int sizePx = (int)Math.Ceiling(diameterPx);
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST,
            (int)Math.Round(left), (int)Math.Round(top),
            sizePx, sizePx,
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);

        // Clip the desktop blur to the circle at the current size.
        ApplyGlass(sizePx);

        PlayOpenAnimation();

        _currentIndex = -1;
        _menu.SetHighlight(-1);
        UpdateCursor(cursor);
    }

    private void PlayOpenAnimation()
    {
        if (!_config.Style.Animate)
        {
            // Ensure a clean, fully-visible state when animation is disabled.
            RootGrid.BeginAnimation(OpacityProperty, null);
            _scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            _scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            RootGrid.Opacity = 1;
            _scale.ScaleX = _scale.ScaleY = 1;
            return;
        }

        var dur = TimeSpan.FromMilliseconds(130);
        var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        var fade = new DoubleAnimation(0, 1, dur);
        var pop = new DoubleAnimation(0.85, 1.0, dur) { EasingFunction = ease };

        RootGrid.BeginAnimation(OpacityProperty, fade);
        _scale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
        _scale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
    }

    /// <summary>Update the highlighted segment from the current cursor position.</summary>
    public void UpdateCursor(ScreenPoint cursor)
    {
        double dxDip = (cursor.X - _centerX) / _scale;
        double dyDip = (cursor.Y - _centerY) / _scale;
        _currentIndex = _menu.HitTest(dxDip, dyDip);
        _menu.SetHighlight(_currentIndex);
    }

    /// <summary>Hide the bubble and return the segment that was selected (or null).</summary>
    public SegmentConfig? Commit()
    {
        int index = _currentIndex;
        HideMenu();
        if (index >= 0 && index < _config.Segments.Count)
            return _config.Segments[index];
        return null;
    }

    public void HideMenu()
    {
        Visibility = Visibility.Hidden;
        _currentIndex = -1;
        _menu.SetHighlight(-1);
    }

    private static (double scale, NativeMethods.RECT work) GetMonitorMetrics(ScreenPoint p)
    {
        var pt = new NativeMethods.POINT { x = p.X, y = p.Y };
        IntPtr mon = NativeMethods.MonitorFromPoint(pt, NativeMethods.MONITOR_DEFAULTTONEAREST);

        double scale = 1.0;
        if (NativeMethods.GetDpiForMonitor(mon, NativeMethods.MDT_EFFECTIVE_DPI, out uint dpiX, out _) == 0 && dpiX > 0)
            scale = dpiX / 96.0;

        var mi = new NativeMethods.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        NativeMethods.RECT work;
        if (NativeMethods.GetMonitorInfo(mon, ref mi))
            work = mi.rcWork;
        else
            work = new NativeMethods.RECT { left = 0, top = 0, right = 100000, bottom = 100000 };

        return (scale, work);
    }

    private static double Clamp(double v, double min, double max)
    {
        if (max < min) return min; // bubble larger than the work area
        return Math.Min(Math.Max(v, min), max);
    }
}
