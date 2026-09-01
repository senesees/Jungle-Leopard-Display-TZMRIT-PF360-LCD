using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

using JLDisplayManager.Models.Overlay;
using JLDisplayManager.Services.Sensors;

namespace JLDisplayManager.Services.Overlay;

/// <summary>
/// What the renderer needs to know beyond the layers and the sensors.
/// </summary>
public sealed class LayerContext
{
    /// <summary>Drives the WhilePlaying / WhileIdle visibility rules.</summary>
    public bool IsPlaying { get; set; }

    /// <summary>Where <see cref="ImageLayer.File"/> is resolved from.</summary>
    public string AssetDirectory { get; set; } = "";

    /// <summary>
    /// The design surface, in the orientation the viewer actually sees — 960x480
    /// normally, 480x960 when the panel is mounted at 90 or 270 degrees. Layers
    /// are positioned and anchored against this, not against the panel's native
    /// buffer, because a profile is designed against what is on the glass.
    /// </summary>
    public double Width { get; set; } = OverlayRenderer.PanelWidth;

    public double Height { get; set; } = OverlayRenderer.PanelHeight;

    /// <summary>
    /// The look every colour role resolves through. Never null in practice —
    /// <see cref="OverlayRenderer.DrawProfile"/> fills it from the profile — but
    /// nullable so a caller drawing one layer in isolation need not care.
    /// </summary>
    public OverlayTheme? Theme { get; set; }
}

/// <summary>
/// Draws overlay layers.
///
/// This is deliberately the ONLY place that knows what a layer looks like. The
/// render thread calls it to produce the 960x480 surface that goes to the panel,
/// and the editor calls it to paint its design canvas. One implementation means
/// WYSIWYG is structural rather than two pieces of code kept in step by hand —
/// which, on past form, they would not be.
/// </summary>
public static class OverlayRenderer
{
    /// <summary>The panel's native buffer. Always this, whatever the mounting.</summary>
    public const int PanelWidth = 960;

    public const int PanelHeight = 480;

    /// <summary>
    /// The design surface for a given mounting rotation.
    ///
    /// The pump head turns on its magnet, and the rotation setting makes ffmpeg
    /// pre-rotate the video so the physical turn cancels it. At 90 or 270 that
    /// swaps what the viewer sees: a 960x480 buffer read sideways is a 480x960
    /// picture, and a profile has to be designed against the picture.
    /// </summary>
    public static (double Width, double Height) DesignSize(int rotate) =>
        ((rotate % 360 + 360) % 360) is 90 or 270
            ? (PanelHeight, PanelWidth)
            : (PanelWidth, PanelHeight);

    /// <summary>
    /// Turns a <paramref name="w"/> x <paramref name="h"/> surface clockwise by
    /// <paramref name="degrees"/>, translated so the result starts at the
    /// origin. At 90 and 270 the output is h x w. Null when there is nothing to
    /// do, which callers treat as the identity.
    /// </summary>
    public static Transform? RotateInto(double w, double h, int degrees)
    {
        var group = new TransformGroup();

        switch (((degrees % 360) + 360) % 360)
        {
            case 90:
                // The source's top-left lands at the result's top-right.
                group.Children.Add(new RotateTransform(90));
                group.Children.Add(new TranslateTransform(h, 0));
                break;

            case 180:
                group.Children.Add(new RotateTransform(180));
                group.Children.Add(new TranslateTransform(w, h));
                break;

            case 270:
                group.Children.Add(new RotateTransform(270));
                group.Children.Add(new TranslateTransform(0, w));
                break;

            default:
                return null;
        }

        group.Freeze();
        return group;
    }

    /// <summary>
    /// Maps the design surface onto the panel's buffer, so the overlay turns
    /// with the video instead of staying square to the buffer.
    /// </summary>
    public static Transform? RotationTransform(int rotate)
    {
        (double w, double h) = DesignSize(rotate);
        return RotateInto(w, h, rotate);
    }

    /// <summary>
    /// The other direction: maps the panel's buffer back into design space.
    ///
    /// The live preview is a copy of the buffer, so on a rotated mounting it
    /// arrives upside down or on its side. The editor has to undo that before
    /// showing it, or the design surface is the one place the picture is wrong —
    /// and nobody wants to lay out a profile upside down.
    /// </summary>
    public static Transform? UnrotateBackdrop(int rotate) =>
        RotateInto(PanelWidth, PanelHeight, -rotate);

    // Frozen and shared: decoding a logo on every frame would dwarf everything
    // else the renderer does.
    private static readonly ConcurrentDictionary<string, BitmapSource?> ImageCache = new();

    /// <summary>Draws a whole profile, bottom layer first.</summary>
    public static void DrawProfile(DrawingContext dc, OverlayProfile? profile,
                                   SensorSnapshot values, LayerContext ctx)
    {
        if (profile == null) return;

        // Taken from the profile rather than the caller, so the theme a profile
        // was saved with is the theme it draws with — whoever is drawing it.
        ctx.Theme = OverlayTheme.ByName(profile.Theme);

        foreach (OverlayLayer layer in profile.Layers)
        {
            if (!layer.Enabled) continue;
            if (!IsVisible(layer, values, ctx)) continue;

            try
            {
                DrawLayer(dc, layer, values, ctx);
            }
            catch (Exception)
            {
                // One malformed layer — a bad colour, a missing font, an image
                // that will not decode — must not take the whole overlay with
                // it. The rest of the profile still draws.
            }
        }
    }

    /// <summary>Draws one layer, with its opacity and rotation applied.</summary>
    public static void DrawLayer(DrawingContext dc, OverlayLayer layer,
                                 SensorSnapshot values, LayerContext ctx)
    {
        (double x, double y) = layer.TopLeft(ctx.Width, ctx.Height);
        var bounds = new Rect(x, y, Math.Max(1, layer.Width), Math.Max(1, layer.Height));

        int pushes = 0;

        if (layer.Opacity < 0.999)
        {
            dc.PushOpacity(Math.Clamp(layer.Opacity, 0, 1));
            pushes++;
        }

        if (Math.Abs(layer.Rotation) > 0.01)
        {
            dc.PushTransform(new RotateTransform(
                layer.Rotation, bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2));
            pushes++;
        }

        switch (layer)
        {
            case ShapeLayer s: DrawShape(dc, s, bounds, ctx.Theme); break;
            case ImageLayer i: DrawImage(dc, i, bounds, ctx); break;
            case BarLayer b: DrawBar(dc, b, bounds, values, ctx.Theme); break;
            case GaugeLayer g: DrawGauge(dc, g, bounds, values, ctx.Theme); break;
            case TextLayer t: DrawText(dc, t, bounds, values, ctx.Theme); break;
            case GlyphLayer gl: DrawGlyph(dc, gl, bounds, values, ctx.Theme); break;
            case GraphLayer gr: DrawGraph(dc, gr, bounds, values, ctx.Theme); break;
        }

        for (int i = 0; i < pushes; i++) dc.Pop();
    }

    // -----------------------------------------------------------------------

    private static bool IsVisible(OverlayLayer layer, SensorSnapshot values, LayerContext ctx)
    {
        switch (layer.VisibleWhen)
        {
            case VisibilityRule.Always:
                return true;

            case VisibilityRule.WhilePlaying:
                return ctx.IsPlaying;

            case VisibilityRule.WhileIdle:
                return !ctx.IsPlaying;

            case VisibilityRule.SensorAbove:
            case VisibilityRule.SensorBelow:
            {
                if (string.IsNullOrEmpty(layer.VisibleSource)) return true;
                SensorReading r = values[layer.VisibleSource];

                // A threshold that cannot be evaluated hides the layer. A
                // warning that appears because its sensor went missing would be
                // worse than one that never appears at all.
                if (!r.Available) return false;

                return layer.VisibleWhen == VisibilityRule.SensorAbove
                    ? r.Value >= layer.VisibleThreshold
                    : r.Value <= layer.VisibleThreshold;
            }

            default:
                return true;
        }
    }

    // -----------------------------------------------------------------------
    // Shapes and images
    // -----------------------------------------------------------------------

    private static void DrawShape(DrawingContext dc, ShapeLayer s, Rect r,
                                  OverlayTheme? theme)
    {
        Brush? fill = s.FillColourTo != null
            ? VerticalGradient(s.FillColour, s.FillColourTo, theme)
            : Palette.Brush(s.FillColour, theme);

        Pen? pen = Palette.Pen(s.StrokeColour, s.StrokeWidth, theme);

        // Ornament is stroked, not filled, so for those kinds the fill colour is
        // what the model or the editor actually meant by "the colour". Falling
        // back keeps a bracket from silently drawing nothing when only a fill
        // was given.
        if (IsOrnament(s.Kind) && pen == null && fill != null)
            pen = Palette.Pen(s.FillColour, s.StrokeWidth, theme);

        if (fill == null && pen == null) return;

        switch (s.Kind)
        {
            case ShapeKind.Ellipse:
                dc.DrawEllipse(fill, pen,
                    new Point(r.X + r.Width / 2, r.Y + r.Height / 2), r.Width / 2, r.Height / 2);
                break;

            case ShapeKind.Line:
                // A line uses the box's diagonal, so dragging the corners in the
                // editor sets both ends without a separate concept.
                if (pen != null) dc.DrawLine(pen, r.TopLeft, r.BottomRight);
                break;

            case ShapeKind.Ring:
                // No fill by design: a filled ring is an ellipse, and there is
                // already a kind for that.
                if (pen != null)
                    dc.DrawEllipse(null, pen, new Point(r.X + r.Width / 2, r.Y + r.Height / 2),
                        r.Width / 2 - pen.Thickness / 2, r.Height / 2 - pen.Thickness / 2);
                break;

            case ShapeKind.Arc:
                if (pen != null)
                {
                    var centre = new Point(r.X + r.Width / 2, r.Y + r.Height / 2);
                    double radius = Math.Max(1,
                        Math.Min(r.Width, r.Height) / 2 - pen.Thickness / 2);

                    dc.DrawGeometry(null, pen, Arc(centre, radius, s.StartAngle, s.SweepAngle));
                }
                break;

            case ShapeKind.Bracket:
                if (pen != null) DrawBracket(dc, pen, r);
                break;

            case ShapeKind.Rule:
                DrawRule(dc, s, r, pen, theme);
                break;

            case ShapeKind.Chevron:
                if (pen != null) DrawChevron(dc, pen, r);
                break;

            default:
                double cr = ResolveRadius(s.CornerRadius, theme);
                if (cr > 0) dc.DrawRoundedRectangle(fill, pen, r, cr, cr);
                else
                    dc.DrawRectangle(fill, pen, r);
                break;
        }
    }

    /// <summary>
    /// A top-to-bottom gradient between two roles.
    ///
    /// Vertical rather than configurable: a card shaded top to bottom reads as a
    /// lit surface, which is the whole point, and every other angle mostly reads
    /// as a mistake. Falls back to the flat colour if either half will not
    /// resolve, so a bad gradient loses the effect rather than the layer.
    /// </summary>
    private static Brush? VerticalGradient(string? from, string? to, OverlayTheme? theme)
    {
        Brush? a = Palette.Brush(from, theme);
        Brush? b = Palette.Brush(to, theme);

        if (a is not SolidColorBrush sa || b is not SolidColorBrush sb) return a;

        var gradient = new LinearGradientBrush(sa.Color, sb.Color, 90);
        gradient.Freeze();
        return gradient;
    }

    private static bool IsOrnament(ShapeKind kind) =>
        kind is ShapeKind.Ring or ShapeKind.Arc or ShapeKind.Bracket
            or ShapeKind.Rule or ShapeKind.Chevron;

    /// <summary>
    /// Four corner marks framing the box.
    ///
    /// The legs are a quarter of the shorter side, which keeps a bracket looking
    /// like a frame at any size rather than closing into a rectangle on a small
    /// box or dwindling to specks on a large one.
    /// </summary>
    private static void DrawBracket(DrawingContext dc, Pen pen, Rect r)
    {
        double leg = Math.Min(r.Width, r.Height) * 0.25;

        (Point Corner, Point H, Point V)[] corners =
        {
            (r.TopLeft,     new Point(r.Left + leg,  r.Top),    new Point(r.Left,  r.Top + leg)),
            (r.TopRight,    new Point(r.Right - leg, r.Top),    new Point(r.Right, r.Top + leg)),
            (r.BottomLeft,  new Point(r.Left + leg,  r.Bottom), new Point(r.Left,  r.Bottom - leg)),
            (r.BottomRight, new Point(r.Right - leg, r.Bottom), new Point(r.Right, r.Bottom - leg)),
        };

        foreach ((Point corner, Point h, Point v) in corners)
        {
            dc.DrawLine(pen, corner, h);
            dc.DrawLine(pen, corner, v);
        }
    }

    /// <summary>
    /// A divider along the box's longer axis, optionally fading at both ends.
    ///
    /// The fade matters more than it sounds: a hard-ended hairline butting into
    /// the edge of a cluster reads as a mistake, while one that stops reads as a
    /// choice.
    /// </summary>
    private static void DrawRule(DrawingContext dc, ShapeLayer s, Rect r, Pen? pen,
                                 OverlayTheme? theme)
    {
        if (pen == null) return;

        bool horizontal = r.Width >= r.Height;

        Point a = horizontal
            ? new Point(r.Left, r.Y + r.Height / 2)
            : new Point(r.X + r.Width / 2, r.Top);

        Point b = horizontal
            ? new Point(r.Right, r.Y + r.Height / 2)
            : new Point(r.X + r.Width / 2, r.Bottom);

        if (!s.Fade)
        {
            dc.DrawLine(pen, a, b);
            return;
        }

        // Transparent at both ends, solid through the middle. Built from the
        // resolved colour rather than the pen so the alpha ramp is exact.
        string? literal = Palette.Literal(s.StrokeColour ?? s.FillColour, theme);
        if (literal == null) { dc.DrawLine(pen, a, b); return; }

        Color c;
        try { c = Palette.Parse(literal); }
        catch (FormatException) { dc.DrawLine(pen, a, b); return; }

        var gradient = new LinearGradientBrush
        {
            StartPoint = horizontal ? new Point(0, 0.5) : new Point(0.5, 0),
            EndPoint = horizontal ? new Point(1, 0.5) : new Point(0.5, 1),
        };
        gradient.GradientStops.Add(new GradientStop(Color.FromArgb(0, c.R, c.G, c.B), 0));
        gradient.GradientStops.Add(new GradientStop(c, 0.5));
        gradient.GradientStops.Add(new GradientStop(Color.FromArgb(0, c.R, c.G, c.B), 1));
        gradient.Freeze();

        var faded = new Pen(gradient, pen.Thickness);
        faded.Freeze();
        dc.DrawLine(faded, a, b);
    }

    /// <summary>
    /// A single direction mark pointing right. Rotation turns it, which is why
    /// there is no separate direction property.
    /// </summary>
    private static void DrawChevron(DrawingContext dc, Pen pen, Rect r)
    {
        double cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
        double half = Math.Min(r.Width, r.Height) / 2;

        var tip = new Point(cx + half * 0.45, cy);
        dc.DrawLine(pen, new Point(cx - half * 0.35, cy - half * 0.7), tip);
        dc.DrawLine(pen, new Point(cx - half * 0.35, cy + half * 0.7), tip);
    }

    private static void DrawImage(DrawingContext dc, ImageLayer layer, Rect r, LayerContext ctx)
    {
        BitmapSource? bmp = LoadImage(layer.File, ctx.AssetDirectory);
        if (bmp == null) return;

        if (!layer.PreserveAspect) { dc.DrawImage(bmp, r); return; }

        double scale = Math.Min(r.Width / bmp.PixelWidth, r.Height / bmp.PixelHeight);
        double w = bmp.PixelWidth * scale, h = bmp.PixelHeight * scale;
        dc.DrawImage(bmp, new Rect(r.X + (r.Width - w) / 2, r.Y + (r.Height - h) / 2, w, h));
    }

    private static BitmapSource? LoadImage(string file, string assetDir)
    {
        if (string.IsNullOrWhiteSpace(file)) return null;

        return ImageCache.GetOrAdd(file, f =>
        {
            try
            {
                string path = Path.IsPathRooted(f) ? f : Path.Combine(assetDir, f);
                if (!File.Exists(path)) return null;

                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;   // release the file handle
                bmp.UriSource = new Uri(path);
                bmp.EndInit();
                bmp.Freeze();                                 // shareable across threads
                return bmp;
            }
            catch
            {
                return null;   // cached as a miss, so a bad path is not retried every frame
            }
        });
    }

    /// <summary>Forgets decoded images, for when the editor replaces one.</summary>
    public static void ClearImageCache() => ImageCache.Clear();

    // -----------------------------------------------------------------------
    // Bars
    // -----------------------------------------------------------------------

    private static void DrawBar(DrawingContext dc, BarLayer b, Rect r, SensorSnapshot values,
                                OverlayTheme? theme)
    {
        double fraction = Fraction(b.Source, b.Min, b.Max, values, out SensorReading reading);

        Brush? track = Palette.Brush(b.TrackColour, theme);
        Pen? border = Palette.Pen(b.BorderColour, b.BorderWidth, theme);
        // A bar asking for the theme gets a pill under a rounded look and a
        // hard rectangle under a square one, rather than one fixed shape.
        double stored = b.CornerRadius < 0
            ? ((theme ?? OverlayTheme.Minimal).CornerRadius > 0 ? r.Height / 2 : 0)
            : b.CornerRadius;
        double radius = Math.Min(stored, Math.Min(r.Width, r.Height) / 2);

        if (track != null || border != null)
        {
            if (radius > 0) dc.DrawRoundedRectangle(track, border, r, radius, radius);
            else dc.DrawRectangle(track, border, r);
        }

        if (!reading.Available || fraction <= 0) return;

        Brush fill = ResolveFill(b.FillColour, b.FillColourTo, b.Thresholds, reading.Value, r,
                                 b.Orientation == BarOrientation.Horizontal, theme);

        if (b.Segments > 0)
        {
            DrawSegments(dc, b, r, fraction, fill, radius);
            return;
        }

        Rect filled = FilledRect(r, fraction, b.Orientation, b.Reversed, radius);
        if (filled.Width <= 0 || filled.Height <= 0) return;

        if (radius > 0)
        {
            // Clip to the track so the fill's own rounded end cannot poke out of
            // the corner at low values.
            dc.PushClip(new RectangleGeometry(r, radius, radius));
            dc.DrawRoundedRectangle(fill, null, filled, radius, radius);
            dc.Pop();
        }
        else
        {
            dc.DrawRectangle(fill, null, filled);
        }
    }

    private static Rect FilledRect(Rect r, double fraction, BarOrientation o, bool reversed,
                                   double radius)
    {
        if (o == BarOrientation.Horizontal)
        {
            // Never narrower than the corner diameter, or a rounded bar at 1%
            // renders as a sliver with no shape at all.
            double w = Math.Max(radius * 2, r.Width * fraction);
            return reversed ? new Rect(r.Right - w, r.Y, w, r.Height)
                            : new Rect(r.X, r.Y, w, r.Height);
        }

        double h = Math.Max(radius * 2, r.Height * fraction);
        // Vertical bars fill upwards by default, which is what a level looks like.
        return reversed ? new Rect(r.X, r.Y, r.Width, h)
                        : new Rect(r.X, r.Bottom - h, r.Width, h);
    }

    private static void DrawSegments(DrawingContext dc, BarLayer b, Rect r, double fraction,
                                     Brush fill, double radius)
    {
        int n = Math.Max(1, b.Segments);
        int lit = (int)Math.Round(fraction * n);
        bool horizontal = b.Orientation == BarOrientation.Horizontal;

        double gap = b.SegmentGap;
        double span = (horizontal ? r.Width : r.Height) - gap * (n - 1);
        double size = span / n;
        if (size <= 0) return;

        for (int i = 0; i < n; i++)
        {
            if (i >= lit) continue;

            // Index from the far end when reversed so the lit run stays contiguous.
            int slot = b.Reversed ? n - 1 - i : i;

            Rect seg = horizontal
                ? new Rect(r.X + slot * (size + gap), r.Y, size, r.Height)
                : new Rect(r.X, r.Bottom - (slot + 1) * size - slot * gap, r.Width, size);

            if (radius > 0)
                dc.DrawRoundedRectangle(fill, null, seg, radius, radius);
            else
                dc.DrawRectangle(fill, null, seg);
        }
    }

    // -----------------------------------------------------------------------
    // Gauges
    // -----------------------------------------------------------------------

    private static void DrawGauge(DrawingContext dc, GaugeLayer g, Rect r, SensorSnapshot values,
                                  OverlayTheme? theme)
    {
        double fraction = Fraction(g.Source, g.Min, g.Max, values, out SensorReading reading);

        var centre = new Point(r.X + r.Width / 2, r.Y + r.Height / 2);
        double radius = Math.Max(1, Math.Min(r.Width, r.Height) / 2 - g.Thickness / 2);

        PenLineCap cap = g.RoundCaps ? PenLineCap.Round : PenLineCap.Flat;

        Pen? track = Palette.Pen(g.TrackColour, g.Thickness, theme);
        if (track != null)
        {
            track = track.Clone();
            track.StartLineCap = track.EndLineCap = cap;
            track.Freeze();
            dc.DrawGeometry(null, track, Arc(centre, radius, g.StartAngle, g.SweepAngle));
        }

        if (reading.Available && fraction > 0.001)
        {
            Brush fillBrush = ResolveFill(g.FillColour, null, g.Thresholds, reading.Value, r,
                                          true, theme);
            var pen = new Pen(fillBrush, g.Thickness) { StartLineCap = cap, EndLineCap = cap };
            pen.Freeze();
            dc.DrawGeometry(null, pen, Arc(centre, radius, g.StartAngle, g.SweepAngle * fraction));
        }

        if (g.Ticks > 1)
        {
            Pen? tick = Palette.Pen(g.TickColour, 2, theme);
            if (tick != null)
            {
                double inner = radius - g.Thickness / 2 - 3;
                double outer = radius - g.Thickness / 2 - 9;
                for (int i = 0; i < g.Ticks; i++)
                {
                    double a = (g.StartAngle + g.SweepAngle * i / (g.Ticks - 1)) * Math.PI / 180.0;
                    dc.DrawLine(tick,
                        new Point(centre.X + inner * Math.Cos(a), centre.Y + inner * Math.Sin(a)),
                        new Point(centre.X + outer * Math.Cos(a), centre.Y + outer * Math.Sin(a)));
                }
            }
        }

        bool hasCaption = !string.IsNullOrEmpty(g.Caption);

        if (!string.IsNullOrEmpty(g.CentreTemplate))
        {
            FormattedText ft = Text(TokenFormatter.Format(g.CentreTemplate, values),
                g.FontFamily, g.CentreFontSize, true, false,
                Palette.Brush(g.CentreColour, theme), theme);

            // Lifted slightly when a caption sits underneath, so the pair reads
            // as one block centred in the ring rather than two things.
            double dy = hasCaption ? 6 : 0;
            dc.DrawText(ft, new Point(centre.X - ft.Width / 2, centre.Y - ft.Height / 2 - dy));
        }

        if (hasCaption)
        {
            FormattedText ft = Text(TokenFormatter.Format(g.Caption, values),
                g.FontFamily, g.CaptionFontSize, false, false,
                Palette.Brush(g.CaptionColour, theme), theme);
            dc.DrawText(ft, new Point(centre.X - ft.Width / 2, centre.Y + 10));
        }
    }

    private static Geometry Arc(Point c, double r, double startDeg, double sweepDeg)
    {
        Point At(double deg)
        {
            double a = deg * Math.PI / 180.0;
            return new Point(c.X + r * Math.Cos(a), c.Y + r * Math.Sin(a));
        }

        bool negative = sweepDeg < 0;
        double sweep = Math.Abs(sweepDeg);
        if (sweep > 359.9) sweep = 359.9;   // a full circle has no arc endpoints

        var figure = new PathFigure { StartPoint = At(startDeg), IsClosed = false, IsFilled = false };
        figure.Segments.Add(new ArcSegment(
            At(startDeg + (negative ? -sweep : sweep)),
            new Size(r, r), 0, sweep > 180,
            negative ? SweepDirection.Counterclockwise : SweepDirection.Clockwise, true));

        var geo = new PathGeometry();
        geo.Figures.Add(figure);
        geo.Freeze();
        return geo;
    }

    // -----------------------------------------------------------------------
    // Graph
    // -----------------------------------------------------------------------

    private static void DrawGraph(DrawingContext dc, GraphLayer g, Rect r,
                                  SensorSnapshot values, OverlayTheme? theme)
    {
        if (r.Width < 2 || r.Height < 2) return;

        if (g.BackgroundColour != null)
        {
            Brush? back = Palette.Brush(g.BackgroundColour, theme);
            if (back != null)
            {
                if (g.CornerRadius > 0)
                    dc.DrawRoundedRectangle(back, null, r, g.CornerRadius, g.CornerRadius);
                else
                    dc.DrawRectangle(back, null, r);
            }
        }

        double[] samples = values.History(g.Source, g.WindowSeconds);

        // One point is not a trend, and drawing it as a flat line across the
        // whole window would claim a minute of history that does not exist.
        if (samples.Length < 2) return;

        (double lo, double hi) = Range(g, samples, values);
        if (hi <= lo) return;

        Brush? line = ResolveThreshold(g.Source, g.Thresholds, values, theme)
                      ?? Palette.Brush(g.LineColour, theme);

        // The right-hand edge is the newest sample, so the trace grows leftwards
        // into the past — which is the direction every other graph in the world
        // reads, and worth matching even though the array is oldest-first.
        // A stroke is centred on its path, so a value at 0 or at 100 would put
        // half the line outside the plot area — which is exactly what an idle
        // GPU looked like: a trace sitting on the bottom edge, half of it cut
        // off. Plot into a rect inset by half the line width instead.
        double inset = g.Style == GraphStyle.Bars
            ? 0
            : Math.Min(g.LineWidth / 2, r.Height / 4);

        var plot = new Rect(r.X, r.Y + inset, r.Width, Math.Max(1, r.Height - inset * 2));

        var points = new Point[samples.Length];
        double step = plot.Width / (samples.Length - 1);

        for (int i = 0; i < samples.Length; i++)
        {
            double t = Math.Clamp((samples[i] - lo) / (hi - lo), 0, 1);
            points[i] = new Point(plot.X + i * step, plot.Bottom - t * plot.Height);
        }

        if (g.Style == GraphStyle.Bars)
        {
            DrawGraphBars(dc, plot, points, line);
        }
        else
        {
            // The line's own colour, not the fill's. FillColour decides WHETHER
            // there is a fill; the threshold ramp decides what colour the graph
            // is. Resolving the two separately drew a green trace over a grey
            // wash, because the ramp reached the line and the fill fell back to
            // the neutral role.
            if (g.Style == GraphStyle.Area && g.FillColour != null && line is SolidColorBrush sc)
                DrawGraphArea(dc, plot, points, sc.Color);

            Pen? pen = Palette.Pen(g.LineColour, g.LineWidth, theme);
            if (line != null && pen != null)
            {
                pen = pen.Clone();
                pen.Brush = line;
                pen.LineJoin = PenLineJoin.Round;
                pen.StartLineCap = PenLineCap.Round;
                pen.EndLineCap = PenLineCap.Round;
                pen.Freeze();
                dc.DrawGeometry(null, pen, Polyline(points, false, plot));
            }
        }

        if (g.Baseline is { } baseline)
        {
            double t = (baseline - lo) / (hi - lo);
            if (t is >= 0 and <= 1)
            {
                Pen? bp = Palette.Pen(g.BaselineColour, 1, theme);
                if (bp != null)
                {
                    double y = plot.Bottom - t * plot.Height;
                    dc.DrawLine(bp, new Point(r.X, y), new Point(r.Right, y));
                }
            }
        }
    }

    private static void DrawGraphArea(DrawingContext dc, Rect r, Point[] points, Color colour)
    {
        // Fading to nothing at the bottom rather than filling flat: a solid
        // block hides whatever video is behind it, and the shape of the trace is
        // the information — the area is only there to give it weight.
        var fade = new LinearGradientBrush(
            Color.FromArgb((byte)(colour.A * 0.38), colour.R, colour.G, colour.B),
            Color.FromArgb(0, colour.R, colour.G, colour.B),
            90);
        fade.Freeze();

        dc.DrawGeometry(fade, null, Polyline(points, true, r));
    }

    private static void DrawGraphBars(DrawingContext dc, Rect r, Point[] points, Brush? fill)
    {
        if (fill == null) return;

        // Columns sit in slots, not on the points the line style uses. A line
        // spans edge to edge across n-1 gaps, so its first and last points are
        // ON the boundaries — centring a column there hangs half of it outside
        // the graph. n slots instead, each column inside its own.
        double slot = r.Width / points.Length;

        // A gap only once there is room for one; below about three pixels a
        // column plus a gap rounds away to nothing and the graph goes blank.
        double w = Math.Max(1, slot - (slot > 3 ? 1 : 0));

        for (int i = 0; i < points.Length; i++)
        {
            // A floor of one pixel, so a sensor reading zero draws a row of
            // stubs rather than an empty box. An idle GPU is a real reading and
            // the graph should say so; nothing at all just looks broken.
            double h = Math.Max(1, r.Bottom - points[i].Y);

            dc.DrawRectangle(fill, null, new Rect(r.X + i * slot, r.Bottom - h, w, h));
        }
    }

    private static Geometry Polyline(Point[] points, bool close, Rect r)
    {
        var figure = new PathFigure { StartPoint = points[0], IsClosed = close, IsFilled = close };

        for (int i = 1; i < points.Length; i++)
            figure.Segments.Add(new LineSegment(points[i], true));

        if (close)
        {
            // Down to the baseline and back, so the fill has a bottom edge.
            figure.Segments.Add(new LineSegment(new Point(points[^1].X, r.Bottom), false));
            figure.Segments.Add(new LineSegment(new Point(points[0].X, r.Bottom), false));
        }

        var geo = new PathGeometry();
        geo.Figures.Add(figure);
        geo.Freeze();
        return geo;
    }

    /// <summary>
    /// The vertical range to plot against.
    ///
    /// Auto-scaling is padded and never allowed to collapse: a sensor that has
    /// not moved would otherwise give a zero-height range, and every sample
    /// would land on the same line at an arbitrary height.
    /// </summary>
    private static (double Lo, double Hi) Range(GraphLayer g, double[] samples,
                                                SensorSnapshot values)
    {
        // Bars ignore auto-scale, deliberately. A column's height IS its value,
        // read against the bottom of the box — so rescaling makes an idle GPU
        // draw sixteen half-height columns, which says "steady moderate load"
        // about a card doing nothing. A line has no such claim: it only ever
        // shows a shape, so zooming in on it is fair.
        if (g.AutoScale && g.Style != GraphStyle.Bars)
        {
            double min = samples[0], max = samples[0];
            foreach (double v in samples)
            {
                if (v < min) min = v;
                if (v > max) max = v;
            }

            double pad = Math.Max((max - min) * 0.15, 0.5);
            double alo = min - pad, ahi = max + pad;

            // Never zoom so far in that noise looks like an event. Memory
            // sitting between 19.0% and 20.0% all minute is a flat line, and
            // that is the honest picture — blown up to fill the box it would
            // read as a dramatic swing. A fifth of the sensor's own range is the
            // tightest window worth showing.
            double floor = Span(g.Source, values) * 0.2;
            if (ahi - alo < floor)
            {
                double mid = (alo + ahi) / 2;
                alo = mid - floor / 2;
                ahi = mid + floor / 2;
            }

            return (alo, ahi);
        }

        double lo = g.Min ?? 0, hi = g.Max ?? 100;

        if (g.Min == null || g.Max == null)
        {
            foreach (SensorDescriptor d in values.Descriptors)
            {
                if (d.Id != g.Source) continue;
                lo = g.Min ?? d.Min;
                hi = g.Max ?? d.Max;
                break;
            }
        }

        return (lo, hi);
    }

    /// <summary>How wide the sensor's own sensible range is. 100 when unknown.</summary>
    private static double Span(string source, SensorSnapshot values)
    {
        foreach (SensorDescriptor d in values.Descriptors)
            if (d.Id == source && d.Max > d.Min)
                return d.Max - d.Min;

        return 100;
    }

    // -----------------------------------------------------------------------
    // Text
    // -----------------------------------------------------------------------

    private static void DrawText(DrawingContext dc, TextLayer t, Rect r, SensorSnapshot values,
                                 OverlayTheme? theme)
    {
        string s = TokenFormatter.Format(t.Template, values);
        if (s.Length == 0) return;

        Brush colour = ResolveThreshold(t.ThresholdSource, t.Thresholds, values, theme)
                       ?? Palette.Brush(t.Colour, theme) ?? Brushes.White;

        FormattedText ft = Text(s, t.FontFamily, t.FontSize, t.Bold, t.Italic, colour, theme);

        if (t.LineHeight > 0) ft.LineHeight = t.LineHeight;
        if (t.Wrap) ft.MaxTextWidth = Math.Max(1, r.Width);

        double x = t.Align switch
        {
            TextAlign.Centre => r.X + (r.Width - ft.Width) / 2,
            TextAlign.Right => r.Right - ft.Width,
            _ => r.X,
        };
        double y = r.Y;

        if (t.BackgroundColour != null)
        {
            Brush? back = Palette.Brush(t.BackgroundColour, theme);
            if (back != null)
            {
                double p = t.BackgroundPadding;
                var pill = new Rect(x - p, y - p / 2, ft.Width + p * 2, ft.Height + p);
                dc.DrawRoundedRectangle(back, null, pill, t.BackgroundRadius, t.BackgroundRadius);
            }
        }

        if (t.ShadowOffsetX != 0 || t.ShadowOffsetY != 0)
        {
            Brush? shadow = Palette.Brush(t.ShadowColour, theme);
            if (shadow != null)
            {
                FormattedText sft = Text(s, t.FontFamily, t.FontSize, t.Bold, t.Italic,
                                         shadow, theme);
                if (t.Wrap) sft.MaxTextWidth = Math.Max(1, r.Width);
                dc.DrawText(sft, new Point(x + t.ShadowOffsetX, y + t.ShadowOffsetY));
            }
        }

        if (t.GlowRadius > 0)
        {
            // Stroked passes, widest and faintest first, then the fill on top.
            //
            // Not a BlurEffect: that forces the visual through a bitmap effect
            // pass whose cost depends on the blur radius and the surface size,
            // and the render already spends about 3 ms of a 33 ms frame. Four
            // passes cost a fixed, predictable amount and read as a halo from
            // the distance a pump head is actually viewed at.
            Color gc = ((t.GlowColour != null
                            ? Palette.Brush(t.GlowColour, theme)
                            : colour) as SolidColorBrush)?.Color ?? Colors.White;

            Geometry geo = ft.BuildGeometry(new Point(x, y));

            const int passes = 4;
            for (int i = passes; i >= 1; i--)
            {
                var pen = new Pen(
                    new SolidColorBrush(Color.FromArgb((byte)(70 / i), gc.R, gc.G, gc.B)),
                    t.GlowRadius * 2 * i / passes)
                {
                    LineJoin = PenLineJoin.Round,
                };
                pen.Freeze();
                dc.DrawGeometry(null, pen, geo);
            }
        }

        if (t.OutlineWidth > 0)
        {
            // Geometry rather than a glyph run, so this is the slower path to
            // draw. It is not the more expensive one to encode, which is what
            // this comment used to claim — see OutlineWidth.
            Pen? pen = Palette.Pen(t.OutlineColour, t.OutlineWidth * 2, theme);
            if (pen != null)
            {
                pen = pen.Clone();
                pen.LineJoin = PenLineJoin.Round;
                pen.Freeze();
                dc.DrawGeometry(colour, pen, ft.BuildGeometry(new Point(x, y)));
                return;   // the geometry already carries the fill
            }
        }

        dc.DrawText(ft, new Point(x, y));
    }

    private static FormattedText Text(string s, string family, double size, bool bold, bool italic,
                                      Brush? brush, OverlayTheme? theme)
    {
        var typeface = new Typeface(
            new FontFamily(ResolveFont(family, theme)),
            italic ? FontStyles.Italic : FontStyles.Normal,
            bold ? FontWeights.Bold : FontWeights.Normal,
            FontStretches.Normal);

        return new FormattedText(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            typeface, Math.Max(1, size), brush ?? Brushes.White, 96.0);
    }

    /// <summary>
    /// Draws one icon, centred in its box.
    ///
    /// Centred on the glyph's own measured extents rather than on the font's
    /// line box: icon fonts carry a full text ascent and descent, so centring on
    /// the line box leaves every icon sitting noticeably high.
    /// </summary>
    private static void DrawGlyph(DrawingContext dc, GlyphLayer layer, Rect r,
                                  SensorSnapshot values, OverlayTheme? theme)
    {
        string? glyph = IconNames.Glyph(layer.Icon);
        if (glyph == null) return;

        string family = IconNames.ResolveFont();
        if (family.Length == 0) return;   // no icon font on this machine

        Brush colour = ResolveThreshold(layer.ThresholdSource, layer.Thresholds, values, theme)
                       ?? Palette.Brush(layer.Colour, theme) ?? Brushes.White;

        // 0 means "fit the box". The glyph's drawn height is a little under its
        // em size, so asking for the full box overflows it slightly.
        double size = layer.Size > 0
            ? layer.Size
            : Math.Min(r.Width, r.Height) * 0.86;

        var ft = new FormattedText(glyph, CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(new FontFamily(family), FontStyles.Normal, FontWeights.Normal,
                FontStretches.Normal),
            Math.Max(1, size), colour, 96.0);

        // A missing glyph measures as zero-width rather than throwing, so this
        // is also the check that catches a codepoint the installed font lacks.
        if (ft.Width <= 0) return;

        Geometry geometry = ft.BuildGeometry(new Point(0, 0));
        Rect ink = geometry.Bounds;

        if (ink.IsEmpty)
        {
            dc.DrawText(ft, new Point(r.X + (r.Width - ft.Width) / 2,
                                      r.Y + (r.Height - ft.Height) / 2));
            return;
        }

        dc.DrawText(ft, new Point(
            r.X + (r.Width - ink.Width) / 2 - ink.X,
            r.Y + (r.Height - ink.Height) / 2 - ink.Y));
    }

    /// <summary>
    /// A corner radius, honouring the theme when the layer asked it to.
    ///
    /// A negative stored radius means "whatever the theme says", which is what a
    /// generated layer writes. Square-versus-round is one of the most visible
    /// differences between looks, so leaving it baked at generation would make a
    /// theme switch only half apply.
    ///
    /// Zero or positive is a deliberate choice and is kept, which is why every
    /// profile written before themes existed keeps its own corners.
    /// </summary>
    private static double ResolveRadius(double stored, OverlayTheme? theme) =>
        stored < 0 ? (theme ?? OverlayTheme.Minimal).CornerRadius : stored;

    /// <summary>
    /// Which typeface a layer's font field means.
    ///
    /// Three cases, in the same order of precedence as colours. Empty means
    /// "follow the theme", which is what a generated layer stores so a theme
    /// change restyles it. A known role — <c>mono</c>, <c>condensed</c> —
    /// resolves through <see cref="FontRoles"/>. Anything else is taken as a
    /// literal family name, which is what every profile written before themes
    /// existed holds and why they still render unchanged.
    /// </summary>
    private static string ResolveFont(string? family, OverlayTheme? theme)
    {
        if (string.IsNullOrWhiteSpace(family))
            return FontRoles.Resolve((theme ?? OverlayTheme.Minimal).Font);

        string resolved = FontRoles.Resolve(family);

        // Resolve returns the default for anything it does not recognise, so an
        // unrecognised name is a literal rather than a role.
        return resolved == FontRoles.Resolve("default") && !IsDefaultRole(family)
            ? family
            : resolved;
    }

    private static bool IsDefaultRole(string family) =>
        family.Trim().ToLowerInvariant() is "default" or "";

    // -----------------------------------------------------------------------
    // Shared helpers
    // -----------------------------------------------------------------------

    /// <summary>
    /// Where a sensor sits between its bounds, 0 to 1. Falls back to the
    /// sensor's own declared range when the layer does not override it, so a
    /// bar bound to gpu.power is sensible without anyone typing 0 and 600.
    /// </summary>
    private static double Fraction(string source, double? min, double? max,
                                   SensorSnapshot values, out SensorReading reading)
    {
        reading = values[source];
        if (!reading.Available) return 0;

        double lo = min ?? 0, hi = max ?? 100;

        if (min == null || max == null)
        {
            foreach (SensorDescriptor d in values.Descriptors)
            {
                if (d.Id != source) continue;
                lo = min ?? d.Min;
                hi = max ?? d.Max;
                break;
            }
        }

        if (hi <= lo) return 0;
        return Math.Clamp((reading.Value - lo) / (hi - lo), 0, 1);
    }

    private static Brush ResolveFill(string colour, string? colourTo, List<ColourStop> stops,
                                     double value, Rect r, bool horizontal, OverlayTheme? theme)
    {
        if (stops.Count > 0)
        {
            Brush? byThreshold = PickStop(stops, value, theme);
            if (byThreshold != null) return byThreshold;
        }

        Brush solid = Palette.Brush(colour, theme) ?? Brushes.White;
        if (colourTo == null) return solid;

        Brush? to = Palette.Brush(colourTo, theme);
        if (to is not SolidColorBrush toSolid || solid is not SolidColorBrush fromSolid) return solid;

        var gradient = new LinearGradientBrush(fromSolid.Color, toSolid.Color,
            horizontal ? 0 : 90);
        gradient.Freeze();
        return gradient;
    }

    private static Brush? ResolveThreshold(string? source, List<ColourStop> stops,
                                           SensorSnapshot values, OverlayTheme? theme)
    {
        if (stops.Count == 0 || string.IsNullOrEmpty(source)) return null;

        SensorReading r = values[source];
        return r.Available ? PickStop(stops, r.Value, theme) : null;
    }

    /// <summary>The highest stop the value has reached.</summary>
    private static Brush? PickStop(List<ColourStop> stops, double value, OverlayTheme? theme)
    {
        ColourStop? best = null;
        foreach (ColourStop s in stops)
            if (value >= s.AtOrAbove && (best == null || s.AtOrAbove >= best.AtOrAbove))
                best = s;

        return best == null ? null : Palette.Brush(best.Colour, theme);
    }
}
