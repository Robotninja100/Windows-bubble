using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using CursorBubble.Config;
// System.IO is used for File.Exists; alias Path so it stays the WPF shape here.
using Path = System.Windows.Shapes.Path;

namespace CursorBubble.Overlay;

/// <summary>
/// Draws the radial "bubble": a ring of rounded glass petals around a central
/// dead zone. Rebuilds itself from an <see cref="AppConfig"/>, exposes
/// hit-testing (cursor offset -> segment index) and highlighting of the active
/// segment.
///
/// The "liquid glass" look is stacked per petal: a thin tinted body (lit
/// diagonally across the whole bubble, so the petals read as one piece of
/// glass), two clipped edge bands that fake light refracting around the rim, a
/// specular gloss, and a crisp outline — plus one shared drop shadow that lifts
/// the ring off the desktop. The desktop blur behind it is clipped to these
/// same petal outlines, so only the glass frosts what is underneath it.
///
/// All measurements are in device-independent pixels; the hosting window scales
/// the cursor offset to DIPs before calling <see cref="HitTest"/>.
/// </summary>
public sealed class RadialMenuControl : Canvas
{
    /// <summary>Windows system icon font, with a fallback for older Windows 10.</summary>
    internal static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    private readonly List<Path> _segments = new();

    /// <summary>Petal outlines, used to clip the desktop blur to the glass itself.</summary>
    private readonly List<Geometry> _blurShapes = new();

    private StyleConfig _style = new();
    private int _count;
    private double _outer;
    private double _inner;
    private double _startAngle;
    private double _gap;
    private double _corner;

    private Brush _segmentBrush = Brushes.Transparent;
    private Brush _highlightBrush = Brushes.Transparent;
    private Brush _labelBrush = Brushes.White;

    private int _highlighted = -1;

    /// <summary>Diameter of the control (and the hosting window) in DIPs.</summary>
    public double Diameter => _outer * 2;

    public double OuterRadius => _outer;
    public double InnerRadius => _inner;

    /// <summary>Number of pending Claude Code sessions, shown as a badge on the inbox segment.</summary>
    public int InboxCount { get; set; }

    public void Build(AppConfig config)
    {
        Children.Clear();
        _segments.Clear();
        _blurShapes.Clear();
        _highlighted = -1;
        Effect = null;

        _style = config.Style;
        _outer = Math.Max(40, _style.OuterRadius);
        _inner = Math.Clamp(_style.InnerRadius, 0, _outer - 10);
        _startAngle = _style.StartAngle;
        _gap = Math.Clamp(_style.SegmentGap, 0, 20);
        _count = config.Segments.Count;

        // The fillet can never eat more than half the petal's width.
        _corner = Math.Clamp(_style.SegmentCornerRadius, 0, (_outer - _inner) / 2.0);

        Width = Diameter;
        Height = Diameter;

        Color tint = ParseColor(_style.TintColor, Color.FromRgb(0xFF, 0xFF, 0xFF));
        Color highlight = ParseColor(_style.HighlightColor, Color.FromRgb(0xEA, 0xF2, 0xFF));
        Color label = ParseColor(_style.LabelColor, Color.FromRgb(0x20, 0x24, 0x2C));
        _labelBrush = new SolidColorBrush(label);
        _labelBrush.Freeze();

        // With acrylic on, the blurred desktop already carries most of the
        // frost, so the body stays thin and the rim does the work.
        double glassAlpha = Math.Clamp(_style.TintOpacity, 0, 1);
        if (_style.UseAcrylicBlur)
            glassAlpha *= 0.38;

        _segmentBrush = GlassFill(tint, glassAlpha);
        _highlightBrush = GlassFill(highlight, Math.Min(1.0, glassAlpha + 0.40));
        Brush strokeBrush = RimStroke();
        Brush bleedBrush = EdgeRefraction(glassAlpha, 0.42);
        Brush edgeBrush = EdgeRefraction(glassAlpha, 1.0);
        Brush sheenBrush = Sheen(glassAlpha);

        // The refracted edge scales with the petal, so the glass reads as the
        // same material at every radius.
        double edgeWidth = Math.Clamp((_outer - _inner) * 0.07, 2.0, 9.0);

        double cx = _outer, cy = _outer;

        if (_count == 0)
        {
            DrawEmptyHint(cx, cy, strokeBrush);
            return;
        }

        double sweep = 360.0 / _count;
        // Split the angular gap so each wedge is inset by half a gap on each side.
        double halfGap = _count > 1 ? _gap / 2.0 : 0;

        for (int i = 0; i < _count; i++)
        {
            double a0 = _startAngle + i * sweep + halfGap;
            double a1 = _startAngle + (i + 1) * sweep - halfGap;

            Geometry shape = BuildPetalGeometry(cx, cy, _inner, _outer, a0, a1, _corner);
            _blurShapes.Add(shape);

            // 1. The body of the glass: thin, mostly transparent.
            var body = new Path
            {
                Data = shape,
                Fill = _segmentBrush
            };
            _segments.Add(body);
            Children.Add(body);

            // 2. Refracted edge: a wide stroke clipped to the shape leaves only
            //    its inner half, which is the band of light a thick piece of
            //    glass bends around its rim. Brightest at two opposite edges.
            Children.Add(new Path
            {
                Data = shape,
                Fill = null,
                Stroke = bleedBrush,
                StrokeThickness = edgeWidth * 2,
                Clip = shape
            });
            Children.Add(new Path
            {
                Data = shape,
                Fill = null,
                Stroke = edgeBrush,
                StrokeThickness = edgeWidth * 0.7,
                Clip = shape
            });

            // 3. Specular gloss across the upper part of each petal.
            Children.Add(new Path
            {
                Data = shape,
                Fill = sheenBrush
            });

            // 4. Crisp outline so the glass keeps a defined edge against the desktop.
            Children.Add(new Path
            {
                Data = shape,
                Fill = null,
                Stroke = strokeBrush,
                StrokeThickness = 1.0,
                StrokeLineJoin = PenLineJoin.Round
            });

            AddSegmentContent(config.Segments[i], cx, cy, (a0 + a1) / 2.0);
        }

        AddCenterCancel(cx, cy);

        // One soft shadow for the whole ring: each petal picks up its own edge,
        // which is what gives the glass its lift off the desktop.
        Effect = new DropShadowEffect
        {
            BlurRadius = 18,
            ShadowDepth = 0,
            Opacity = 0.35,
            Color = Colors.Black
        };
    }

    /// <summary>
    /// The petal outlines in physical pixels, for clipping the desktop blur to
    /// the glass. Returns an empty list when there is nothing to blur.
    /// </summary>
    public List<Point[]> BuildBlurPolygons(double scale)
    {
        var result = new List<Point[]>();

        foreach (Geometry shape in _blurShapes)
        {
            // A flattened outline is what GDI regions need; 0.25 DIP is well
            // below what is visible and keeps the point count modest.
            PathGeometry flat = shape.GetFlattenedPathGeometry(0.25, ToleranceType.Absolute);

            foreach (PathFigure figure in flat.Figures)
            {
                var points = new List<Point> { figure.StartPoint };
                foreach (PathSegment segment in figure.Segments)
                {
                    switch (segment)
                    {
                        case PolyLineSegment poly:
                            points.AddRange(poly.Points);
                            break;
                        case LineSegment line:
                            points.Add(line.Point);
                            break;
                    }
                }

                if (points.Count < 3)
                    continue;

                var scaled = new Point[points.Count];
                for (int i = 0; i < points.Count; i++)
                    scaled[i] = new Point(points[i].X * scale, points[i].Y * scale);
                result.Add(scaled);
            }
        }

        return result;
    }

    /// <summary>Draw the central "cancel" affordance (an X above a label) in the dead zone.</summary>
    private void AddCenterCancel(double cx, double cy)
    {
        if (_inner < 24)
            return;

        double s = Math.Min(18, _inner * 0.35);
        var cross = new Path
        {
            Data = Geometry.Parse(
                $"M {-s},{-s} L {s},{s} M {-s},{s} L {s},{-s}"),
            Stroke = _labelBrush,
            StrokeThickness = 2.4,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round
        };

        var stack = new StackPanel { Orientation = Orientation.Vertical, HorizontalAlignment = HorizontalAlignment.Center };
        var crossHost = new Grid { Width = s * 2, Height = s * 2, Margin = new Thickness(0, 0, 0, 4) };
        crossHost.Children.Add(cross);
        // Centre the cross geometry inside its host.
        cross.RenderTransform = new TranslateTransform(s, s);
        stack.Children.Add(crossHost);
        stack.Children.Add(new TextBlock
        {
            Text = "Cancel",
            Foreground = _labelBrush,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            Effect = TextGlow()
        });

        stack.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Size d = stack.DesiredSize;
        SetLeft(stack, cx - d.Width / 2.0);
        SetTop(stack, cy - d.Height / 2.0);
        Children.Add(stack);
    }

    private void AddSegmentContent(SegmentConfig segment, double cx, double cy, double midAngle)
    {
        double labelRadius = (_inner + _outer) / 2.0;
        Point p = PointOnCircle(cx, cy, labelRadius, midAngle);

        var panel = new StackPanel
        {
            Orientation = Orientation.Vertical,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        BitmapImage? icon = TryLoadIcon(segment.IconPath);
        if (icon is not null)
        {
            panel.Children.Add(new Image
            {
                Source = icon,
                Width = 28,
                Height = 28,
                Margin = new Thickness(0, 0, 0, 6),
                HorizontalAlignment = HorizontalAlignment.Center
            });
        }
        else if (!string.IsNullOrWhiteSpace(segment.Glyph))
        {
            // Built-in vector icon from the Windows system icon font.
            panel.Children.Add(new TextBlock
            {
                Text = segment.Glyph,
                FontFamily = IconFont,
                FontSize = 26,
                Foreground = _labelBrush,
                Margin = new Thickness(0, 0, 0, 6),
                HorizontalAlignment = HorizontalAlignment.Center,
                Effect = TextGlow()
            });
        }

        panel.Children.Add(new TextBlock
        {
            Text = segment.Label,
            Foreground = _labelBrush,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = Math.Max(56, (_outer - _inner) * 1.4),
            Effect = TextGlow()
        });

        // Measure so we can centre the panel on the label anchor point.
        panel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Size desired = panel.DesiredSize;
        SetLeft(panel, p.X - desired.Width / 2.0);
        SetTop(panel, p.Y - desired.Height / 2.0);
        Children.Add(panel);

        if (segment.Action == ActionType.ClaudeInbox && InboxCount > 0)
            AddBadge(cx, cy, midAngle, InboxCount);
    }

    /// <summary>Draw a small count badge near the outer edge of a segment.</summary>
    private void AddBadge(double cx, double cy, double midAngle, int count)
    {
        Point p = PointOnCircle(cx, cy, _outer - 20, midAngle);
        double size = 24;

        var host = new Grid { Width = size, Height = size };
        host.Children.Add(new Ellipse
        {
            Fill = new SolidColorBrush(Color.FromRgb(0xE0, 0x3A, 0x3A)),
            Stroke = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)),
            StrokeThickness = 1.5
        });
        host.Children.Add(new TextBlock
        {
            Text = count > 99 ? "99+" : count.ToString(),
            Foreground = Brushes.White,
            FontSize = count > 9 ? 10 : 12,
            FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        });

        SetLeft(host, p.X - size / 2.0);
        SetTop(host, p.Y - size / 2.0);
        Children.Add(host);
    }

    private void DrawEmptyHint(double cx, double cy, Brush strokeBrush)
    {
        Geometry ringShape = BuildPetalGeometry(cx, cy, _inner, _outer, 0, 359.999, 0);
        _blurShapes.Add(ringShape);

        Children.Add(new Path
        {
            Data = ringShape,
            Fill = _segmentBrush,
            Stroke = strokeBrush,
            StrokeThickness = 1.0
        });

        var hint = new TextBlock
        {
            Text = "No shortcuts\nconfigured",
            Foreground = _labelBrush,
            FontSize = 13,
            TextAlignment = TextAlignment.Center,
            Effect = TextGlow()
        };
        hint.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        SetLeft(hint, cx - hint.DesiredSize.Width / 2.0);
        SetTop(hint, cy - hint.DesiredSize.Height / 2.0);
        Children.Add(hint);
    }

    /// <summary>
    /// Map a cursor offset from the centre (in DIPs, y-down) to a segment index.
    /// Returns -1 for the central dead zone (cancel) or when there are no segments.
    /// </summary>
    public int HitTest(double dx, double dy)
    {
        if (_count == 0)
            return -1;

        double dist = Math.Sqrt(dx * dx + dy * dy);
        if (dist < _inner)
            return -1; // dead zone = cancel

        double angle = ClockwiseAngleFromTop(dx, dy);
        double rel = Mod(angle - _startAngle, 360);
        double sweep = 360.0 / _count;
        int index = (int)(rel / sweep);
        return Math.Clamp(index, 0, _count - 1);
    }

    public void SetHighlight(int index)
    {
        if (index == _highlighted)
            return;

        if (_highlighted >= 0 && _highlighted < _segments.Count)
            _segments[_highlighted].Fill = _segmentBrush;

        _highlighted = index;

        if (_highlighted >= 0 && _highlighted < _segments.Count)
            _segments[_highlighted].Fill = _highlightBrush;
    }

    // ---- glass brushes ------------------------------------------------------

    /// <summary>
    /// Tint gradient for a petal. The gradient is mapped to the bubble's own
    /// coordinates rather than each petal's bounds, so the light falls across
    /// the whole ring in one direction instead of repeating per petal.
    /// </summary>
    private Brush GlassFill(Color tint, double alpha)
    {
        var brush = new LinearGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute,
            StartPoint = new Point(0, 0),
            EndPoint = new Point(Diameter, Diameter)
        };
        brush.GradientStops.Add(new GradientStop(WithAlpha(tint, Math.Min(1.0, alpha * 1.45)), 0));
        brush.GradientStops.Add(new GradientStop(WithAlpha(tint, alpha), 0.55));
        brush.GradientStops.Add(new GradientStop(WithAlpha(tint, alpha * 0.6), 1));
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// The band of light that a thick piece of glass bends around its own rim.
    /// Mapped to each petal's own bounds (not the whole bubble) and bright at
    /// both ends of the gradient, so every petal picks up a highlight on two
    /// opposite edges with a clear, transparent middle — the thing that reads as
    /// "liquid glass" rather than as a flat translucent panel.
    ///
    /// Drawn twice at different widths: a wide, faint band for the light that
    /// bleeds into the body, and a narrow bright one right at the edge. Two
    /// stacked bands fake a falloff with distance from the rim, which a single
    /// stroke cannot do — its gradient runs along the petal, not across the edge.
    /// </summary>
    private static Brush EdgeRefraction(double glassAlpha, double scale)
    {
        double peak = Math.Clamp(0.40 + glassAlpha * 1.1, 0.3, 0.95) * scale;

        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0.15, 0),
            EndPoint = new Point(0.85, 1)
        };
        brush.GradientStops.Add(new GradientStop(WithAlpha(Colors.White, peak), 0));
        brush.GradientStops.Add(new GradientStop(WithAlpha(Colors.White, peak * 0.22), 0.42));
        brush.GradientStops.Add(new GradientStop(WithAlpha(Colors.White, peak * 0.20), 0.60));
        brush.GradientStops.Add(new GradientStop(WithAlpha(Colors.White, peak * 0.82), 1));
        brush.Freeze();
        return brush;
    }

    /// <summary>Specular gloss over the upper part of each petal.</summary>
    private static Brush Sheen(double glassAlpha)
    {
        var brush = new RadialGradientBrush
        {
            Center = new Point(0.34, 0.14),
            GradientOrigin = new Point(0.34, 0.14),
            RadiusX = 0.80,
            RadiusY = 0.62
        };
        brush.GradientStops.Add(new GradientStop(WithAlpha(Colors.White, Math.Clamp(0.10 + glassAlpha * 0.55, 0.1, 0.45)), 0));
        brush.GradientStops.Add(new GradientStop(WithAlpha(Colors.White, 0.06), 0.55));
        brush.GradientStops.Add(new GradientStop(WithAlpha(Colors.White, 0.0), 1));
        brush.Freeze();
        return brush;
    }

    /// <summary>Rim light: a bright top-left edge fading to almost nothing bottom-right.</summary>
    private Brush RimStroke()
    {
        var brush = new LinearGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute,
            StartPoint = new Point(0, 0),
            EndPoint = new Point(Diameter, Diameter)
        };
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x9E, 0xFF, 0xFF, 0xFF), 0));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF), 0.5));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF), 1));
        brush.Freeze();
        return brush;
    }

    /// <summary>Soft light halo that keeps dark labels readable on the frosted glass.</summary>
    private static DropShadowEffect TextGlow()
        => new() { BlurRadius = 6, ShadowDepth = 0, Color = Colors.White, Opacity = 0.6 };

    // ---- geometry helpers ---------------------------------------------------

    /// <summary>
    /// Build one petal: an annular wedge whose four corners are rounded by
    /// <paramref name="corner"/>. Falls back to sharp corners when the wedge is
    /// too small (or too narrow) for the fillets to fit.
    ///
    /// Each fillet is a true tangent arc. Its centre sits on a circle of radius
    /// <c>rho</c> (rOut - corner outside, rIn + corner inside) and is rotated
    /// away from the radial edge by <c>phi</c>, where <c>sin(phi) = corner / rho</c>
    /// puts the centre exactly <c>corner</c> away from that edge. The arc then runs
    /// between the two tangent points: one on the ring edge at angle a ± phi, one
    /// on the radial edge at radius <c>rho * cos(phi)</c>. Approximating phi as
    /// <c>corner / r</c> is close enough on the outer edge but visibly wrong on the
    /// much tighter inner one, where it leaves a notch instead of a fillet.
    /// </summary>
    private static Geometry BuildPetalGeometry(
        double cx, double cy, double rIn, double rOut, double a0, double a1, double corner)
    {
        double sweep = a1 - a0;

        if (rIn <= 0.5 || sweep <= 2.5)
            return BuildSectorGeometry(cx, cy, rIn, rOut, a0, a1);

        // Shrink the fillet until it fits rather than dropping back to sharp
        // corners: the two fillets on one edge must not meet, so each may span
        // at most half the sweep (less a sliver of straight edge). Inverting
        // sin(phi) = corner / rho for the phi budget gives the ceilings below.
        double maxPhi = (sweep - 2.0) / 2.0 * Math.PI / 180.0;
        double sinPhi = Math.Sin(Math.Min(maxPhi, Math.PI / 2 - 1e-6));

        corner = Math.Min(corner, (rOut - rIn) / 2.0);
        corner = Math.Min(corner, rIn * sinPhi / (1 - sinPhi));   // inner fillets
        corner = Math.Min(corner, rOut * sinPhi / (1 + sinPhi));  // outer fillets

        if (corner <= 0.5)
            return BuildSectorGeometry(cx, cy, rIn, rOut, a0, a1);

        double outerRho = rOut - corner;
        double innerRho = rIn + corner;

        double outerPhiRad = Math.Asin(Math.Clamp(corner / outerRho, 0, 1));
        double innerPhiRad = Math.Asin(Math.Clamp(corner / innerRho, 0, 1));
        double outerPhi = Degrees(outerPhiRad);
        double innerPhi = Degrees(innerPhiRad);

        double outerTangentRadius = outerRho * Math.Cos(outerPhiRad);
        double innerTangentRadius = innerRho * Math.Cos(innerPhiRad);

        // Belt and braces: if rounding still leaves no straight radial stretch,
        // or the fillets would overlap, draw the plain wedge.
        if (sweep <= 2 * Math.Max(outerPhi, innerPhi) + 0.5 ||
            outerTangentRadius <= innerTangentRadius)
        {
            return BuildSectorGeometry(cx, cy, rIn, rOut, a0, a1);
        }

        var figure = new PathFigure
        {
            StartPoint = PointOnCircle(cx, cy, rOut, a0 + outerPhi),
            IsClosed = true,
            IsFilled = true
        };

        var size = new Size(corner, corner);

        // Outer edge, then round into the trailing radial edge.
        figure.Segments.Add(new ArcSegment(
            PointOnCircle(cx, cy, rOut, a1 - outerPhi), new Size(rOut, rOut),
            0, (sweep - 2 * outerPhi) > 180.0, SweepDirection.Clockwise, true));
        figure.Segments.Add(new ArcSegment(
            PointOnCircle(cx, cy, outerTangentRadius, a1), size,
            0, false, SweepDirection.Clockwise, true));

        // Trailing radial edge inwards, then round onto the inner arc.
        figure.Segments.Add(new LineSegment(PointOnCircle(cx, cy, innerTangentRadius, a1), true));
        figure.Segments.Add(new ArcSegment(
            PointOnCircle(cx, cy, rIn, a1 - innerPhi), size,
            0, false, SweepDirection.Clockwise, true));

        // Inner edge back the other way, then round onto the leading radial edge.
        figure.Segments.Add(new ArcSegment(
            PointOnCircle(cx, cy, rIn, a0 + innerPhi), new Size(rIn, rIn),
            0, (sweep - 2 * innerPhi) > 180.0, SweepDirection.Counterclockwise, true));
        figure.Segments.Add(new ArcSegment(
            PointOnCircle(cx, cy, innerTangentRadius, a0), size,
            0, false, SweepDirection.Clockwise, true));

        // Leading radial edge outwards, then round back onto the outer arc.
        figure.Segments.Add(new LineSegment(PointOnCircle(cx, cy, outerTangentRadius, a0), true));
        figure.Segments.Add(new ArcSegment(
            PointOnCircle(cx, cy, rOut, a0 + outerPhi), size,
            0, false, SweepDirection.Clockwise, true));

        var geo = new PathGeometry();
        geo.Figures.Add(figure);
        geo.Freeze();
        return geo;
    }

    private static Geometry BuildSectorGeometry(double cx, double cy, double rIn, double rOut, double a0, double a1)
    {
        Point outerStart = PointOnCircle(cx, cy, rOut, a0);
        Point outerEnd = PointOnCircle(cx, cy, rOut, a1);
        Point innerEnd = PointOnCircle(cx, cy, rIn, a1);
        Point innerStart = PointOnCircle(cx, cy, rIn, a0);

        bool largeArc = (a1 - a0) > 180.0;

        var figure = new PathFigure { StartPoint = outerStart, IsClosed = true, IsFilled = true };
        figure.Segments.Add(new ArcSegment(outerEnd, new Size(rOut, rOut), 0, largeArc, SweepDirection.Clockwise, true));
        figure.Segments.Add(new LineSegment(innerEnd, true));
        if (rIn > 0.5)
        {
            figure.Segments.Add(new ArcSegment(innerStart, new Size(rIn, rIn), 0, largeArc, SweepDirection.Counterclockwise, true));
        }
        else
        {
            figure.Segments.Add(new LineSegment(new Point(cx, cy), true));
        }

        var geo = new PathGeometry();
        geo.Figures.Add(figure);
        geo.Freeze();
        return geo;
    }

    private static Point PointOnCircle(double cx, double cy, double r, double angleDegClockwiseFromTop)
    {
        double rad = angleDegClockwiseFromTop * Math.PI / 180.0;
        return new Point(cx + r * Math.Sin(rad), cy - r * Math.Cos(rad));
    }

    private static double Degrees(double radians) => radians * 180.0 / Math.PI;

    private static double ClockwiseAngleFromTop(double dx, double dy)
        => Mod(Math.Atan2(dx, -dy) * 180.0 / Math.PI, 360);

    private static double Mod(double value, double m) => ((value % m) + m) % m;

    private static BitmapImage? TryLoadIcon(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        try
        {
            string full = Environment.ExpandEnvironmentVariables(path);
            if (!File.Exists(full))
                return null;
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(full);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    private static Color ParseColor(string hex, Color fallback)
    {
        try
        {
            if (ColorConverter.ConvertFromString(hex) is Color c)
                return c;
        }
        catch
        {
            // ignore
        }
        return fallback;
    }

    private static Color WithAlpha(Color c, double alpha)
        => Color.FromArgb((byte)Math.Round(Math.Clamp(alpha, 0, 1) * 255), c.R, c.G, c.B);
}
