using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace JLDisplayManager.Models.Overlay;

/// <summary>
/// Text built from a template: <c>CPU {cpu.load:0}%  {cpu.temp:0}°C</c>.
///
/// Every visual extra here exists because a panel sits over arbitrary video —
/// white text on a bright frame is unreadable, so an outline, a shadow or a
/// translucent pill behind it is the difference between a readout and a smudge.
/// </summary>
public sealed class TextLayer : OverlayLayer
{
    public string Template { get; set; } = "{cpu.load:0}%";

    /// <summary>
    /// Empty follows the profile's theme; a role name such as <c>mono</c>
    /// resolves through <see cref="FontRoles"/>; anything else is a literal
    /// family. Default is empty so a new layer picks up the look around it.
    /// </summary>
    public string FontFamily { get; set; } = "";
    public double FontSize { get; set; } = 24;
    public bool Bold { get; set; } = true;
    public bool Italic { get; set; }
    public double LetterSpacing { get; set; }
    public double LineHeight { get; set; }

    public string Colour { get; set; } = "text";
    public TextAlign Align { get; set; } = TextAlign.Left;

    /// <summary>Wrap to <see cref="OverlayLayer.Width"/> rather than running on.</summary>
    public bool Wrap { get; set; }

    /// <summary>
    /// A dark edge around the glyphs, which is what makes text readable over
    /// video that keeps changing brightness underneath it.
    ///
    /// This was documented as the expensive option — geometry rather than a
    /// glyph run, all those edges being what JPEG spends bits on. Measured, that
    /// is not true: an outline costs between -1.5 and +1.7 KB against plain text
    /// depending on the backdrop, and is CHEAPER than a background pill on two
    /// of the three frames tested. Both are noise against an 80 KB cap. Choose
    /// between them on looks, not on size.
    /// </summary>
    public double OutlineWidth { get; set; }

    public string OutlineColour { get; set; } = "#FF000000";

    public double ShadowOffsetX { get; set; }
    public double ShadowOffsetY { get; set; }
    public string ShadowColour { get; set; } = "#A0000000";

    /// <summary>
    /// A soft halo behind the glyphs, in pixels. 0 for none.
    ///
    /// Drawn as a few stroked passes rather than with a blur: WPF's
    /// <c>BlurEffect</c> on a visual feeding a RenderTargetBitmap is slow and
    /// unpredictable, and the render already costs about 3 ms. Passes are cheap
    /// and, unlike a blur, cost exactly what you can predict.
    ///
    /// Worth setting in proportion to <see cref="FontSize"/> — about a quarter
    /// of it. A radius that reads as a halo on large text fills in the counters
    /// of small text and leaves a blob. The AI path enforces this; the editor
    /// does not, because the editor is where you are allowed to mean it.
    /// </summary>
    public double GlowRadius { get; set; }

    /// <summary>Null takes the text's own colour, which is what a glow usually wants.</summary>
    public string? GlowColour { get; set; }

    /// <summary>Null for no pill behind the text.</summary>
    public string? BackgroundColour { get; set; }

    public double BackgroundRadius { get; set; } = 6;
    public double BackgroundPadding { get; set; } = 8;

    /// <summary>Recolours the text by value. Ignored when <see cref="Thresholds"/> is empty.</summary>
    public string? ThresholdSource { get; set; }

    public List<ColourStop> Thresholds { get; set; } = new();

    public override IEnumerable<string> Sources()
    {
        foreach (string s in TokenScanner.Sources(Template)) yield return s;
        if (!string.IsNullOrEmpty(ThresholdSource)) yield return ThresholdSource;
        if (!string.IsNullOrEmpty(VisibleSource)) yield return VisibleSource;
    }
}

/// <summary>A linear bar: the most legible way to show a percentage at arm's length.</summary>
public sealed class BarLayer : OverlayLayer
{
    public string Source { get; set; } = "cpu.load";

    /// <summary>Null takes the sensor's own sensible range.</summary>
    public double? Min { get; set; }

    public double? Max { get; set; }

    public BarOrientation Orientation { get; set; } = BarOrientation.Horizontal;

    /// <summary>Fill from the far end instead — right to left, or top to bottom.</summary>
    public bool Reversed { get; set; }

    public string TrackColour { get; set; } = "track";
    public string FillColour { get; set; } = "good";

    /// <summary>Non-null makes the fill a gradient from <see cref="FillColour"/> to this.</summary>
    public string? FillColourTo { get; set; }

    public List<ColourStop> Thresholds { get; set; } = new();

    public double CornerRadius { get; set; } = 6;
    public string? BorderColour { get; set; }
    public double BorderWidth { get; set; } = 1;

    /// <summary>0 is a continuous bar; anything else gives the blocky segmented look.</summary>
    public int Segments { get; set; }

    public double SegmentGap { get; set; } = 3;

    public override IEnumerable<string> Sources()
    {
        yield return Source;
        if (!string.IsNullOrEmpty(VisibleSource)) yield return VisibleSource;
    }
}

/// <summary>
/// A radial gauge. The shape that suits a 960x480 pump head best, and the one
/// worth getting right: a ring reads at a glance from across a room in a way a
/// number does not.
/// </summary>
public sealed class GaugeLayer : OverlayLayer
{
    public string Source { get; set; } = "gpu.load";

    public double? Min { get; set; }
    public double? Max { get; set; }

    /// <summary>Degrees clockwise from three o'clock. 135 with a 270 sweep is the classic dial.</summary>
    public double StartAngle { get; set; } = 135;

    public double SweepAngle { get; set; } = 270;

    public double Thickness { get; set; } = 14;

    public bool RoundCaps { get; set; } = true;

    public string TrackColour { get; set; } = "track";
    public string FillColour { get; set; } = "good";
    public List<ColourStop> Thresholds { get; set; } = new();

    /// <summary>0 for none.</summary>
    public int Ticks { get; set; }

    public string TickColour { get; set; } = "line";

    /// <summary>Drawn in the middle; empty for a bare ring.</summary>
    public string CentreTemplate { get; set; } = "{gpu.load:0}%";

    public double CentreFontSize { get; set; } = 26;
    public string CentreColour { get; set; } = "text";

    /// <summary>A small caption under the centre text — "GPU", "CPU".</summary>
    public string Caption { get; set; } = "";

    public double CaptionFontSize { get; set; } = 14;
    public string CaptionColour { get; set; } = "dim";

    /// <summary>
    /// Empty follows the profile's theme; a role name such as <c>mono</c>
    /// resolves through <see cref="FontRoles"/>; anything else is a literal
    /// family. Default is empty so a new layer picks up the look around it.
    /// </summary>
    public string FontFamily { get; set; } = "";

    public override IEnumerable<string> Sources()
    {
        yield return Source;
        foreach (string s in TokenScanner.Sources(CentreTemplate)) yield return s;
        foreach (string s in TokenScanner.Sources(Caption)) yield return s;
        if (!string.IsNullOrEmpty(VisibleSource)) yield return VisibleSource;
    }
}

/// <summary>
/// A plain rectangle, ellipse or line. What turns a scatter of readouts into
/// something that looks designed: a backing panel, a divider, a frame.
/// </summary>
public sealed class ShapeLayer : OverlayLayer
{
    public ShapeKind Kind { get; set; } = ShapeKind.Rectangle;

    public string? FillColour { get; set; } = "panel";

    /// <summary>
    /// Non-null makes the fill a gradient down from <see cref="FillColour"/>,
    /// which is what turns a flat card into something that reads as a surface.
    /// </summary>
    public string? FillColourTo { get; set; }

    public string? StrokeColour { get; set; }
    public double StrokeWidth { get; set; } = 1;
    public double CornerRadius { get; set; }

    /// <summary>Where an <see cref="ShapeKind.Arc"/> begins, clockwise from three o'clock.</summary>
    public double StartAngle { get; set; } = 135;

    /// <summary>How far an arc sweeps. Negative runs anticlockwise.</summary>
    public double SweepAngle { get; set; } = 270;

    /// <summary>
    /// Fades a <see cref="ShapeKind.Rule"/> out at both ends, so a divider stops
    /// rather than butting into whatever is beside it.
    /// </summary>
    public bool Fade { get; set; }

    public override IEnumerable<string> Sources()
    {
        if (!string.IsNullOrEmpty(VisibleSource)) yield return VisibleSource;
    }
}

/// <summary>
/// One icon from the system icon font.
///
/// An icon beside a readout is most of the difference between a dashboard and a
/// list of numbers, and this costs nothing to ship: Windows already has the
/// font, so there is no asset to bundle, no file to lose, and no image to
/// decode per frame.
///
/// Distinct from <see cref="ImageLayer"/> on purpose. That one draws a file the
/// user chose, which is useful in the editor and useless to a language model —
/// it cannot know what images exist. This one is reachable from a prompt.
/// </summary>
public sealed class GlyphLayer : OverlayLayer
{
    /// <summary>A name from <see cref="Services.Overlay.IconNames"/>, not a codepoint.</summary>
    public string Icon { get; set; } = "cpu";

    /// <summary>
    /// 0 fits the glyph to the layer box, which is what a generated layer wants.
    /// Anything else is an explicit size in pixels.
    /// </summary>
    public double Size { get; set; }

    public string Colour { get; set; } = "text";

    /// <summary>Recolours by value, exactly as a text layer does.</summary>
    public string? ThresholdSource { get; set; }

    public List<ColourStop> Thresholds { get; set; } = new();

    public override IEnumerable<string> Sources()
    {
        if (!string.IsNullOrEmpty(ThresholdSource)) yield return ThresholdSource;
        if (!string.IsNullOrEmpty(VisibleSource)) yield return VisibleSource;
    }
}

/// <summary>
/// A sparkline: one sensor's recent past.
///
/// The only layer here that shows something a glance at the panel could not
/// otherwise tell you. A number says the CPU is at 40%; a graph says whether it
/// has been there for a minute or arrived a second ago, which is usually the
/// question actually being asked.
///
/// It has one cost worth knowing about, recorded in the plan's §8.3: the
/// renderer skips a frame when nothing visible has changed, and a graph changes
/// every time its window slides. A profile containing one skips far less.
/// </summary>
public sealed class GraphLayer : OverlayLayer
{
    public string Source { get; set; } = "cpu.load";

    public GraphStyle Style { get; set; } = GraphStyle.Area;

    /// <summary>
    /// How much of the past to show. Clamped by what the registry actually
    /// keeps, so asking for an hour quietly gives you the two minutes it has.
    /// </summary>
    public double WindowSeconds { get; set; } = 60;

    /// <summary>Null tracks the sensor's own sensible range.</summary>
    public double? Min { get; set; }

    public double? Max { get; set; }

    /// <summary>
    /// Rescale to whatever the window actually contains rather than the sensor's
    /// nominal range. Turns a flat line at 3% into a readable trace, at the cost
    /// of a vertical scale that keeps moving.
    /// </summary>
    public bool AutoScale { get; set; }

    public string LineColour { get; set; } = "good";

    /// <summary>Null draws no fill even in <see cref="GraphStyle.Area"/>.</summary>
    public string? FillColour { get; set; } = "good";

    public double LineWidth { get; set; } = 2;

    /// <summary>Null for none. A faint frame is usually enough to read against.</summary>
    public string? BackgroundColour { get; set; }

    public double CornerRadius { get; set; } = 4;

    /// <summary>0 for none; otherwise a horizontal line at this value.</summary>
    public double? Baseline { get; set; }

    public string BaselineColour { get; set; } = "line";

    public List<ColourStop> Thresholds { get; set; } = new();

    public override IEnumerable<string> Sources()
    {
        yield return Source;
        if (!string.IsNullOrEmpty(VisibleSource)) yield return VisibleSource;
    }
}

/// <summary>
/// A still image — an icon, a logo, a frame.
///
/// The file is copied into the overlay assets folder when it is added, so a
/// profile stays portable and does not break when the original is moved.
/// </summary>
public sealed class ImageLayer : OverlayLayer
{
    /// <summary>File name within the overlay assets folder, or an absolute path.</summary>
    public string File { get; set; } = "";

    /// <summary>Fit inside <see cref="OverlayLayer.Width"/> x Height rather than filling it.</summary>
    public bool PreserveAspect { get; set; } = true;

    public override IEnumerable<string> Sources()
    {
        if (!string.IsNullOrEmpty(VisibleSource)) yield return VisibleSource;
    }
}

/// <summary>
/// Pulls sensor ids out of a template so a layer can declare what it depends on
/// without the renderer having to parse it twice.
/// </summary>
internal static class TokenScanner
{
    public static IEnumerable<string> Sources(string? template)
    {
        if (string.IsNullOrEmpty(template)) yield break;

        for (int i = 0; i < template.Length; i++)
        {
            if (template[i] != '{') continue;
            if (i + 1 < template.Length && template[i + 1] == '{') { i++; continue; }

            int end = template.IndexOf('}', i + 1);
            if (end < 0) yield break;

            string body = template[(i + 1)..end];
            int colon = body.IndexOf(':');
            string id = (colon >= 0 ? body[..colon] : body).Trim();
            if (id.Length > 0) yield return id;

            i = end;
        }
    }
}
