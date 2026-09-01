using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace JLDisplayManager.Services.Overlay.Ai;

/// <summary>
/// One layer as a language model describes it.
///
/// This is deliberately a fraction of <see cref="Models.Overlay.OverlayLayer"/>:
/// around eight fields instead of forty, none of them geometry, none of them a
/// colour. Everything else is filled in by <see cref="LayerFactory"/> from the
/// kind, the sensor's own unit and range, and the requested size.
///
/// The point is not brevity for its own sake. A field the model never writes is
/// a field it can never get wrong, so every produced layer is valid by
/// construction rather than by validation. What it costs is reach: a 200°
/// anticlockwise gauge with flat caps cannot be asked for here. That is what the
/// editor is for.
///
/// Every property is tolerant of nonsense — see the Parse helpers on
/// <see cref="LayerFactory"/> — because this is the one place in the app whose
/// input is written by a language model.
/// </summary>
public sealed class LayerSpec
{
    /// <summary>text | bar | gauge | graph | panel | icon.</summary>
    [JsonPropertyName("kind")]
    public string? Kind { get; set; }

    /// <summary>A sensor id such as <c>gpu.temp</c>. Checked against the live registry.</summary>
    [JsonPropertyName("sensor")]
    public string? Sensor { get; set; }

    /// <summary>Nine-point anchor: <c>top-left</c> … <c>bottom-right</c>, or <c>center</c>.</summary>
    [JsonPropertyName("anchor")]
    public string? Anchor { get; set; }

    /// <summary>small | medium | large.</summary>
    [JsonPropertyName("size")]
    public string? Size { get; set; }

    /// <summary>A short caption: "GPU", "CPU". Prefixes a readout, captions a gauge.</summary>
    [JsonPropertyName("label")]
    public string? Label { get; set; }

    /// <summary>
    /// An explicit template such as <c>{cpu.load:0}%</c>, for when the model
    /// wants a shape the default would not produce. Optional; the factory builds
    /// a sensible one from the sensor when this is absent.
    /// </summary>
    [JsonPropertyName("template")]
    public string? Template { get; set; }

    /// <summary>neutral | good | cool | warm | hot. A role, never a hex code.</summary>
    [JsonPropertyName("accent")]
    public string? Accent { get; set; }

    /// <summary>
    /// Which icon, for <c>kind: "icon"</c>. A name from
    /// <see cref="IconNames"/> — <c>thermometer</c>, <c>cpu</c>, <c>wifi</c> —
    /// never a codepoint.
    /// </summary>
    [JsonPropertyName("icon")]
    public string? Icon { get; set; }

    /// <summary>
    /// Layers sharing a group are laid out as one cluster, and a panel in a
    /// group is sized to fit the rest of it. This is how "GPU usage bottom left"
    /// becomes a readout with a bar under it rather than two overlapping things.
    /// </summary>
    [JsonPropertyName("group")]
    public string? Group { get; set; }

    /// <summary>Optional nudge after placement, in design pixels. Clamped.</summary>
    [JsonPropertyName("offset")]
    public double[]? Offset { get; set; }

    /// <summary>
    /// Optional per-layer styling. Absent — which it usually is — leaves the
    /// layer exactly as the factory and the theme would have made it.
    /// </summary>
    [JsonPropertyName("style")]
    public LayerStyle? Style { get; set; }
}

/// <summary>
/// The handful of visual choices worth varying per layer.
///
/// This exists because the renderer could already do gradients, segmented bars,
/// gauge ticks, text outlines and per-layer fonts, and the AI had no field that
/// could ask for any of them: eight writable fields against ninety-six
/// implemented properties. So the largest available improvement was not new
/// drawing code but a way to reach the drawing code already there.
///
/// Kept to a dozen fields on purpose. Every one is optional, every one is
/// validated against a named set or clamped to a range, and anything absent
/// falls through to the theme and then to <see cref="LayerFactory"/>'s
/// defaults. A spec with no style block produces byte-for-byte what it produced
/// before this existed.
///
/// Still not exposed, and deliberately: pixel geometry (the layout engine owns
/// that), literal colours (the theme owns those), and the long tail of
/// properties the editor exists for.
/// </summary>
public sealed class LayerStyle
{
    /// <summary>
    /// An accent role, or two joined by <c>-&gt;</c> for a gradient:
    /// <c>"warm"</c>, <c>"cool-&gt;hot"</c>. Never a colour — see
    /// <see cref="AccentPalette"/>.
    /// </summary>
    [JsonPropertyName("fill")]
    public string? Fill { get; set; }

    /// <summary>The unfilled part of a bar or gauge.</summary>
    [JsonPropertyName("track")]
    public string? Track { get; set; }

    /// <summary>0 is a continuous bar; anything else gives the blocky VU look.</summary>
    [JsonPropertyName("segments")]
    public int? Segments { get; set; }

    /// <summary>Corner radius. Omit to follow the theme.</summary>
    [JsonPropertyName("radius")]
    public double? Radius { get; set; }

    /// <summary>Text outline width. Cheap, despite what §7.1 originally assumed.</summary>
    [JsonPropertyName("outline")]
    public double? Outline { get; set; }

    /// <summary>A translucent card behind text, for legibility over bright video.</summary>
    [JsonPropertyName("pill")]
    public bool? Pill { get; set; }

    /// <summary>
    /// A soft halo behind text, in pixels. 0 for none.
    ///
    /// The one effect that makes text look lit rather than pasted on, which is
    /// most of what "HUD" or "neon" is asking for.
    /// </summary>
    [JsonPropertyName("glow")]
    public double? Glow { get; set; }

    /// <summary>A font role: <c>default</c>, <c>condensed</c>, <c>mono</c>, <c>display</c>.</summary>
    [JsonPropertyName("font")]
    public string? Font { get; set; }

    [JsonPropertyName("bold")]
    public bool? Bold { get; set; }

    [JsonPropertyName("opacity")]
    public double? Opacity { get; set; }

    /// <summary>Degrees clockwise about the layer's own centre.</summary>
    [JsonPropertyName("rotate")]
    public double? Rotate { get; set; }

    /// <summary>Gauge tick marks. 0 for none.</summary>
    [JsonPropertyName("ticks")]
    public int? Ticks { get; set; }

    /// <summary>How far a gauge sweeps, in degrees. 270 is the classic dial.</summary>
    [JsonPropertyName("sweep")]
    public double? Sweep { get; set; }

    /// <summary>
    /// What a <c>panel</c> draws: <c>card</c> (the default), <c>ring</c>,
    /// <c>arc</c>, <c>bracket</c>, <c>rule</c>, <c>chevron</c>.
    ///
    /// Ornament rides on the existing <c>panel</c> kind rather than becoming
    /// five more kinds, which keeps the vocabulary the model has to hold in mind
    /// short. For a <c>graph</c> it means something else — <c>line</c>,
    /// <c>area</c> or <c>bars</c> — which is the same trick again.
    /// </summary>
    [JsonPropertyName("shape")]
    public string? Shape { get; set; }
}

/// <summary>What the model returns for one prompt.</summary>
public sealed class OverlayPlan
{
    /// <summary>
    /// <c>add</c> keeps the current profile and appends; <c>replace</c> rebuilds
    /// it. The model picks from the wording — "add a clock" against "create a
    /// full overlay" — and the user can override before anything is applied.
    /// </summary>
    [JsonPropertyName("intent")]
    public string? Intent { get; set; }

    /// <summary>One line on what it did, shown in the result banner.</summary>
    [JsonPropertyName("note")]
    public string? Note { get; set; }

    /// <summary>
    /// Which <see cref="Models.Overlay.OverlayTheme"/> the overlay should use.
    ///
    /// Named once per plan rather than styled per layer, which is what lets one
    /// word produce a coherent look — and is why "make it look like a HUD" is
    /// answerable at all. Only applied on a <c>replace</c>: changing the theme
    /// while adding a layer would restyle work the request never mentioned.
    /// </summary>
    [JsonPropertyName("theme")]
    public string? Theme { get; set; }

    [JsonPropertyName("layers")]
    public List<LayerSpec> Layers { get; set; } = new();
}
