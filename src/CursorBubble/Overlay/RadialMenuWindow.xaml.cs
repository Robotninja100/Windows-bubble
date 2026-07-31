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
    private readonly ScaleTransform _zoom = new(1, 1);
    private AppConfig _config;

    private bool _shownOnce;

    /// <summary>Window size the frost is waiting to be applied at, once the open animation settles.</summary>
    private int _pendingGlassSizePx;

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
        RootGrid.RenderTransform = _zoom;
        _menu.EnableAnimations = _config.Style.Animate;
        _menu.OpenAnimationCompleted += OnOpenAnimationCompleted;
        _menu.Build(_config);
    }

    /// <summary>Currently highlighted segment index, or -1 for none.</summary>
    public int CurrentIndex => _currentIndex;

    /// <summary>Rebuild the menu after the configuration changed.</summary>
    public void Rebuild(AppConfig config)
    {
        _config = config;
        _menu.EnableAnimations = _config.Style.Animate;
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

    /// <summary>(Re)apply the desktop blur, clipped to the glass segments.</summary>
    private void ApplyGlass(int sizePx)
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
            return;

        if (_config.Style.UseAcrylicBlur)
            AcrylicHelper.EnableBlur(hwnd, _menu.BuildBlurPolygons(_scale), sizePx);
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

        bool animate = _config.Style.Animate;

        // Collapse the petals before the window is painted, so the first frame is
        // already the start of the animation rather than the finished bubble.
        if (animate)
            _menu.PrepareOpenAnimation();

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

        if (animate)
        {
            // The blur region is a GDI region on the window; it does not follow the
            // WPF transforms, so a full-size frosted ring would hang in the air
            // while the petals are still small. It also survives hiding the window,
            // so the previous open's region has to be actively cleared — skipping
            // ApplyGlass is not enough. The frost is applied when the petals settle.
            _pendingGlassSizePx = sizePx;
            AcrylicHelper.Disable(hwnd);
            _menu.StartOpenAnimation();
        }
        else
        {
            _pendingGlassSizePx = 0;
            ApplyGlass(sizePx);
            ResetOpenAnimation();
        }

        _currentIndex = -1;
        _menu.SetHighlight(-1);
        UpdateCursor(cursor);
    }

    /// <summary>Frost the desktop once the petals have settled at full size.</summary>
    private void OnOpenAnimationCompleted()
    {
        if (Visibility == Visibility.Visible && _pendingGlassSizePx > 0)
            ApplyGlass(_pendingGlassSizePx);
        _pendingGlassSizePx = 0;
    }

    /// <summary>Drop straight to the fully-open state (animations turned off).</summary>
    private void ResetOpenAnimation()
    {
        _menu.CancelOpenAnimation();
        RootGrid.BeginAnimation(OpacityProperty, null);
        _zoom.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _zoom.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        RootGrid.Opacity = 1;
        _zoom.ScaleX = _zoom.ScaleY = 1;
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
        // Closing stays instant so the chosen action fires without delay.
        Visibility = Visibility.Hidden;
        _pendingGlassSizePx = 0;
        _menu.CancelOpenAnimation();
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
