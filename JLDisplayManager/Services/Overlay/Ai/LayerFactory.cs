using System;
using System.Collections.Generic;
using System.Globalization;

using JLDisplayManager.Models.Overlay;
using JLDisplayManager.Services.Sensors;

namespace JLDisplayManager.Services.Overlay.Ai;

/// <summary>What an expansion produced, and what it had to throw away.</summary>
public sealed class ExpansionResult
{
    public List<OverlayLayer> Layers { get; } = new();

    /// <summary>
    /// Plain-language notes about anything dropped or corrected, shown with the
    /// result. A silently discarded layer is worse than a visible one: the user
    /// asked for something and has to be told it did not happen.
    /// </summary>
    public List<string> Notes { get; } = new();
}

/// <summary>
/// Turns a <see cref="LayerSpec"/> into a real overlay layer.
///
/// This is where the feature's taste lives, and it is deliberately ours rather
/// than the model's. A gauge always gets a sensible sweep and cap; a
/// temperature always gets an amber-then-red ramp; a bar bound to a percentage
/// always spans 0–100 whether or not anyone said so. The model chooses what and
/// roughly where; everything about how it looks is decided here.
///
/// Geometry is only sized here, never placed — <see cref="LayoutEngine"/> owns
/// position. Anything written to X and Y at this stage is a placeholder.
/// </summary>
public static class LayerFactory
{
    // The size ladder. Three steps rather than free numbers, so a generated
    // profile is internally consistent and two "medium" things line up.
    private const double TextSmall = 16, TextMedium = 22, TextLarge = 32;

    /// <summary>
    /// Expands a whole plan. Layers that cannot be made are dropped with a note
    /// rather than failing the batch — one bad layer in ten must not cost the
    /// other nine.
    /// </summary>
    public static ExpansionResult Expand(IEnumerable<LayerSpec> specs, SensorSnapshot sensors)
    {
        var result = new ExpansionResult();

        foreach (LayerSpec spec in specs)
        {
            try
            {
                OverlayLayer? layer = Build(spec, sensors, result.Notes);
                if (layer == null) continue;

                // After the defaults, never instead of them. A spec with no
                // style block does not reach this, which is what keeps its
                // output byte-for-byte what it was before styling existed.
                StyleApplier.Apply(layer, spec.Style, result.Notes);

                result.Layers.Add(layer);
            }
            catch (Exception ex)
            {
                result.Notes.Add($"dropped a {spec.Kind ?? "layer"}: {ex.Message}");
            }
        }

        return result;
    }

    // -----------------------------------------------------------------------

    private static OverlayLayer? Build(LayerSpec spec, SensorSnapshot sensors, List<string> notes)
    {
        string kind = Normalise(spec.Kind);
        SensorDescriptor? sensor = FindSensor(spec.Sensor, sensors);

        // A sensor the model invented. For anything that draws a value this is
        // fatal — a bar with no source is a meaningless rectangle — but a text
        // layer can still carry a caption, so it survives as static text.
        if (!string.IsNullOrWhiteSpace(spec.Sensor) && sensor == null)
        {
            if (kind is "bar" or "gauge" or "graph")
            {
                notes.Add($"dropped a {kind}: this machine has no sensor called \"{spec.Sensor}\"");
                return null;
            }
            notes.Add($"\"{spec.Sensor}\" is not a sensor on this machine; kept the text without it");
        }

        LayerAnchor anchor = ParseAnchor(spec.Anchor);
        Size size = ParseSize(spec.Size);
        string accent = AccentPalette.Resolve(spec.Accent);

        return kind switch
        {
            "bar" => BuildBar(spec, sensor, anchor, size, accent),
            "gauge" => BuildGauge(spec, sensor, anchor, size, accent),
            "graph" => BuildGraph(spec, sensor, anchor, size, accent),
            "panel" => BuildPanel(spec, anchor, size),
            "icon" => BuildGlyph(spec, sensor, anchor, size, accent, notes),
            "image" => BuildImage(spec, anchor, size),
            "text" => BuildText(spec, sensor, anchor, size, accent),
            _ => Fallback(spec, sensor, anchor, size, accent, kind, notes),
        };
    }

    private static OverlayLayer? Fallback(LayerSpec spec, SensorDescriptor? sensor,
        LayerAnchor anchor, Size size, string accent, string kind, List<string> notes)
    {
        // An unknown kind that names a sensor is almost always meant to be a
        // readout, so make one rather than dropping the request entirely.
        if (sensor != null)
        {
            notes.Add($"\"{kind}\" is not a layer kind; made a readout instead");
            return BuildText(spec, sensor, anchor, size, accent);
        }

        notes.Add($"dropped a layer: \"{kind}\" is not a kind this can make");
        return null;
    }

    // -----------------------------------------------------------------------

    private static TextLayer BuildText(LayerSpec spec, SensorDescriptor? sensor,
        LayerAnchor anchor, Size size, string accent)
    {
        double font = size switch
        {
            Size.Small => TextSmall,
            Size.Large => TextLarge,
            _ => TextMedium,
        };

        string template = spec.Template ?? BuildTemplate(sensor, spec.Label);

        var layer = new TextLayer
        {
            Name = spec.Label ?? sensor?.Name ?? "Text",
            Anchor = anchor,
            Width = EstimateTextWidth(template, font),
            Height = font * 1.45,
            Template = template,
            FontSize = font,
            Colour = accent,
            Align = AlignFor(anchor),

            // Empty means "follow the theme", so a theme switch restyles the
            // typeface along with the palette. A literal name here would pin it.
            FontFamily = "",

            // A shadow rather than an outline: both keep text legible over
            // bright video, but an outline is geometry rather than a glyph run
            // and costs noticeably more to JPEG-encode under the panel's size
            // cap. See the overlay plan's notes on encoded size.
            ShadowOffsetX = 1.5,
            ShadowOffsetY = 1.5,
        };

        ApplyRamp(layer.Thresholds, sensor, spec, out string? rampSource);
        if (rampSource != null) layer.ThresholdSource = rampSource;

        return layer;
    }

    private static BarLayer BuildBar(LayerSpec spec, SensorDescriptor? sensor,
        LayerAnchor anchor, Size size, string accent)
    {
        (double w, double h) = size switch
        {
            Size.Small => (160.0, 10.0),
            Size.Large => (300.0, 18.0),
            _ => (220.0, 14.0),
        };

        var layer = new BarLayer
        {
            Name = (spec.Label ?? sensor?.Name ?? "Bar") + " bar",
            Anchor = anchor,
            Width = w,
            Height = h,
            Source = sensor?.Id ?? "cpu.load",
            TrackColour = AccentPalette.Track,
            FillColour = accent,
            // Negative: follow the theme. A rounded look gives a pill, a
            // square one gives a hard rectangle.
            CornerRadius = -1,
        };

        ApplyRamp(layer.Thresholds, sensor, spec, out _);
        return layer;
    }

    /// <summary>
    /// A sparkline. Wide and short by default, because that is the shape a
    /// trend reads best in and the shape that fits under a readout.
    /// </summary>
    private static GraphLayer BuildGraph(LayerSpec spec, SensorDescriptor? sensor,
        LayerAnchor anchor, Size size, string accent)
    {
        (double w, double h) = size switch
        {
            Size.Small => (160.0, 34.0),
            Size.Large => (300.0, 70.0),
            _ => (220.0, 50.0),
        };

        var layer = new GraphLayer
        {
            Name = (spec.Label ?? sensor?.Name ?? "Graph") + " graph",
            Anchor = anchor,
            Width = w,
            Height = h,
            Source = sensor?.Id ?? "cpu.load",
            LineColour = accent,
            FillColour = accent,

            // A plot area, always. A bare trace over video reads as a stray
            // line rather than a chart — rendered against the real thing, two
            // generated graphs looked like scratches on the panel. Inside a
            // group card it simply reads as the plot, which is what a chart
            // looks like anyway.
            BackgroundColour = AccentPalette.Panel,

            // A sparkline's job is "which way is this going", and against a full
            // 0-100 range most sensors are a flat line near the floor — an idle
            // GPU and 19% memory both drew as a straight edge. Auto-scaling has
            // a floor of its own in the renderer, so this reads the trend
            // without turning a 1% wobble into a mountain range.
            AutoScale = true,

            // A minute is the span where a trend is visible and the newest
            // sample still moves the picture. Two minutes is all that is kept,
            // and at that length a 220 px trace is under two pixels a sample.
            WindowSeconds = 60,
        };

        ApplyRamp(layer.Thresholds, sensor, spec, out _);
        return layer;
    }

    private static GaugeLayer BuildGauge(LayerSpec spec, SensorDescriptor? sensor,
        LayerAnchor anchor, Size size, string accent)
    {
        double d = size switch
        {
            Size.Small => 90.0,
            Size.Large => 160.0,
            _ => 120.0,
        };

        string id = sensor?.Id ?? "cpu.load";

        var layer = new GaugeLayer
        {
            Name = (spec.Label ?? sensor?.Name ?? "Gauge") + " gauge",
            Anchor = anchor,
            Width = d,
            Height = d,
            Source = id,

            // 135° with a 270° sweep is the dial everyone recognises, and
            // leaves the gap at the bottom where a caption goes.
            StartAngle = 135,
            SweepAngle = 270,
            Thickness = d / 8.5,
            RoundCaps = true,
            TrackColour = AccentPalette.Track,
            FillColour = accent,

            CentreTemplate = spec.Template ?? CentreTemplate(sensor),
            CentreFontSize = d * 0.22,
            CentreColour = AccentPalette.Neutral,
            Caption = spec.Label ?? "",
            CaptionFontSize = d * 0.12,
            CaptionColour = AccentPalette.Dim,
            FontFamily = "",   // follow the theme
        };

        ApplyRamp(layer.Thresholds, sensor, spec, out _);
        return layer;
    }

    private static ShapeLayer BuildPanel(LayerSpec spec, LayerAnchor anchor, Size size)
    {
        // Placeholder extents. A panel in a group is resized to fit that group
        // by the layout pass; one on its own keeps these.
        (double w, double h) = size switch
        {
            Size.Small => (200.0, 70.0),
            Size.Large => (400.0, 160.0),
            _ => (280.0, 110.0),
        };

        return new ShapeLayer
        {
            Name = spec.Label ?? "Panel",
            Anchor = anchor,
            Width = w,
            Height = h,
            Kind = ShapeKind.Rectangle,
            CornerRadius = -1,   // follow the theme
            FillColour = AccentPalette.Panel,
        };
    }

    /// <summary>
    /// An icon from the system font. This is what <c>kind: "icon"</c> means
    /// now — the old meaning, a file the user picks, moved to <c>image</c>.
    ///
    /// The swap is deliberate: a model cannot know what image files exist, so
    /// the reachable kind should be the one it can actually fill in.
    /// </summary>
    private static OverlayLayer? BuildGlyph(LayerSpec spec, SensorDescriptor? sensor,
        LayerAnchor anchor, Size size, string accent, List<string> notes)
    {
        // The label is a fallback because a model writing an icon layer often
        // puts the subject there rather than in the icon field.
        string? name = IconNames.Known(spec.Icon) ? spec.Icon
            : IconNames.Known(spec.Label) ? spec.Label
            : null;

        if (name == null)
        {
            string asked = spec.Icon ?? spec.Label ?? "";
            notes.Add(asked.Length > 0
                ? $"dropped an icon: there is no \"{asked}\" icon"
                : "dropped an icon: it did not say which one");
            return null;
        }

        double d = size switch
        {
            Size.Small => 28.0,
            Size.Large => 72.0,
            _ => 44.0,
        };

        var layer = new GlyphLayer
        {
            Name = name,
            Anchor = anchor,
            Width = d,
            Height = d,
            Icon = name,
            Colour = accent,
        };

        // An icon bound to a sensor takes the same ramp a readout would, so a
        // thermometer beside a temperature goes red with it.
        ApplyRamp(layer.Thresholds, sensor, spec, out string? rampSource);
        if (rampSource != null) layer.ThresholdSource = rampSource;

        return layer;
    }

    private static ImageLayer BuildImage(LayerSpec spec, LayerAnchor anchor, Size size)
    {
        double d = size switch
        {
            Size.Small => 48.0,
            Size.Large => 112.0,
            _ => 72.0,
        };

        // The file is whatever the user later points it at. An icon layer with
        // no file draws nothing, which is a visible, fixable blank rather than
        // an error — the model cannot know what images exist.
        return new ImageLayer
        {
            Name = spec.Label ?? "Image",
            Anchor = anchor,
            Width = d,
            Height = d,
            File = "",
            PreserveAspect = true,
        };
    }

    // -----------------------------------------------------------------------
    // Templates
    // -----------------------------------------------------------------------

    /// <summary>
    /// A readout for a sensor: "CPU 47%", "62°C", "20.3 GB". The label is worth
    /// including because a bare number on a panel means nothing at a glance.
    /// </summary>
    private static string BuildTemplate(SensorDescriptor? sensor, string? label)
    {
        if (sensor == null) return label ?? "";
        if (sensor.IsText) return string.IsNullOrWhiteSpace(label)
            ? $"{{{sensor.Id}}}"
            : $"{label} {{{sensor.Id}}}";

        string value = $"{{{sensor.Id}:{FormatFor(sensor)}}}{UnitSuffix(sensor.Unit)}";
        return string.IsNullOrWhiteSpace(label) ? value : $"{label}  {value}";
    }

    /// <summary>The number in the middle of a gauge — no label, the caption carries that.</summary>
    private static string CentreTemplate(SensorDescriptor? sensor)
    {
        if (sensor == null) return "";
        if (sensor.IsText) return $"{{{sensor.Id}}}";
        return $"{{{sensor.Id}:{FormatFor(sensor)}}}{UnitSuffix(sensor.Unit)}";
    }

    /// <summary>
    /// How many decimals a sensor deserves. A percentage or a temperature wants
    /// none — "47%" not "47.3%" — while a clock rate in GHz is useless without
    /// one. Chosen from the unit rather than the value, so it does not change
    /// as the reading moves.
    /// </summary>
    private static string FormatFor(SensorDescriptor s) => s.Unit switch
    {
        "%" or "°C" or "W" or "MHz" or "rpm" => "0",
        "GHz" or "GB" or "MB/s" => "0.0",
        "h" => "0.0",
        _ => s.Max <= 10 ? "0.0" : "0",
    };

    /// <summary>
    /// Whether the unit is written against the number or after a space.
    /// "47%" and "62°C" read as one token; "243 W" and "20.3 GB" do not.
    /// </summary>
    private static string UnitSuffix(string unit) => unit switch
    {
        "" => "",
        "%" or "°C" => unit,
        _ => " " + unit,
    };

    /// <summary>
    /// Gives a load or temperature its green-amber-red ramp — but only when the
    /// spec did not name a colour.
    ///
    /// An explicit accent has to win, or asking for a blue GPU gauge silently
    /// produces a green one, since almost every interesting sensor is a
    /// percentage or a temperature and would otherwise always be ramped. The
    /// ramp remains the default because it is the more useful answer when
    /// nobody expressed a preference.
    /// </summary>
    private static void ApplyRamp(List<ColourStop> stops, SensorDescriptor? sensor,
        LayerSpec spec, out string? source)
    {
        source = null;
        if (!string.IsNullOrWhiteSpace(spec.Accent)) return;
        if (sensor == null || sensor.IsText) return;
        if (!AccentPalette.HigherIsWorse(sensor.Unit)) return;

        stops.Clear();
        stops.AddRange(AccentPalette.Ramp(sensor.Min, sensor.Max));
        source = sensor.Id;
    }

    // -----------------------------------------------------------------------
    // Tolerant parsing
    //
    // Every one of these takes text written by a language model and returns
    // something usable. None of them throw; an unrecognised value is a default,
    // because a layer in roughly the right place beats no layer at all.
    // -----------------------------------------------------------------------

    private enum Size { Small, Medium, Large }

    public static LayerAnchor ParseAnchor(string? anchor)
    {
        string a = Normalise(anchor).Replace('_', '-').Replace(' ', '-');

        // "centre" and "center" both, and either order of the two words.
        bool top = a.Contains("top") || a.Contains("upper");
        bool bottom = a.Contains("bottom") || a.Contains("lower");
        bool left = a.Contains("left");
        bool right = a.Contains("right");

        if (top && left) return LayerAnchor.TopLeft;
        if (top && right) return LayerAnchor.TopRight;
        if (bottom && left) return LayerAnchor.BottomLeft;
        if (bottom && right) return LayerAnchor.BottomRight;
        if (top) return LayerAnchor.TopCentre;
        if (bottom) return LayerAnchor.BottomCentre;
        if (left) return LayerAnchor.MiddleLeft;
        if (right) return LayerAnchor.MiddleRight;

        return LayerAnchor.Centre;
    }

    private static Size ParseSize(string? size) => Normalise(size) switch
    {
        "small" or "s" or "tiny" or "compact" => Size.Small,
        "large" or "l" or "big" or "huge" => Size.Large,
        _ => Size.Medium,
    };

    /// <summary>
    /// Text hugs the edge it is anchored to, so a right-anchored readout stays
    /// put as its value changes width instead of drifting.
    /// </summary>
    private static TextAlign AlignFor(LayerAnchor anchor) => anchor switch
    {
        LayerAnchor.TopRight or LayerAnchor.MiddleRight or LayerAnchor.BottomRight
            => TextAlign.Right,
        LayerAnchor.TopCentre or LayerAnchor.Centre or LayerAnchor.BottomCentre
            => TextAlign.Centre,
        _ => TextAlign.Left,
    };

    private static SensorDescriptor? FindSensor(string? id, SensorSnapshot sensors)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;

        string want = id.Trim().TrimStart('{').TrimEnd('}');

        // A model that wrote "{gpu.temp:0}" instead of "gpu.temp" meant the
        // sensor; take the part before any format specifier.
        int colon = want.IndexOf(':');
        if (colon >= 0) want = want[..colon];

        foreach (SensorDescriptor d in sensors.Descriptors)
            if (string.Equals(d.Id, want, StringComparison.OrdinalIgnoreCase)) return d;

        return null;
    }

    /// <summary>
    /// Roughly how wide rendered text will be, for layout. Deliberately an
    /// estimate: measuring properly needs a FormattedText and therefore a
    /// dispatcher, and this runs off the UI thread. Tokens are assumed to
    /// render at about four characters, which is what most sensor values are.
    /// </summary>
    private static double EstimateTextWidth(string template, double fontSize)
    {
        int chars = 0;
        bool inToken = false;

        foreach (char c in template)
        {
            if (c == '{') { inToken = true; chars += 4; continue; }
            if (c == '}') { inToken = false; continue; }
            if (!inToken) chars++;
        }

        // 0.56 em per character is close for Segoe UI at these sizes.
        return Math.Max(40, chars * fontSize * 0.56);
    }

    private static string Normalise(string? s) =>
        string.IsNullOrWhiteSpace(s) ? "" : s.Trim().ToLowerInvariant();
}
