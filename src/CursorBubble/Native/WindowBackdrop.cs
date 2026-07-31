namespace CursorBubble.Native;

/// <summary>
/// Applies a translucent "glass" backdrop (acrylic), a dark title bar and
/// rounded corners to a normal window via DWM. Only Windows 11 supports the
/// system backdrop; on Windows 10 this is a no-op that returns false so the
/// caller can fall back to a solid dark theme.
/// </summary>
internal static class WindowBackdrop
{
    private static bool IsWindows11 =>
        OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);

    /// <summary>
    /// Enable the glass backdrop. Returns true if a translucent backdrop was
    /// applied (so the caller should make the window background transparent).
    /// </summary>
    public static bool Apply(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
            return false;

        // Dark title bar works on Windows 10 2004+ as well.
        int dark = 1;
        NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));

        if (!IsWindows11)
            return false;

        int corner = NativeMethods.DWMWCP_ROUND;
        NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));

        int backdrop = NativeMethods.DWMSBT_TRANSIENTWINDOW; // acrylic
        int hr = NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));
        return hr == 0;
    }
}
