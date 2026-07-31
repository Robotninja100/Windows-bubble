namespace CursorBubble.Overlay;

/// <summary>
/// The pure maths behind the radial menu: angles, hit-testing and the corner
/// radius the petals can actually accommodate.
///
/// Kept free of WPF types and of any control state so it can be reasoned about —
/// and tested — on its own. <see cref="RadialMenuControl"/> is the only caller.
/// </summary>
internal static class RadialMath
{
    /// <summary>Positive modulo, so negative angles wrap instead of staying negative.</summary>
    public static double Mod(double value, double m) => ((value % m) + m) % m;

    public static double Degrees(double radians) => radians * 180.0 / Math.PI;

    /// <summary>
    /// Angle of an offset from the centre, in degrees clockwise from straight up,
    /// in a y-down coordinate system.
    /// </summary>
    public static double ClockwiseAngleFromTop(double dx, double dy)
        => Mod(Math.Atan2(dx, -dy) * 180.0 / Math.PI, 360);

    /// <summary>
    /// Map a cursor offset from the centre (in DIPs, y-down) to a segment index.
    /// Returns -1 for the central dead zone (cancel) or when there are no segments.
    ///
    /// Distance beyond the outer radius deliberately still selects: a radial menu
    /// is aimed at by direction, so flicking well past the ring picks the segment
    /// you flicked towards.
    /// </summary>
    public static int SegmentAt(double dx, double dy, int count, double startAngle, double innerRadius)
    {
        if (count <= 0)
            return -1;

        if (Math.Sqrt(dx * dx + dy * dy) < innerRadius)
            return -1; // dead zone = cancel

        double rel = Mod(ClockwiseAngleFromTop(dx, dy) - startAngle, 360);
        int index = (int)(rel / (360.0 / count));
        return Math.Clamp(index, 0, count - 1);
    }

    /// <summary>
    /// The largest corner radius whose fillets still fit in a petal, capped at
    /// <paramref name="wanted"/>.
    ///
    /// A fillet's centre sits on a circle of radius <c>rho</c> (rOut - corner
    /// outside, rIn + corner inside) rotated away from the radial edge by
    /// <c>phi</c>, where <c>sin(phi) = corner / rho</c>. Two fillets share one
    /// edge, so each may span at most half the sweep, less a sliver of straight
    /// edge; inverting that for <c>corner</c> gives the ceilings below.
    ///
    /// Shrinking rather than giving up matters: without it, adding one more
    /// segment would silently snap every petal back to sharp wedges.
    /// </summary>
    /// <returns>0 when no fillet fits at all, in which case the caller draws a plain wedge.</returns>
    public static double FittedCornerRadius(double innerRadius, double outerRadius, double sweepDegrees, double wanted)
    {
        if (wanted <= 0 || innerRadius <= 0.5 || outerRadius <= innerRadius || sweepDegrees <= 2.5)
            return 0;

        // Leave 2 degrees of straight edge so the fillets never quite meet.
        double sinPhi = Math.Sin(Math.Min((sweepDegrees - 2.0) / 2.0 * Math.PI / 180.0, Math.PI / 2 - 1e-6));
        if (sinPhi <= 0 || sinPhi >= 1)
            return 0;

        double corner = Math.Min(wanted, (outerRadius - innerRadius) / 2.0);
        corner = Math.Min(corner, innerRadius * sinPhi / (1 - sinPhi));   // inner fillets
        corner = Math.Min(corner, outerRadius * sinPhi / (1 + sinPhi));   // outer fillets

        return corner > 0.5 ? corner : 0;
    }
}
