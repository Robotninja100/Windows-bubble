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
/// Draws the radial "bubble": a ring of glass segments around a central dead
/// zone. Rebuilds itself from an <see cref="AppConfig"/>, exposes hit-testing
/// (cursor offset -> segment index) and highlighting of the active segment.
///
/// All measurements are in device-independent pixels; the hosting window scales
/// the cursor offset to DIPs before calling <see cref="HitTest"/>.
/// </summary>
public sealed class RadialMenuControl : Canvas
{
    /// <summary>Windows system icon font, with a fallback for older Windows 10.</summary>
    internal static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    private readonly List<Path> _segments = new();
    private StyleConfig _style = new();
    private int _count;
    private double _outer;
    private double _inner;
    private double _startAngle;
    private double _gap;

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
        _highlighted = -1;

        _style = config.Style;
        _outer = Math.Max(40, _style.OuterRadius);
        _inner = Math.Clamp(_style.InnerRadius, 0, _outer - 10);
        _startAngle = _style.StartAngle;
        _gap = Math.Clamp(_style.SegmentGap, 0, 20);
        _count = config.Segments.Count;

        Width = Diameter;
        Height = Diameter;

        Color tint = ParseColor(_style.TintColor, Color.FromRgb(0xFF, 0xFF, 0xFF));
        Color highlight = ParseColor(_style.HighlightColor, Color.FromRgb(0xEA, 0xF2, 0xFF));
        Color label = ParseColor(_style.LabelColor, Color.FromRgb(0x20, 0x24, 0x2C));
        _labelBrush = new SolidColorBrush(label);
        _labelBrush.Freeze();

        // When acrylic blur is on, the backdrop supplies the tint, so the WPF
        // fill only adds a faint sheen. Without blur, the fill carries the tint.
        double segAlpha = _style.UseAcrylicBlur
            ? Math.Min(0.18, _style.SegmentOpacity)
            : _style.SegmentOpacity;

        _segmentBrush = new SolidColorBrush(WithAlpha(tint, segAlpha));
        _segmentBrush.Freeze();
        _highlightBrush = new SolidColorBrush(WithAlpha(highlight, 0.65));
        _highlightBrush.Freeze();

        var strokeBrush = new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF));
        strokeBrush.Freeze();

        double cx = _outer, cy = _outer;

        // Base frosted disc: the tint sits over the (blurred) desktop, and it
        // keeps the bubble a translucent glass circle even when blur is off.
        double baseAlpha = _style.UseAcrylicBlur ? Math.Min(0.16, _style.TintOpacity) : _style.TintOpacity;
        var baseFill = new SolidColorBrush(WithAlpha(tint, baseAlpha));
        baseFill.Freeze();
        var baseCircle = new Ellipse
        {
            Width = Diameter,
            Height = Diameter,
            Fill = baseFill,
            Stroke = new SolidColorBrush(Color.FromArgb(0x59, 0xFF, 0xFF, 0xFF)),
            StrokeThickness = 1.5
        };
        SetLeft(baseCircle, 0);
        SetTop(baseCircle, 0);
        Children.Add(baseCircle);

        if (_count == 0)
        {
            DrawEmptyHint(cx, cy);
            return;
        }

        double sweep = 360.0 / _count;
        // Split the angular gap so each wedge is inset by half a gap on each side.
        double halfGap = _count > 1 ? _gap / 2.0 : 0;

        for (int i = 0; i < _count; i++)
        {
            double a0 = _startAngle + i * sweep + halfGap;
            double a1 = _startAngle + (i + 1) * sweep - halfGap;

            var path = new Path
            {
                Data = BuildSectorGeometry(cx, cy, _inner, _outer, a0, a1),
                Fill = _segmentBrush,
                Stroke = strokeBrush,
                StrokeThickness = 1.2,
                StrokeLineJoin = PenLineJoin.Round,
                SnapsToDevicePixels = true
            };
            _segments.Add(path);
            Children.Add(path);

            AddSegmentContent(config.Segments[i], cx, cy, (a0 + a1) / 2.0);
        }

        AddCenterCancel(cx, cy);

        // Soft drop shadow gives the glass some depth against the desktop.
        Effect = new DropShadowEffect
        {
            BlurRadius = 24,
            ShadowDepth = 0,
            Opacity = 0.5,
            Color = Colors.Black
        };
    }

    /// <summary>Draw the central "cancel" affordance (an X above a label) in the dead zone.</summary>
    private void AddCenterCancel(double cx, double cy)
    {
        if (_inner < 24)
            return;

        double s = Math.Min(18, _inner * 0.35);
        var cross = new System.Windows.Shapes.Path
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
            Text = "Annuleren",
            Foreground = _labelBrush,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center
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
                Margin = new Thickness(0, 0, 0, 4),
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
                Margin = new Thickness(0, 0, 0, 4),
                HorizontalAlignment = HorizontalAlignment.Center,
                Effect = new DropShadowEffect { BlurRadius = 6, ShadowDepth = 0, Color = Colors.White, Opacity = 0.6 }
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
            // Soft light glow keeps dark labels readable on the frosted glass.
            Effect = new DropShadowEffect { BlurRadius = 6, ShadowDepth = 0, Color = Colors.White, Opacity = 0.6 }
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

    private void DrawEmptyHint(double cx, double cy)
    {
        var ring = new Path
        {
            Data = BuildSectorGeometry(cx, cy, _inner, _outer, 0, 359.999),
            Fill = _segmentBrush,
            Stroke = new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)),
            StrokeThickness = 1.0
        };
        Children.Add(ring);

        var hint = new TextBlock
        {
            Text = "Geen shortcuts\ningesteld",
            Foreground = _labelBrush,
            FontSize = 13,
            TextAlignment = TextAlignment.Center
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

    // ---- geometry helpers ---------------------------------------------------

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
