using CursorBubble.Overlay;
using Xunit;

namespace CursorBubble.Tests;

/// <summary>
/// The geometry and hit-testing behind the bubble. This is where the interaction
/// actually lives: pick the wrong segment here and the app runs the wrong action.
/// </summary>
public class RadialMathTests
{
    private const double Inner = 60;
    private const double Outer = 200;

    // ---- hit-testing --------------------------------------------------------

    [Fact]
    public void Centre_is_the_cancel_dead_zone()
    {
        Assert.Equal(-1, RadialMath.SegmentAt(0, 0, 6, 0, Inner));
        Assert.Equal(-1, RadialMath.SegmentAt(Inner - 1, 0, 6, 0, Inner));
    }

    [Fact]
    public void Just_outside_the_dead_zone_already_selects()
    {
        Assert.Equal(0, RadialMath.SegmentAt(0, -(Inner + 1), 6, 0, Inner));
    }

    [Fact]
    public void No_segments_means_no_selection()
    {
        Assert.Equal(-1, RadialMath.SegmentAt(0, -150, 0, 0, Inner));
    }

    [Theory]
    // Straight up is the start of segment 0; then clockwise, one per 60 degrees.
    [InlineData(0, 0)]
    [InlineData(59, 0)]
    [InlineData(61, 1)]
    [InlineData(180, 3)]
    [InlineData(359, 5)]
    public void Angle_maps_to_the_segment_it_points_at(double degrees, int expected)
    {
        (double dx, double dy) = Offset(degrees, 150);
        Assert.Equal(expected, RadialMath.SegmentAt(dx, dy, 6, 0, Inner));
    }

    [Fact]
    public void Selection_wraps_the_same_way_for_negative_and_positive_angles()
    {
        (double dx, double dy) = Offset(-30, 150);
        Assert.Equal(RadialMath.SegmentAt(dx, dy, 6, 0, Inner),
                     RadialMath.SegmentAt(Offset(330, 150).dx, Offset(330, 150).dy, 6, 0, Inner));
    }

    [Fact]
    public void Start_angle_rotates_the_whole_ring()
    {
        // With the ring rotated 30 degrees, straight up now lands in the last
        // segment rather than the first.
        (double dx, double dy) = Offset(0, 150);
        Assert.Equal(0, RadialMath.SegmentAt(dx, dy, 6, 0, Inner));
        Assert.Equal(5, RadialMath.SegmentAt(dx, dy, 6, 30, Inner));
    }

    [Fact]
    public void Aiming_far_past_the_ring_still_selects()
    {
        // A radial menu is aimed by direction — flicking well past the outer
        // edge must not fall back to "nothing selected".
        (double dx, double dy) = Offset(90, Outer * 10);
        Assert.Equal(1, RadialMath.SegmentAt(dx, dy, 6, 0, Inner));
    }

    [Fact]
    public void Index_never_escapes_the_segment_range()
    {
        for (int count = 1; count <= 12; count++)
        {
            for (double a = 0; a < 360; a += 0.5)
            {
                (double dx, double dy) = Offset(a, 150);
                int index = RadialMath.SegmentAt(dx, dy, count, 0, Inner);
                Assert.InRange(index, 0, count - 1);
            }
        }
    }

    [Fact]
    public void Every_segment_is_reachable()
    {
        for (int count = 1; count <= 12; count++)
        {
            var seen = new HashSet<int>();
            for (double a = 0; a < 360; a += 0.25)
            {
                (double dx, double dy) = Offset(a, 150);
                seen.Add(RadialMath.SegmentAt(dx, dy, count, 0, Inner));
            }
            Assert.Equal(count, seen.Count);
        }
    }

    // ---- corner rounding ----------------------------------------------------

    [Fact]
    public void Requested_radius_is_kept_when_it_fits()
    {
        // The shipped default: 7 segments, 5 degree gap.
        double sweep = 360.0 / 7 - 5;
        Assert.Equal(34, RadialMath.FittedCornerRadius(Inner, Outer, sweep, 34), precision: 6);
    }

    [Fact]
    public void Radius_shrinks_instead_of_snapping_back_to_sharp_corners()
    {
        // This is the regression that matters: adding segments used to drop the
        // rounding entirely. It must degrade smoothly instead.
        double previous = double.MaxValue;
        for (int count = 4; count <= 16; count++)
        {
            double sweep = 360.0 / count - 5;
            double corner = RadialMath.FittedCornerRadius(Inner, Outer, sweep, 34);
            Assert.True(corner > 0, $"{count} segments lost their rounding entirely");
            Assert.True(corner <= previous + 1e-9, $"{count} segments rounded more than {count - 1}");
            previous = corner;
        }
    }

    [Fact]
    public void Radius_never_exceeds_half_the_petal_width()
    {
        // Any more and the inner and outer fillets would overlap.
        double corner = RadialMath.FittedCornerRadius(100, 140, 60, 999);
        Assert.True(corner <= (140 - 100) / 2.0 + 1e-9, $"{corner} is wider than half the petal");
    }

    [Theory]
    [InlineData(0)]      // nothing asked for
    [InlineData(-5)]     // nonsense
    public void No_rounding_asked_for_means_none_given(double wanted)
    {
        Assert.Equal(0, RadialMath.FittedCornerRadius(Inner, Outer, 50, wanted));
    }

    [Fact]
    public void Degenerate_petals_fall_back_to_a_sharp_wedge()
    {
        Assert.Equal(0, RadialMath.FittedCornerRadius(0, Outer, 50, 20));      // no inner radius
        Assert.Equal(0, RadialMath.FittedCornerRadius(Inner, Outer, 1, 20));   // hairline sweep
        Assert.Equal(0, RadialMath.FittedCornerRadius(Inner, Inner, 50, 20));  // no width
    }

    [Fact]
    public void A_fitted_radius_always_leaves_room_for_both_fillets()
    {
        // sin(phi) = corner / rho, and the two fillets on an edge together must
        // stay inside the sweep — the invariant the caller depends on.
        for (int count = 3; count <= 16; count++)
        {
            double sweep = 360.0 / count - 5;
            double corner = RadialMath.FittedCornerRadius(Inner, Outer, sweep, 60);
            if (corner <= 0)
                continue;

            double innerPhi = RadialMath.Degrees(Math.Asin(corner / (Inner + corner)));
            double outerPhi = RadialMath.Degrees(Math.Asin(corner / (Outer - corner)));
            Assert.True(2 * Math.Max(innerPhi, outerPhi) < sweep,
                $"{count} segments: fillets span {2 * Math.Max(innerPhi, outerPhi):0.##} of a {sweep:0.##} sweep");
        }
    }

    // ---- angle helpers ------------------------------------------------------

    [Theory]
    [InlineData(0, -1, 0)]     // up
    [InlineData(1, 0, 90)]     // right
    [InlineData(0, 1, 180)]    // down
    [InlineData(-1, 0, 270)]   // left
    public void Angles_are_measured_clockwise_from_straight_up(double dx, double dy, double expected)
    {
        Assert.Equal(expected, RadialMath.ClockwiseAngleFromTop(dx, dy), precision: 6);
    }

    [Theory]
    [InlineData(-10, 360, 350)]
    [InlineData(370, 360, 10)]
    [InlineData(-370, 360, 350)]
    public void Modulo_wraps_negatives_into_range(double value, double m, double expected)
    {
        Assert.Equal(expected, RadialMath.Mod(value, m), precision: 6);
    }

    /// <summary>Cursor offset at an angle clockwise from straight up (y-down).</summary>
    private static (double dx, double dy) Offset(double degrees, double radius)
    {
        double rad = degrees * Math.PI / 180.0;
        return (radius * Math.Sin(rad), -radius * Math.Cos(rad));
    }
}
