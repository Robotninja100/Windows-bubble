using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
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

    // ---- animation tuning ---------------------------------------------------
    private const double OpenStartScale = 0.30;
    private const double OpenTotalMs = 320;
    private const double MaxStaggerMs = 26;
    private const double StaggerWindowMs = 110;
    private const double MinPetalMs = 190;
    private const double HoverScaleFactor = 1.04;
    private const double HoverInMs = 130;
    private const double HoverOutMs = 150;
    private const double ShadowRestOpacity = 0.35;
    private const double ShadowHoverOpacity = 0.55;

    /// <summary>
    /// Give each petal its own drop shadow instead of one for the whole ring, so a
    /// lifted petal can cast a deeper shadow. Costs one effect per petal, and a
    /// neighbour's shadow now composites over adjacent glass — set false to fall
    /// back to a single shadow on the control (hover then only scales + brightens).
    /// </summary>
    private const bool PerPetalShadow = true;

    /// <summary>One radial segment and everything that has to move with it.</summary>
    private sealed class Petal
    {
        public required Canvas Host { get; init; }
        public required ScaleTransform HoverScale { get; init; }
        public required ScaleTransform OpenScale { get; init; }
        public required RotateTransform OpenTwist { get; init; }
        public DropShadowEffect? Shadow { get; init; }
        public Path? Highlight { get; set; }
    }

    private readonly List<Petal> _petals = new();

    /// <summary>Petal outlines, used to clip the desktop blur to the glass itself.</summary>
    private readonly List<Geometry> _blurShapes = new();

    private Canvas? _centerHost;
    private ScaleTransform? _centerScale;

    /// <summary>Bumped on every open/cancel so a stale Completed callback can be ignored.</summary>
    private int _openGeneration;

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

    /// <summary>
    /// Whether the open and hover animations actually run. Off by default so the
    /// settings live preview — which rebuilds on every slider tick — stays static;
    /// the overlay window turns it on from <see cref="StyleConfig.Animate"/>.
    /// </summary>
    public bool EnableAnimations { get; set; }

    /// <summary>Raised once the open animation has fully settled.</summary>
    public event Action? OpenAnimationCompleted;

    /// <summary>Diameter of the control (and the hosting window) in DIPs.</summary>
    public double Diameter => _outer * 2;

    public double OuterRadius => _outer;
    public double InnerRadius => _inner;

    /// <summary>Number of pending Claude Code sessions, shown as a badge on the inbox segment.</summary>
    public int InboxCount { get; set; }

    public void Build(AppConfig config)
    {
        Children.Clear();
        _petals.Clear();
        _blurShapes.Clear();
        _centerHost = null;
        _centerScale = null;
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
        // The highlight now stacks over the body rather than replacing it, so it
        // needs less alpha of its own to land at the same brightness.
        _highlightBrush = GlassFill(highlight, Math.Min(1.0, glassAlpha + 0.28));
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

            double midAngle = (a0 + a1) / 2.0;
            Geometry shape = BuildPetalGeometry(cx, cy, _inner, _outer, a0, a1, _corner);
            _blurShapes.Add(shape);

            // Everything for this segment goes into its own host so the open
            // unfurl and the hover lift can move it as one piece.
            Petal petal = CreatePetalHost(PointOnCircle(cx, cy, (_inner + _outer) / 2.0, midAngle));
            Children.Add(petal.Host);

            // 1. The body of the glass: thin, mostly transparent.
            petal.Host.Children.Add(new Path
            {
                Data = shape,
                Fill = _segmentBrush
            });

            // 1b. The highlight, stacked over the body and faded in on hover so
            //     the brighten is a cross-fade rather than a hard brush swap.
            petal.Highlight = new Path
            {
                Data = shape,
                Fill = _highlightBrush,
                Opacity = 0
            };
            petal.Host.Children.Add(petal.Highlight);

            // 2. Refracted edge: a wide stroke clipped to the shape leaves only
            //    its inner half, which is the band of light a thick piece of
            //    glass bends around its rim. Brightest at two opposite edges.
            petal.Host.Children.Add(new Path
            {
                Data = shape,
                Fill = null,
                Stroke = bleedBrush,
                StrokeThickness = edgeWidth * 2,
                Clip = shape
            });
            petal.Host.Children.Add(new Path
            {
                Data = shape,
                Fill = null,
                Stroke = edgeBrush,
                StrokeThickness = edgeWidth * 0.7,
                Clip = shape
            });

            // 3. Specular gloss across the upper part of each petal.
            petal.Host.Children.Add(new Path
            {
                Data = shape,
                Fill = sheenBrush
            });

            // 4. Crisp outline so the glass keeps a defined edge against the desktop.
            petal.Host.Children.Add(new Path
            {
                Data = shape,
                Fill = null,
                Stroke = strokeBrush,
                StrokeThickness = 1.0,
                StrokeLineJoin = PenLineJoin.Round
            });

            AddSegmentContent(petal.Host, config.Segments[i], cx, cy, midAngle);
            _petals.Add(petal);
        }

        AddCenterCancel(cx, cy);

        // With per-petal shadows each segment carries its own lift; otherwise one
        // soft shadow for the whole ring, as before. The const makes one branch
        // unreachable by design — it is the switch for backing the change out.
#pragma warning disable CS0162 // Unreachable code detected
        if (!PerPetalShadow)
        {
            Effect = new DropShadowEffect
            {
                BlurRadius = 18,
                ShadowDepth = 0,
                Opacity = ShadowRestOpacity,
                Color = Colors.Black
            };
        }
    }

    /// <summary>
    /// Build the container that holds one segment's layers, with the transforms the
    /// animations drive.
    ///
    /// It has to be a <see cref="Canvas"/>: the content panel and the badge position
    /// themselves with <c>Canvas.Left/Top</c>, which only a Canvas parent honours.
    /// It is placed at the origin at full size, so every coordinate inside it —
    /// geometry, clips, the absolutely-mapped gradients — is identical to drawing
    /// straight onto the control.
    /// </summary>
    private Petal CreatePetalHost(Point centroid)
    {
        // Order matters: TransformGroup applies Children[0] first, and the hover
        // lift must be innermost so its centre stays a constant in the petal's own
        // untransformed space. Outermost, that centre would have to chase the open
        // animation frame by frame and the two would fight.
        var hover = new ScaleTransform(1, 1) { CenterX = centroid.X, CenterY = centroid.Y };
        var openScale = new ScaleTransform(1, 1) { CenterX = _outer, CenterY = _outer };
        var openTwist = new RotateTransform(0) { CenterX = _outer, CenterY = _outer };

        var transforms = new TransformGroup();
        transforms.Children.Add(hover);
        transforms.Children.Add(openScale);
        transforms.Children.Add(openTwist);

        var host = new Canvas
        {
            Width = Diameter,
            Height = Diameter,
            IsHitTestVisible = false,
            RenderTransform = transforms
        };
        SetLeft(host, 0);
        SetTop(host, 0);

        DropShadowEffect? shadow = null;
        if (PerPetalShadow)
        {
            shadow = new DropShadowEffect
            {
                BlurRadius = 18,
                ShadowDepth = 0,
                Opacity = ShadowRestOpacity,
                Color = Colors.Black
            };
            host.Effect = shadow;
        }

        return new Petal
        {
            Host = host,
            HoverScale = hover,
            OpenScale = openScale,
            OpenTwist = openTwist,
            Shadow = shadow
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

        // The hub of the umbrella: it gets its own host so it can scale up with
        // the ring instead of sitting at full size while the petals are tiny.
        _centerScale = new ScaleTransform(1, 1) { CenterX = _outer, CenterY = _outer };
        _centerHost = new Canvas
        {
            Width = Diameter,
            Height = Diameter,
            IsHitTestVisible = false,
            RenderTransform = _centerScale
        };
        SetLeft(_centerHost, 0);
        SetTop(_centerHost, 0);
        _centerHost.Children.Add(stack);
        Children.Add(_centerHost);
    }

    private void AddSegmentContent(Canvas host, SegmentConfig segment, double cx, double cy, double midAngle)
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
        host.Children.Add(panel);

        if (segment.Action == ActionType.ClaudeInbox && InboxCount > 0)
            AddBadge(host, cx, cy, midAngle, InboxCount);
    }

    /// <summary>Draw a small count badge near the outer edge of a segment.</summary>
    private void AddBadge(Canvas host, double cx, double cy, double midAngle, int count)
    {
        Point p = PointOnCircle(cx, cy, _outer - 20, midAngle);
        double size = 24;

        var badge = new Grid { Width = size, Height = size };
        badge.Children.Add(new Ellipse
        {
            Fill = new SolidColorBrush(Color.FromRgb(0xE0, 0x3A, 0x3A)),
            Stroke = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)),
            StrokeThickness = 1.5
        });
        badge.Children.Add(new TextBlock
        {
            Text = count > 99 ? "99+" : count.ToString(CultureInfo.CurrentCulture),
            Foreground = Brushes.White,
            FontSize = count > 9 ? 10 : 12,
            FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        });

        SetLeft(badge, p.X - size / 2.0);
        SetTop(badge, p.Y - size / 2.0);
        host.Children.Add(badge);
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
        return RadialMath.SegmentAt(dx, dy, _count, _startAngle, _inner);
    }

    public void SetHighlight(int index)
    {
        // UpdateCursor calls this on every mouse move while the gesture is held,
        // so this early-out is what keeps the animations off the hot path.
        if (index == _highlighted)
            return;

        if (_highlighted >= 0 && _highlighted < _petals.Count)
            ApplyHighlight(_petals[_highlighted], false);

        _highlighted = index;

        if (_highlighted >= 0 && _highlighted < _petals.Count)
            ApplyHighlight(_petals[_highlighted], true);
    }

    /// <summary>Lift a petal towards the viewer and brighten it (or settle it back).</summary>
    private void ApplyHighlight(Petal petal, bool on)
    {
        if (!EnableAnimations)
        {
            petal.Highlight?.BeginAnimation(OpacityProperty, null);
            petal.HoverScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            petal.HoverScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            petal.Shadow?.BeginAnimation(DropShadowEffect.OpacityProperty, null);

            if (petal.Highlight is not null)
                petal.Highlight.Opacity = on ? 1 : 0;
            petal.HoverScale.ScaleX = petal.HoverScale.ScaleY = 1;
            if (petal.Shadow is not null)
                petal.Shadow.Opacity = ShadowRestOpacity;
            return;
        }

        double ms = on ? HoverInMs : HoverOutMs;

        // A spring on the way up, a plain settle on the way down.
        IEasingFunction ease = on
            ? (IEasingFunction)new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.5 }
            : new CubicEase { EasingMode = EasingMode.EaseOut };

        // Brighten slightly ahead of the lift so the colour leads the movement.
        petal.Highlight?.BeginAnimation(OpacityProperty,
            To(on ? 1 : 0, on ? 120 : 160, new QuadraticEase { EasingMode = EasingMode.EaseOut }));

        double scale = on ? HoverScaleFactor : 1.0;
        petal.HoverScale.BeginAnimation(ScaleTransform.ScaleXProperty, To(scale, ms, ease));
        petal.HoverScale.BeginAnimation(ScaleTransform.ScaleYProperty, To(scale, ms, ease));

        // Only the opacity: animating BlurRadius re-rasterizes the blur kernel
        // every frame, which is by far the most expensive thing here.
        petal.Shadow?.BeginAnimation(DropShadowEffect.OpacityProperty,
            To(on ? ShadowHoverOpacity : ShadowRestOpacity, ms, ease));
    }

    // ---- open animation -----------------------------------------------------

    /// <summary>
    /// Put every petal into its collapsed start state. This has to happen before
    /// the window is shown: an animation with a <c>BeginTime</c> renders the
    /// property's base value during its delay, so without this the later petals
    /// would flash at full size before their turn came round.
    /// </summary>
    public void PrepareOpenAnimation()
    {
        _openGeneration++;

        if (_petals.Count == 0)
            return;

        double twist = OpenTwistAngle();

        foreach (Petal petal in _petals)
        {
            ClearOpenClocks(petal);
            petal.Host.Opacity = 0;
            petal.OpenScale.ScaleX = petal.OpenScale.ScaleY = OpenStartScale;
            petal.OpenTwist.Angle = twist;
        }

        if (_centerHost is not null && _centerScale is not null)
        {
            _centerHost.BeginAnimation(OpacityProperty, null);
            _centerScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            _centerScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            _centerHost.Opacity = 0;
            _centerScale.ScaleX = _centerScale.ScaleY = 0.6;
        }
    }

    /// <summary>Spring the petals open one after another, like an umbrella catching.</summary>
    public void StartOpenAnimation()
    {
        if (_petals.Count == 0)
        {
            ResetToRest();
            OpenAnimationCompleted?.Invoke();
            return;
        }

        int generation = _openGeneration;
        int n = _petals.Count;

        // Keep the whole thing at roughly OpenTotalMs however many segments there are.
        double stagger = n > 1 ? Math.Min(MaxStaggerMs, StaggerWindowMs / (n - 1)) : 0;
        double petalMs = Math.Max(MinPetalMs, OpenTotalMs - stagger * (n - 1));
        double twist = OpenTwistAngle();

        var spring = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.4 };
        var settle = new CubicEase { EasingMode = EasingMode.EaseOut };
        var fade = new QuadraticEase { EasingMode = EasingMode.EaseOut };

        // The hub leads; the petals unfurl from it.
        if (_centerHost is not null && _centerScale is not null)
        {
            _centerHost.BeginAnimation(OpacityProperty, From(0, 1, petalMs * 0.55, fade));
            _centerScale.BeginAnimation(ScaleTransform.ScaleXProperty, From(0.6, 1, petalMs, spring));
            _centerScale.BeginAnimation(ScaleTransform.ScaleYProperty, From(0.6, 1, petalMs, spring));
        }

        for (int i = 0; i < n; i++)
        {
            Petal petal = _petals[i];
            double begin = i * stagger;

            petal.Host.BeginAnimation(OpacityProperty, From(0, 1, petalMs * 0.55, fade, begin));

            DoubleAnimation scaleX = From(OpenStartScale, 1, petalMs, spring, begin);
            if (i == n - 1)
                scaleX.Completed += (_, _) => SettleOpen(generation);

            petal.OpenScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleX);
            petal.OpenScale.BeginAnimation(ScaleTransform.ScaleYProperty, From(OpenStartScale, 1, petalMs, spring, begin));

            // A second overshooting curve on top of the spring reads wobbly, so
            // the twist just eases out.
            petal.OpenTwist.BeginAnimation(RotateTransform.AngleProperty, From(twist, 0, petalMs, settle, begin));
        }
    }

    /// <summary>Stop any open animation in flight and leave the bubble fully open.</summary>
    public void CancelOpenAnimation()
    {
        _openGeneration++;
        ResetToRest();
    }

    private void SettleOpen(int generation)
    {
        if (generation != _openGeneration)
            return; // a newer open (or a cancel) has already taken over

        ResetToRest();
        OpenAnimationCompleted?.Invoke();
    }

    /// <summary>
    /// Drop the open-animation clocks and write exact rest values. The exactness
    /// matters: WPF turns ClearType off under a non-identity transform, so the
    /// composed matrix has to end up exactly identity for the labels to stay crisp.
    /// </summary>
    private void ResetToRest()
    {
        foreach (Petal petal in _petals)
        {
            ClearOpenClocks(petal);
            petal.Host.Opacity = 1;
            petal.OpenScale.ScaleX = petal.OpenScale.ScaleY = 1;
            petal.OpenTwist.Angle = 0;
        }

        if (_centerHost is not null && _centerScale is not null)
        {
            _centerHost.BeginAnimation(OpacityProperty, null);
            _centerScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            _centerScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            _centerHost.Opacity = 1;
            _centerScale.ScaleX = _centerScale.ScaleY = 1;
        }
    }

    private static void ClearOpenClocks(Petal petal)
    {
        petal.Host.BeginAnimation(OpacityProperty, null);
        petal.OpenScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        petal.OpenScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        petal.OpenTwist.BeginAnimation(RotateTransform.AngleProperty, null);
    }

    /// <summary>How far back the petals are twisted before they unfurl.</summary>
    private double OpenTwistAngle()
        => _count > 0 ? -Math.Min(12.0, 360.0 / _count * 0.35) : 0;

    /// <summary>Animation towards a value, picking up whatever the property is at right now.</summary>
    private static DoubleAnimation To(double to, double ms, IEasingFunction? ease = null)
        => new(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = ease };

    private static DoubleAnimation From(double from, double to, double ms, IEasingFunction? ease, double beginMs = 0)
    {
        var animation = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = ease };
        if (beginMs > 0)
            animation.BeginTime = TimeSpan.FromMilliseconds(beginMs);
        return animation;
    }

    // ---- glass brushes ------------------------------------------------------

    /// <summary>
    /// Tint gradient for a petal. The gradient is mapped to the bubble's own
    /// coordinates rather than each petal's bounds, so the light falls across
    /// the whole ring in one direction instead of repeating per petal.
    /// </summary>
    private LinearGradientBrush GlassFill(Color tint, double alpha)
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
    private static LinearGradientBrush EdgeRefraction(double glassAlpha, double scale)
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
    private static RadialGradientBrush Sheen(double glassAlpha)
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
    private LinearGradientBrush RimStroke()
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

        corner = RadialMath.FittedCornerRadius(rIn, rOut, sweep, corner);
        if (corner <= 0)
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

    private static PathGeometry BuildSectorGeometry(double cx, double cy, double rIn, double rOut, double a0, double a1)
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

    private static double Degrees(double radians) => RadialMath.Degrees(radians);

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
