using System.Runtime.InteropServices;

namespace CursorBubble.Native;

/// <summary>
/// Screen coordinate in physical pixels (as reported by the OS mouse hook).
/// </summary>
public readonly record struct ScreenPoint(int X, int Y);

/// <summary>
/// Installs a global low-level mouse hook and recognises the activation gesture:
/// hold the RIGHT button and press the LEFT button. While the gesture is active
/// the radial menu is shown; the first button release commits the selection.
///
/// The right-button-down that starts the gesture is swallowed so it never
/// reaches the app below. If the user releases the right button without pressing
/// left (an ordinary right click) the click is replayed with SendInput so the
/// normal context menu still appears.
///
/// The hook is installed on the WPF UI thread, so its callback runs there and
/// events are raised directly on the UI thread. Handlers must be fast; heavier
/// UI work is marshalled asynchronously by the subscribers.
/// </summary>
public sealed class MouseHook : IDisposable
{
    private enum State
    {
        Idle,
        RightHeld,   // right down swallowed, waiting to see if left follows
        MenuActive,  // gesture recognised, menu shown, waiting for a release
        Draining     // committed, swallowing button events until all released
    }

    // Keep the delegate alive for the lifetime of the hook.
    private readonly NativeMethods.LowLevelMouseProc _proc;
    private IntPtr _hookHandle = IntPtr.Zero;

    private State _state = State.Idle;
    private bool _leftDown;
    private bool _rightDown;

    /// <summary>Raised when the gesture opens the menu. Argument: cursor position.</summary>
    public event Action<ScreenPoint>? MenuOpen;

    /// <summary>Raised on cursor movement while the menu is open.</summary>
    public event Action<ScreenPoint>? MenuMove;

    /// <summary>Raised when a button is released and the selection should commit.</summary>
    public event Action? MenuCommit;

    public MouseHook()
    {
        _proc = HookCallback;
    }

    public void Install()
    {
        if (_hookHandle != IntPtr.Zero)
            return;

        // WH_MOUSE_LL does not require a module handle for a managed callback,
        // but passing the current module keeps older Windows happy.
        IntPtr module = NativeMethods.GetModuleHandle(null);
        _hookHandle = NativeMethods.SetWindowsHookEx(NativeMethods.WH_MOUSE_LL, _proc, module, 0);
        if (_hookHandle == IntPtr.Zero)
            throw new InvalidOperationException("Failed to install the global mouse hook.");
    }

    public void Uninstall()
    {
        if (_hookHandle != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_hookHandle);
            _hookHandle = IntPtr.Zero;
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0)
            return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);

        var data = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);

        // Ignore anything we injected ourselves (the replayed right click),
        // otherwise we would re-enter the state machine.
        if ((data.flags & NativeMethods.LLMHF_INJECTED) != 0)
            return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);

        // The message id is a small constant; the truncation is intentional.
        int msg = unchecked((int)wParam);
        bool swallow = false;

        switch (msg)
        {
            case NativeMethods.WM_RBUTTONDOWN:
                _rightDown = true;
                if (_state == State.Idle)
                {
                    // Hold the right-down back until we know whether the user
                    // is chording with left (gesture) or just right-clicking.
                    _state = State.RightHeld;
                    swallow = true;
                }
                break;

            case NativeMethods.WM_LBUTTONDOWN:
                _leftDown = true;
                if (_state == State.RightHeld)
                {
                    // Gesture recognised.
                    _state = State.MenuActive;
                    swallow = true;
                    RaiseOpen(data.pt);
                }
                break;

            case NativeMethods.WM_MOUSEMOVE:
                if (_state == State.MenuActive)
                    MenuMove?.Invoke(new ScreenPoint(data.pt.x, data.pt.y));
                break;

            case NativeMethods.WM_LBUTTONUP:
                _leftDown = false;
                if (_state == State.MenuActive)
                {
                    swallow = true;
                    Commit();
                }
                else if (_state == State.Draining)
                {
                    swallow = true;
                    MaybeFinishDraining();
                }
                break;

            case NativeMethods.WM_RBUTTONUP:
                _rightDown = false;
                switch (_state)
                {
                    case State.RightHeld:
                        // Plain right click: nothing chorded. Replay it so the
                        // normal context menu behaviour is preserved.
                        swallow = true;
                        _state = State.Idle;
                        ReplayRightClick();
                        break;

                    case State.MenuActive:
                        swallow = true;
                        Commit();
                        break;

                    case State.Draining:
                        swallow = true;
                        MaybeFinishDraining();
                        break;
                }
                break;
        }

        if (swallow)
            return new IntPtr(1); // block the event from reaching other apps

        return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    private void RaiseOpen(NativeMethods.POINT pt)
        => MenuOpen?.Invoke(new ScreenPoint(pt.x, pt.y));

    private void Commit()
    {
        MenuCommit?.Invoke();
        _state = State.Draining;
        MaybeFinishDraining();
    }

    private void MaybeFinishDraining()
    {
        // Stay in Draining (swallowing button events) until both buttons are up,
        // so a still-held right button never fires a context menu afterwards.
        if (!_leftDown && !_rightDown)
            _state = State.Idle;
    }

    private static void ReplayRightClick()
    {
        var inputs = new[]
        {
            new NativeMethods.INPUT
            {
                type = NativeMethods.INPUT_MOUSE,
                mi = new NativeMethods.MOUSEINPUT { dwFlags = NativeMethods.MOUSEEVENTF_RIGHTDOWN }
            },
            new NativeMethods.INPUT
            {
                type = NativeMethods.INPUT_MOUSE,
                mi = new NativeMethods.MOUSEINPUT { dwFlags = NativeMethods.MOUSEEVENTF_RIGHTUP }
            }
        };
        _ = NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.INPUT>());
    }

    public void Dispose() => Uninstall();
}
