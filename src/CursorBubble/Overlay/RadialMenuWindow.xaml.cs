using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using CursorBubble.Accessibility;
using CursorBubble.ClaudeCode;
using CursorBubble.Config;
using CursorBubble.Diagnostics;
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

    private MenuInputMode _mode = MenuInputMode.Mouse;

    /// <summary>Window to hand focus back to when closing. Keyboard mode only; zero otherwise.</summary>
    private IntPtr _restoreTarget;

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

    /// <summary>True while the bubble is on screen.</summary>
    public bool IsOpen => Visibility == Visibility.Visible;

    /// <summary>What is driving the opening currently on screen.</summary>
    public MenuInputMode InputMode => _mode;

    /// <summary>
    /// The user chose a segment from the keyboard. The mouse path has its own
    /// commit signal (the button release), so this fires for keyboard mode only.
    /// </summary>
    public event Action? CommitRequested;

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

        ApplyInputMode(_mode);
    }

    /// <summary>
    /// Set the extended window styles for the given mode.
    ///
    /// Mouse mode is exactly what this window has always been: a tool window
    /// that never activates and is click-through, so it cannot disturb the app
    /// underneath while a mouse button is held.
    ///
    /// Keyboard mode keeps WS_EX_TOOLWINDOW — it stays out of alt-tab and the
    /// taskbar while remaining fully visible to UI Automation — and clears the
    /// other two, because a window that refuses activation cannot read the
    /// keyboard and a click-through window cannot be clicked away.
    /// </summary>
    private void ApplyInputMode(MenuInputMode mode)
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
            return; // applied from OnSourceInitialized once the handle exists

        int ex = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
        ex |= NativeMethods.WS_EX_TOOLWINDOW;

        if (mode == MenuInputMode.Keyboard)
            ex &= ~(NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TRANSPARENT);
        else
            ex |= NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TRANSPARENT;

        _ = NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE, ex);

        // Extended-style changes are not guaranteed to take effect until the
        // frame is recalculated.
        NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE |
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE |
            NativeMethods.SWP_FRAMECHANGED);

        Focusable = mode == MenuInputMode.Keyboard;
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

    /// <summary>
    /// Show the bubble centred on the given (physical pixel) cursor point, driven
    /// by the mouse. This overload exists so the gesture path is byte-for-byte
    /// the behaviour it has always had.
    /// </summary>
    public void ShowAt(ScreenPoint cursor) => ShowAt(cursor, MenuInputMode.Mouse, IntPtr.Zero);

    /// <summary>
    /// Show the bubble around the mouse pointer, wherever it is.
    ///
    /// Used by the hotkey. It used to centre the bubble on the monitor instead,
    /// on the reasoning that someone reaching for the keyboard does not care
    /// where the mouse is sitting. In use the opposite turned out to hold: the
    /// pointer is where you are already looking, and a bubble that opens
    /// somewhere else pulls your eyes across the screen every single time. Both
    /// ways in now put it in the same place, which is also one less thing to
    /// explain.
    ///
    /// <see cref="ShowAt(ScreenPoint, MenuInputMode, IntPtr)"/> keeps the whole
    /// ring inside the work area, so a pointer in a corner is already handled.
    /// </summary>
    public void ShowAtCursor(MenuInputMode mode, IntPtr restoreTarget)
    {
        if (NativeMethods.GetCursorPos(out NativeMethods.POINT pointer))
        {
            ShowAt(new ScreenPoint(pointer.x, pointer.y), mode, restoreTarget);
            return;
        }

        // No pointer position to be had — the middle of the primary monitor is a
        // better answer than its top-left corner.
        (_, NativeMethods.RECT work) = GetMonitorMetrics(new ScreenPoint(0, 0));

        var centre = new ScreenPoint(
            (work.left + work.right) / 2,
            (work.top + work.bottom) / 2);

        ShowAt(centre, mode, restoreTarget);
    }

    /// <summary>
    /// Show the bubble centred on the given (physical pixel) point.
    /// </summary>
    /// <param name="restoreTarget">
    /// Window to hand focus back to on close. Honoured in keyboard mode only —
    /// the mouse path never takes focus, so it has nothing to give back.
    /// </param>
    public void ShowAt(ScreenPoint cursor, MenuInputMode mode, IntPtr restoreTarget)
    {
        _mode = mode;
        _restoreTarget = mode == MenuInputMode.Keyboard ? restoreTarget : IntPtr.Zero;

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

        // After the handle exists and before the real move, so the frame is
        // already recalculated when the window lands in its final place.
        ApplyInputMode(mode);

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

        // Deliberately nothing selected: with the first segment pre-selected, a
        // reflexive Enter would run an arbitrary script. Announced as the opening
        // sentence rather than as a selection change.
        SetSelection(-1, announce: false);

        if (mode == MenuInputMode.Keyboard)
        {
            Say(MenuAnnouncement.Opened(_config.Segments.Count));
            TakeFocus(hwnd);
        }
        else
        {
            UpdateCursor(cursor);
        }
    }

    /// <summary>
    /// Put <paramref name="text"/> where a screen reader will read it.
    ///
    /// Both mechanisms on purpose: the proxy is the focused element, and Narrator
    /// follows focus far more reliably than it follows a live region in a
    /// transient top-most tool window — but the name alone changes nothing once
    /// focus has already landed, so the live region carries the updates.
    /// </summary>
    private void Say(string text)
    {
        AutomationProperties.SetName(AnnounceProxy, text);
        Announce.LiveRegion(AnnounceProxy);
    }

    /// <summary>
    /// Pull focus to the bubble so it can read the keyboard.
    ///
    /// This runs inside the WM_HOTKEY turn on purpose: "the process is
    /// processing a hotkey event" is one of SetForegroundWindow's documented
    /// conditions, and it is the only reason a window that was WS_EX_NOACTIVATE
    /// a moment ago is allowed to come to the front at all.
    /// </summary>
    private void TakeFocus(IntPtr hwnd)
    {
        Activate();
        NativeMethods.SetForegroundWindow(hwnd);

        // The proxy, not the window: it is the element that carries the
        // announcement, and Narrator reads the focused element.
        AnnounceProxy.Focus();

        // If the grant did not hold, the bubble is on screen but deaf. Log it
        // once so the field diagnosis exists rather than "it just does nothing".
        if (NativeMethods.GetForegroundWindow() != hwnd)
            Log.Warn("The bubble did not become the foreground window; keyboard selection will not work.");
    }

    /// <summary>
    /// The one place the selection changes, whichever input drove it.
    /// </summary>
    private void SetSelection(int index, bool announce = true)
    {
        if (index < 0 || index >= _config.Segments.Count)
            index = -1;

        bool changed = index != _currentIndex;
        _currentIndex = index;
        _menu.SetHighlight(index);

        // Keyboard mode only: the mouse path would fire this on every pixel of
        // movement, and there is no screen-reader user driving it with a mouse.
        if (announce && changed && _mode == MenuInputMode.Keyboard && IsOpen)
        {
            string? label = index >= 0 ? _config.Segments[index].Label : null;
            Say(MenuAnnouncement.Selected(index, _config.Segments.Count, label));
        }
    }

    /// <summary>Move the keyboard selection around the ring; see RadialMath.StepSelection.</summary>
    private void Step(int direction)
        => SetSelection(RadialMath.StepSelection(_currentIndex, direction, _config.Segments.Count));

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);

        if (_mode != MenuInputMode.Keyboard || !IsOpen || e.Handled)
            return;

        switch (e.Key)
        {
            // The ring is walked as a list, not as a compass: forwards is the
            // next segment clockwise regardless of where on screen it sits.
            case Key.Right:
            case Key.Down:
                Step(1);
                break;

            case Key.Left:
            case Key.Up:
                Step(-1);
                break;

            // Tab is mapped too, so it cannot wander out of a window that is the
            // only thing on screen and leave the bubble unreachable.
            case Key.Tab:
                Step(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1);
                break;

            case Key.Home:
                SetSelection(_config.Segments.Count > 0 ? 0 : -1);
                break;

            case Key.End:
                SetSelection(_config.Segments.Count - 1);
                break;

            case Key.Enter:
            case Key.Space:
                CommitRequested?.Invoke();
                break;

            case Key.Escape:
                CancelMenu();
                break;

            default:
                int digit = DigitFor(e.Key);
                if (digit < 0)
                    return; // not ours: leave e.Handled alone

                int index = RadialMath.IndexForDigit(digit, _config.Segments.Count);
                if (index < 0)
                    return; // names no segment, so do nothing rather than something arbitrary

                SetSelection(index);
                break;
        }

        e.Handled = true;
    }

    /// <summary>The digit a key stands for, from the number row or the keypad; -1 if it is not a digit.</summary>
    private static int DigitFor(Key key) => key switch
    {
        >= Key.D0 and <= Key.D9 => key - Key.D0,
        >= Key.NumPad0 and <= Key.NumPad9 => key - Key.NumPad0,
        _ => -1
    };

    /// <summary>Close without running anything, handing focus back where it came from.</summary>
    public void CancelMenu() => HideMenu();

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);

        // Clicking or alt-tabbing elsewhere cancels, the same way releasing in
        // the dead zone does. Mouse mode never activates, so it never gets here.
        if (_mode == MenuInputMode.Keyboard && IsOpen)
            HideMenu();
    }

    /// <summary>
    /// Hand focus back to whatever was in front before the bubble opened.
    ///
    /// Load-bearing: the actions run against <em>other</em> windows, so an action
    /// that fires while the bubble still owns the foreground would target the
    /// wrong thing entirely.
    /// </summary>
    private void RestoreForeground()
    {
        if (_restoreTarget == IntPtr.Zero)
            return;

        IntPtr target = _restoreTarget;
        _restoreTarget = IntPtr.Zero;
        NativeMethods.SetForegroundWindow(target);
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
        SetSelection(_menu.HitTest(dxDip, dyDip));
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
        SetSelection(-1);

        // Before returning, not afterwards: the caller runs the chosen action
        // next and it must find the original window in front.
        RestoreForeground();

        _mode = MenuInputMode.Mouse;
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
