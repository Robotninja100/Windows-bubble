namespace CursorBubble.Native;

/// <summary>
/// Frosted-glass helper. Uses DWM blur-behind clipped to a circular region so
/// the desktop is blurred only inside the bubble; the transparent corners of
/// the (per-pixel) overlay window stay invisible. Degrades gracefully to a plain
/// translucent circle when the OS no longer honours blur-behind.
/// </summary>
internal static class AcrylicHelper
{
    /// <summary>
    /// Enable a circular desktop blur behind the window.
    /// </summary>
    /// <param name="hwnd">Overlay window handle.</param>
    /// <param name="sizePx">Window client size in physical pixels (square).</param>
    public static void EnableCircularBlur(IntPtr hwnd, int sizePx)
    {
        if (hwnd == IntPtr.Zero || sizePx <= 0)
            return;

        IntPtr region = NativeMethods.CreateEllipticRgn(0, 0, sizePx, sizePx);
        var bb = new NativeMethods.DWM_BLURBEHIND
        {
            dwFlags = NativeMethods.DWM_BB_ENABLE | NativeMethods.DWM_BB_BLURREGION,
            fEnable = true,
            hRgnBlur = region,
            fTransitionOnMaximized = false
        };

        try
        {
            NativeMethods.DwmEnableBlurBehindWindow(hwnd, ref bb);
        }
        finally
        {
            // DWM copies the region; free our GDI handle.
            NativeMethods.DeleteObject(region);
        }
    }

    public static void Disable(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
            return;

        var bb = new NativeMethods.DWM_BLURBEHIND
        {
            dwFlags = NativeMethods.DWM_BB_ENABLE,
            fEnable = false,
            hRgnBlur = IntPtr.Zero,
            fTransitionOnMaximized = false
        };
        NativeMethods.DwmEnableBlurBehindWindow(hwnd, ref bb);
    }
}
