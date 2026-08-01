using System.Runtime.InteropServices;
using System.Windows.Interop;
using CursorBubble.Diagnostics;
using CursorBubble.Native;

namespace CursorBubble.Input;

/// <summary>
/// Owns the application's global hotkey.
///
/// A message-only window (<c>HWND_MESSAGE</c>) receives the <c>WM_HOTKEY</c>
/// messages: it never appears anywhere, costs nothing to keep alive, and does
/// not depend on any of the app's real windows existing yet.
///
/// <c>RegisterHotKey</c> rather than a keyboard hook, for three reasons. A
/// <c>WH_KEYBOARD_LL</c> hook is a global keylogger, which is a bad look for a
/// shortcut utility and reliably upsets antivirus. It costs a callback on every
/// keystroke in the session rather than only on the combination we asked for.
/// And decisively: <c>SetForegroundWindow</c>'s documented conditions include
/// "the process is processing a hotkey event" — that grant is the only reason
/// the bubble can take focus at all when opened from the keyboard. A hook does
/// not get it.
/// </summary>
internal sealed class HotkeyManager : IDisposable
{
    // Arbitrary but stable; the id only has to be unique within this window.
    private const int HotkeyId = 0xB0BB;

    private readonly HwndSource _source;
    private HotkeySpec? _registered;
    private bool _disposed;

    /// <summary>
    /// Raised on the hotkey. Handlers run synchronously inside the
    /// <c>WM_HOTKEY</c> turn — do not defer them onto the dispatcher, or the
    /// foreground grant that lets the bubble take focus is lost.
    /// </summary>
    public event Action? Pressed;

    public HotkeyManager()
    {
        _source = new HwndSource(new HwndSourceParameters("CursorBubble hotkey sink")
        {
            // HWND_MESSAGE: a message-only window. Never shown, never enumerated,
            // and it does not keep a message loop of its own alive.
            ParentWindow = HwndMessage,
            WindowStyle = 0
        });
        _source.AddHook(OnMessage);
    }

    private static IntPtr HwndMessage => new(-3);

    /// <summary>The combination currently registered, or null if none is.</summary>
    public HotkeySpec? Current => _registered;

    /// <summary>
    /// Register <paramref name="spec"/>, replacing whatever is registered now.
    ///
    /// On failure the previous binding is restored, so a bad edit in settings
    /// cannot leave the user with no hotkey at all. Returns false with a reason
    /// suitable for showing to the user.
    /// </summary>
    public bool TryRebind(HotkeySpec spec, out string error)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        error = "";
        if (_registered == spec) return true;

        HotkeySpec? previous = _registered;
        Unregister();

        if (TryRegister(spec, out error))
            return true;

        // Put the old one back. If even that fails there is nothing sensible
        // left to do but log it — the caller already has the real error.
        if (previous is HotkeySpec old && !TryRegister(old, out string rollbackError))
            Log.Warn($"Could not restore the previous hotkey {old} after a failed rebind: {rollbackError}");

        return false;
    }

    private bool TryRegister(HotkeySpec spec, out string error)
    {
        // MOD_NOREPEAT: without it, holding the combination down machine-guns
        // WM_HOTKEY and the bubble flickers open and shut.
        uint modifiers = spec.ToWin32() | NativeMethods.MOD_NOREPEAT;

        if (NativeMethods.RegisterHotKey(_source.Handle, HotkeyId, modifiers, spec.VirtualKey()))
        {
            _registered = spec;
            error = "";
            return true;
        }

        int lastError = Marshal.GetLastWin32Error();
        error = lastError == NativeMethods.ERROR_HOTKEY_ALREADY_REGISTERED
            ? $"{spec} is already in use by another application."
            : $"Windows refused to register {spec} (error {lastError}).";
        return false;
    }

    private void Unregister()
    {
        if (_registered is null) return;
        NativeMethods.UnregisterHotKey(_source.Handle, HotkeyId);
        _registered = null;
    }

    private IntPtr OnMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != NativeMethods.WM_HOTKEY || wParam.ToInt64() != HotkeyId)
            return IntPtr.Zero;

        handled = true;

        // Raised synchronously and on purpose: see the comment on Pressed.
        try
        {
            Pressed?.Invoke();
        }
        catch (Exception ex)
        {
            // An exception here would escape into the window procedure, which is
            // a much worse failure than a hotkey press that did nothing.
            Log.Error("The hotkey handler threw.", ex);
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Unregister();
        _source.RemoveHook(OnMessage);
        _source.Dispose();
    }
}
