using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace JLDisplayManager.Models.Overlay;

/// <summary>
/// Which corner or edge a layer's position is measured from. Anchoring rather
/// than always measuring from the top left means a readout pinned to the bottom
/// right stays there, and a profile reads the way it was designed.
/// </summary>
public enum LayerAnchor
{
    TopLeft, TopCentre, TopRight,
    MiddleLeft, Centre, MiddleRight,
    BottomLeft, BottomCentre, BottomRight,
}

/// <summary>When a layer is drawn at all.</summary>
public enum VisibilityRule
{
    Always,

    /// <summary>Only while something is on the panel.</summary>
    WhilePlaying,

    /// <summary>Only while nothing is — a screensaver-ish clock, say.</summary>
    WhileIdle,

    /// <summary>Only once a sensor passes a threshold: a warning that stays out of the way.</summary>
    SensorAbove,

    SensorBelow,
}

public enum TextAlign { Left, Centre, Right }

/// <summary>How a <see cref="GraphLayer"/> draws its window of history.</summary>
public enum GraphStyle
{
    /// <summary>A stroked line. The most readable at small sizes.</summary>
    Line,

    /// <summary>The line with the area under it filled. Reads at a glance.</summary>
    Area,

    /// <summary>One column per sample. Good for spiky sources.</summary>
    Bars,
}

public enum BarOrientation { Horizontal, Vertical }

/// <summary>
/// What a <see cref="ShapeLayer"/> draws.
///
/// The first three are structure — backing cards and dividers. The rest are
/// ornament, and ornament is most of what separates a layout that looks
/// composed from one that looks stacked.
/// </summary>
public enum ShapeKind
{
    Rectangle,
    Ellipse,
    Line,

    /// <summary>An unfilled circle. Behind a gauge, or as a bare marker.</summary>
    Ring,

    /// <summary>A ring with a gap — decorative, with no value bound to it.</summary>
    Arc,

    /// <summary>Four corner marks framing a cluster. The HUD look.</summary>
    Bracket,

    /// <summary>A hairline divider between clusters, optionally fading at both ends.</summary>
    Rule,

    /// <summary>A direction mark, for flanking a readout. Turn it with Rotation.</summary>
    Chevron,
}

/// <summary>
/// A colour that applies from a value upwards. A layer with stops at 0 green,
/// 70 amber and 85 red goes red at 85 and stays red — the highest stop that the
/// value has reached wins.
/// </summary>
public sealed class ColourStop
{
    public double AtOrAbove { get; set; }

    /// <summary>"#RRGGBB" or "#AARRGGBB".</summary>
    public string Colour { get; set; } = "#FFFFFFFF";
}

/// <summary>
/// Everything common to a drawable layer.
///
/// Deliberately a plain object with no WPF types anywhere in it: the renderer
/// runs on its own thread and WPF objects have thread affinity, so a Brush
/// stored here could not be drawn by both the render thread and the editor.
/// Colours are strings, which also makes a profile readable and hand-editable.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(TextLayer), "text")]
[JsonDerivedType(typeof(BarLayer), "bar")]
[JsonDerivedType(typeof(GaugeLayer), "gauge")]
[JsonDerivedType(typeof(ShapeLayer), "shape")]
[JsonDerivedType(typeof(ImageLayer), "image")]
[JsonDerivedType(typeof(GlyphLayer), "glyph")]
[JsonDerivedType(typeof(GraphLayer), "graph")]
public abstract class OverlayLayer
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>What the layer list calls it. Free text; never used as a key.</summary>
    public string Name { get; set; } = "";

    public bool Enabled { get; set; } = true;

    /// <summary>Editor-only: a locked layer cannot be dragged by accident.</summary>
    public bool Locked { get; set; }

    public LayerAnchor Anchor { get; set; } = LayerAnchor.TopLeft;

    /// <summary>Offset from the anchor, in panel pixels.</summary>
    public double X { get; set; }

    public double Y { get; set; }

    public double Width { get; set; } = 200;

    public double Height { get; set; } = 40;

    /// <summary>Degrees clockwise, about the layer's own centre.</summary>
    public double Rotation { get; set; }

    public double Opacity { get; set; } = 1.0;

    public VisibilityRule VisibleWhen { get; set; } = VisibilityRule.Always;

    /// <summary>The sensor tested by SensorAbove / SensorBelow.</summary>
    public string? VisibleSource { get; set; }

    public double VisibleThreshold { get; set; }

    /// <summary>
    /// Resolves the anchor into a top-left corner in panel space. Kept here so
    /// the renderer and the editor's hit-testing can never disagree about where
    /// a layer actually is.
    /// </summary>
    public (double X, double Y) TopLeft(double panelWidth, double panelHeight)
    {
        double x = Anchor switch
        {
            LayerAnchor.TopLeft or LayerAnchor.MiddleLeft or LayerAnchor.BottomLeft => X,
            LayerAnchor.TopCentre or LayerAnchor.Centre or LayerAnchor.BottomCentre
                => (panelWidth - Width) / 2 + X,
            _ => panelWidth - Width - X,
        };

        double y = Anchor switch
        {
            LayerAnchor.TopLeft or LayerAnchor.TopCentre or LayerAnchor.TopRight => Y,
            LayerAnchor.MiddleLeft or LayerAnchor.Centre or LayerAnchor.MiddleRight
                => (panelHeight - Height) / 2 + Y,
            _ => panelHeight - Height - Y,
        };

        return (x, y);
    }

    /// <summary>
    /// Every sensor this layer reads. The renderer uses it to decide whether
    /// anything has actually changed since the last frame — a profile whose
    /// values are all static costs nothing to keep on screen.
    /// </summary>
    public abstract IEnumerable<string> Sources();
}
