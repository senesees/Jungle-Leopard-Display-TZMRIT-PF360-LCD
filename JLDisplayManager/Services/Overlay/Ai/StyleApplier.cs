using System;
using System.Collections.Generic;
using System.Globalization;

using JLDisplayManager.Models.Overlay;

namespace JLDisplayManager.Services.Overlay.Ai;

/// <summary>
/// Applies a <see cref="LayerStyle"/> to a layer the factory has already built.
///
/// Runs after the defaults rather than instead of them, which is what makes the
/// guarantee work: a spec with no style block never reaches this at all, and one
/// with a partial block changes only what it names.
///
/// Everything here is defensive. The input is written by a language model, so a
/// value out of range is clamped, an unrecognised name is dropped with a note,
/// and a field that means nothing for this layer type is ignored quietly — a
/// model putting <c>ticks</c> on a text layer is confused, not broken, and
/// warning about it would bury the notes that matter.
/// </summary>
internal static class StyleApplier
{
    /// <summary>The gradient separator: <c>"cool-&gt;hot"</c>.</summary>
    private const string GradientArrow = "->";

    public static void Apply(OverlayLayer layer, LayerStyle? style, List<string> notes)
    {
        if (style == null) return;

        ApplyCommon(layer, style, notes);

        switch (layer)
        {
            case TextLayer t: ApplyText(t, style, notes); break;
            case BarLayer b: ApplyBar(b, style, notes); break;
            case GaugeLayer g: ApplyGauge(g, style, notes); break;
            case ShapeLayer s: ApplyShape(s, style, notes); break;
            case GraphLayer gr: ApplyGraph(gr, style, notes); break;
        }
    }

    // -----------------------------------------------------------------------

    private static void ApplyCommon(OverlayLayer layer, LayerStyle style, List<string> notes)
    {
        if (style.Opacity is { } o)
        {
            // Fully transparent is indistinguishable from a bug, so the floor is
            // low but not zero. Anything that wants to disappear has Enabled.
            layer.Opacity = Math.Clamp(o, 0.05, 1.0);
        }

        if (style.Rotate is { } r)
        {
            if (Math.Abs(r) > 360)
            {
                notes.Add($"ignored a rotation of {r:0}°; it has to be within one turn");
            }
            else
            {
                layer.Rotation = r;
            }
        }
    }

    private static void ApplyText(TextLayer t, LayerStyle style, List<string> notes)
    {
        if (style.Fill is { } fill)
        {
            // Text takes only the first half of a gradient: WPF can fill glyphs
            // with one, but at this size it reads as a smudge rather than as an
            // effect, and the second colour is invariably the one that vanishes.
            (string from, string? to) = SplitGradient(fill);
            t.Colour = AccentPalette.Resolve(from);

            if (to != null)
                notes.Add("used the first colour for the text; a gradient across "
                          + "glyphs this small is not legible");

            // An explicit colour beats the automatic green-amber-red ramp, or
            // asking for a blue readout would silently produce a green one.
            t.Thresholds.Clear();
            t.ThresholdSource = null;
        }

        if (style.Font is { } font) t.FontFamily = ResolveFontRole(font, notes);
        if (style.Bold is { } bold) t.Bold = bold;

        if (style.Outline is { } outline)
        {
            t.OutlineWidth = Math.Clamp(outline, 0, 6);

            // An outline and a pill together is belt and braces. The pill wins
            // on looks — an outlined readout on a card reads as two competing
            // treatments — not on encoded size, which is a wash between them.
            if (t.OutlineWidth > 0 && style.Pill != true) t.BackgroundColour = null;
        }

        if (style.Pill is { } pill)
        {
            t.BackgroundColour = pill ? AccentPalette.Panel : null;
            if (pill) t.OutlineWidth = 0;
        }

        if (style.Glow is { } glow)
        {
            // A glow is a fraction of the glyph, not an absolute distance.
            // Rendered as a 1:1 sheet of every radius against every font size
            // the layout engine emits: 12 px around 44 px text is a halo, and
            // the same 12 px around 22 px text fills the counters in and turns
            // the readout into a blob. A flat cap could not tell those apart.
            // Read off the sheet rather than picked: at 22 px, a radius of 6
            // still reads and 9 fills the counters in; at 44 px, 12 is fine.
            // Three tenths of the font size fits all three rows, and 12 stays
            // as an absolute ceiling because past it the passes wash together.
            double ceiling = Math.Min(12, Math.Max(3, t.FontSize * 0.3));
            t.GlowRadius = Math.Clamp(glow, 0, ceiling);

            // Only worth saying when it actually changed something. Rounding a
            // request down by half a pixel and announcing "eased a 6 px glow to
            // 6 px" is noise that buries the notes that matter.
            if (glow - ceiling >= 1)
                notes.Add($"eased a {glow:0} px glow to {ceiling:0.#} px, which is as "
                          + $"much as {t.FontSize:0} px text carries");
        }

        if (style.Radius is { } radius) t.BackgroundRadius = Math.Clamp(radius, 0, 40);
    }

    private static void ApplyBar(BarLayer b, LayerStyle style, List<string> notes)
    {
        if (style.Fill is { } fill)
        {
            (string from, string? to) = SplitGradient(fill);
            b.FillColour = AccentPalette.Resolve(from);
            b.FillColourTo = to == null ? null : AccentPalette.Resolve(to);

            // Same reasoning as text: a named colour is a decision, and the ramp
            // would override it on every load or temperature sensor.
            b.Thresholds.Clear();
        }

        if (style.Track is { } track) b.TrackColour = AccentPalette.Resolve(track);

        if (style.Segments is { } segments)
        {
            if (segments < 0)
            {
                notes.Add("ignored a negative segment count");
            }
            else
            {
                // Past about thirty on a 960-wide panel the gaps are wider than
                // the segments and it stops reading as a bar at all.
                b.Segments = Math.Min(segments, 40);
                if (segments > 40) notes.Add($"capped {segments} segments at 40");
            }
        }

        if (style.Radius is { } radius) b.CornerRadius = Math.Clamp(radius, 0, 40);
    }

    private static void ApplyGauge(GaugeLayer g, LayerStyle style, List<string> notes)
    {
        if (style.Fill is { } fill)
        {
            (string from, string? to) = SplitGradient(fill);
            g.FillColour = AccentPalette.Resolve(from);

            if (to != null)
                notes.Add("a gauge takes one colour; used the first");

            g.Thresholds.Clear();
        }

        if (style.Track is { } track) g.TrackColour = AccentPalette.Resolve(track);

        if (style.Ticks is { } ticks)
        {
            // One tick is a mark, not a scale; the renderer divides by
            // (Ticks - 1) and would divide by zero.
            g.Ticks = ticks <= 1 ? 0 : Math.Min(ticks, 24);
            if (ticks > 24) notes.Add($"capped {ticks} ticks at 24");
        }

        if (style.Sweep is { } sweep)
        {
            double s = Math.Clamp(sweep, -359, 359);

            // Keep the dial centred on the gap at the bottom as it widens or
            // narrows, rather than letting it drift round the face.
            g.SweepAngle = s;
            g.StartAngle = 90 + (360 - Math.Abs(s)) / 2;
        }

        if (style.Font is { } font) g.FontFamily = ResolveFontRole(font, notes);
    }

    private static void ApplyGraph(GraphLayer g, LayerStyle style, List<string> notes)
    {
        if (style.Fill is { } fill)
        {
            (string from, string? to) = SplitGradient(fill);
            g.LineColour = AccentPalette.Resolve(from);
            g.FillColour = AccentPalette.Resolve(from);

            if (to != null)
                notes.Add("a graph takes one colour; used the first");

            g.Thresholds.Clear();
        }

        if (style.Track is { } track) g.BackgroundColour = AccentPalette.Resolve(track);

        if (style.Radius is { } radius) g.CornerRadius = Math.Clamp(radius, 0, 40);

        // Segments is the closest thing the schema has to "draw it as columns",
        // and a model asking for a segmented graph means exactly that.
        if (style.Segments is { } segments && segments > 0)
            g.Style = GraphStyle.Bars;

        if (style.Shape is { } shape)
        {
            switch (shape.Trim().ToLowerInvariant())
            {
                case "line" or "sparkline": g.Style = GraphStyle.Line; break;
                case "area" or "filled": g.Style = GraphStyle.Area; break;
                case "bars" or "bar" or "columns": g.Style = GraphStyle.Bars; break;
                default:
                    notes.Add($"\"{shape}\" is not a graph style; left it as an area");
                    break;
            }
        }
    }

    private static void ApplyShape(ShapeLayer s, LayerStyle style, List<string> notes)
    {
        if (style.Shape is { } shape)
        {
            ShapeKind? kind = ParseShape(shape);

            if (kind == null)
            {
                notes.Add($"\"{shape}\" is not a shape; left it as a card");
            }
            else
            {
                s.Kind = kind.Value;

                // Ornament is a stroke, and a card's translucent fill would
                // otherwise sit behind it as a stray rectangle.
                if (kind != ShapeKind.Rectangle && kind != ShapeKind.Ellipse)
                {
                    s.StrokeColour ??= s.FillColour;
                    s.FillColour = null;

                    // A hairline is what these are for; the card default of 1
                    // is right for a rule but too thin for a bracket to read.
                    if (kind is ShapeKind.Bracket or ShapeKind.Chevron or ShapeKind.Ring
                             or ShapeKind.Arc)
                        s.StrokeWidth = Math.Max(s.StrokeWidth, 2);

                    if (kind == ShapeKind.Rule) s.Fade = true;
                }
            }
        }

        if (style.Fill is { } fill)
        {
            (string from, string? to) = SplitGradient(fill);
            string role = AccentPalette.Resolve(from);

            // Whichever of the two this shape actually draws with.
            if (s.FillColour != null)
            {
                s.FillColour = role;

                // A shaded card reads as a surface where a flat one reads as a
                // hole. Only a filled shape can take it: ornament is a stroke.
                s.FillColourTo = to == null ? null : AccentPalette.Resolve(to);
            }
            else
            {
                s.StrokeColour = role;
                if (to != null) notes.Add("ornament takes one colour; used the first");
            }
        }

        if (style.Radius is { } radius) s.CornerRadius = Math.Clamp(radius, 0, 80);

        if (style.Sweep is { } sweep)
        {
            double v = Math.Clamp(sweep, -359, 359);
            s.SweepAngle = v;
            s.StartAngle = 90 + (360 - Math.Abs(v)) / 2;
        }
    }

    private static ShapeKind? ParseShape(string shape) => shape.Trim().ToLowerInvariant() switch
    {
        "card" or "panel" or "rect" or "rectangle" or "box" => ShapeKind.Rectangle,
        "ellipse" or "circle" or "dot" => ShapeKind.Ellipse,
        "line" => ShapeKind.Line,
        "ring" or "donut" => ShapeKind.Ring,
        "arc" or "curve" => ShapeKind.Arc,
        "bracket" or "brackets" or "corners" or "frame" => ShapeKind.Bracket,
        "rule" or "divider" or "separator" or "hairline" => ShapeKind.Rule,
        "chevron" or "arrow" or "caret" => ShapeKind.Chevron,
        _ => null,
    };

    // -----------------------------------------------------------------------

    /// <summary>
    /// Splits <c>"cool-&gt;hot"</c> into its two roles. A string with no arrow
    /// is a single colour and comes back with a null second half.
    /// </summary>
    private static (string From, string? To) SplitGradient(string fill)
    {
        int at = fill.IndexOf(GradientArrow, StringComparison.Ordinal);
        if (at < 0) return (fill.Trim(), null);

        string from = fill[..at].Trim();
        string to = fill[(at + GradientArrow.Length)..].Trim();

        return (from, to.Length > 0 ? to : null);
    }

    /// <summary>
    /// A font role, or the theme's font if the model invented one.
    ///
    /// Empty means "follow the theme", which is what an unrecognised role falls
    /// back to — a made-up typeface name would otherwise be stored as a literal
    /// and silently render as something else entirely.
    /// </summary>
    private static string ResolveFontRole(string font, List<string> notes)
    {
        string f = font.Trim().ToLowerInvariant();

        if (f is "default" or "condensed" or "narrow" or "mono" or "monospace"
              or "code" or "display" or "headline")
            return f;

        notes.Add($"\"{font}\" is not a font style; used the theme's");
        return "";
    }
}
