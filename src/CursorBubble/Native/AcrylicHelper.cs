using System.Windows;

namespace CursorBubble.Native;

/// <summary>
/// Frosted-glass helper. Uses DWM blur-behind clipped to a region so the desktop
/// is blurred only underneath the glass; everything outside that region — the
/// gaps between the segments, the cancel zone, the transparent corners of the
/// (per-pixel) overlay window — stays crisp. Degrades gracefully to a plain
/// translucent shape when the OS no longer honours blur-behind.
/// </summary>
internal static class AcrylicHelper
{
    /// <summary>
    /// Blur the desktop behind the given polygons (physical pixels, relative to
    /// the window's client area). Falls back to a circular blur of
    /// <paramref name="sizePx"/> when no polygons are supplied.
    /// </summary>
    public static void EnableBlur(IntPtr hwnd, IReadOnlyList<Point[]> polygons, int sizePx)
    {
        if (hwnd == IntPtr.Zero || sizePx <= 0)
            return;

        IntPtr region = BuildRegion(polygons) ?? NativeMethods.CreateEllipticRgn(0, 0, sizePx, sizePx);

        var bb = new NativeMethods.DWM_BLURBEHIND
        {
            dwFlags = NativeMethods.DWM_BB_ENABLE | NativeMethods.DWM_BB_BLURREGION,
            fEnable = true,
            hRgnBlur = region,
            fTransitionOnMaximized = false
        };

        try
        {
            _ = NativeMethods.DwmEnableBlurBehindWindow(hwnd, ref bb);
        }
        finally
        {
            // DWM copies the region; free our GDI handle.
            NativeMethods.DeleteObject(region);
        }
    }

    /// <summary>Union of the given polygons, or null if there is nothing usable.</summary>
    private static IntPtr? BuildRegion(IReadOnlyList<Point[]> polygons)
    {
        if (polygons.Count == 0)
            return null;

        IntPtr combined = IntPtr.Zero;
        try
        {
            foreach (Point[] polygon in polygons)
            {
                if (polygon.Length < 3)
                    continue;

                var points = new NativeMethods.POINT[polygon.Length];
                for (int i = 0; i < polygon.Length; i++)
                {
                    points[i] = new NativeMethods.POINT
                    {
                        x = (int)Math.Round(polygon[i].X),
                        y = (int)Math.Round(polygon[i].Y)
                    };
                }

                IntPtr part = NativeMethods.CreatePolygonRgn(points, points.Length, NativeMethods.WINDING);
                if (part == IntPtr.Zero)
                    continue;

                if (combined == IntPtr.Zero)
                {
                    combined = part;
                    continue;
                }

                // CombineRgn needs a destination handle of its own.
                IntPtr merged = NativeMethods.CreateRectRgn(0, 0, 1, 1);
                _ = NativeMethods.CombineRgn(merged, combined, part, NativeMethods.RGN_OR);
                NativeMethods.DeleteObject(combined);
                NativeMethods.DeleteObject(part);
                combined = merged;
            }
        }
        catch
        {
            if (combined != IntPtr.Zero)
                NativeMethods.DeleteObject(combined);
            return null;
        }

        return combined == IntPtr.Zero ? null : combined;
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
        _ = NativeMethods.DwmEnableBlurBehindWindow(hwnd, ref bb);
    }
}
