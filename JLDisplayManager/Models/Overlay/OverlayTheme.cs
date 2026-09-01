using System;
using System.Collections.Generic;

namespace JLDisplayManager.Models.Overlay;

/// <summary>
/// One coherent look: a palette, a font, a corner radius, a spacing multiplier.
///
/// This is what makes a generated overlay look designed rather than assembled.
/// A profile whose four greens differ by a few points reads as accidental, and
/// no amount of good layout fixes that — so colours are named roles resolved
/// here rather than values chosen per layer.
///
/// Themes are resolved at DRAW time, not when a layer is made. A layer stores
/// <c>"warm"</c> and the renderer asks the active theme what that means, which
/// is why switching theme restyles a whole profile instead of only affecting
/// whatever is generated next.
///
/// A layer may still store a literal <c>#AARRGGBB</c>; anything that is not a
/// known role is passed through untouched. That is what keeps every profile
/// written before themes existed rendering exactly as it did.
/// </summary>
public sealed class OverlayTheme
{
    public string Name { get; init; } = "minimal";

    /// <summary>Shown in the editor's theme picker and to the AI.</summary>
    public string Description { get; init; } = "";

    // -----------------------------------------------------------------------
    // Ink
    // -----------------------------------------------------------------------

    public string Text { get; init; } = "#FFFFFFFF";

    /// <summary>Captions and secondary readouts.</summary>
    public string TextDim { get; init; } = "#FFD0D0D0";

    public string Good { get; init; } = "#FF4AD995";
    public string Cool { get; init; } = "#FF5AB6FF";
    public string Warm { get; init; } = "#FFFFB43A";
    public string Hot { get; init; } = "#FFFF5A52";

    /// <summary>Backing card behind a cluster.</summary>
    public string Panel { get; init; } = "#73000000";

    /// <summary>The unfilled part of a bar or gauge.</summary>
    public string Track { get; init; } = "#60FFFFFF";

    /// <summary>Rules, brackets and other ornament.</summary>
    public string Line { get; init; } = "#50FFFFFF";

    // -----------------------------------------------------------------------
    // Shape
    // -----------------------------------------------------------------------

    /// <summary>A font role — see <see cref="FontRoles"/>. Not a font name.</summary>
    public string Font { get; init; } = "default";

    public double CornerRadius { get; init; } = 10;

    /// <summary>
    /// Multiplies every margin and gutter in the layout. Lets a dense theme
    /// pack tightly and an airy one breathe, from one number rather than a
    /// second set of constants in the layout engine.
    /// </summary>
    public double Density { get; init; } = 1.0;

    /// <summary>
    /// Whether text gets a shadow by default. Cheap legibility over video, and
    /// something a flat theme may not want.
    /// </summary>
    public bool TextShadow { get; init; } = true;

    // -----------------------------------------------------------------------
    // Roles
    // -----------------------------------------------------------------------

    /// <summary>
    /// What a role name means in this theme, or null if it is not a role — in
    /// which case the caller should treat the string as a literal colour.
    /// </summary>
    public string? Role(string? name) => Normalise(name) switch
    {
        "text" or "neutral" or "default" => Text,
        "dim" or "muted" or "grey" or "gray" or "secondary" => TextDim,
        "good" or "green" or "ok" or "safe" => Good,
        "cool" or "blue" or "cold" or "info" => Cool,
        "warm" or "amber" or "orange" or "yellow" => Warm,
        "hot" or "red" or "danger" or "alert" or "critical" => Hot,
        "panel" or "card" or "background" => Panel,
        "track" => Track,
        "line" or "rule" or "border" => Line,
        _ => null,
    };

    private static string Normalise(string? s) =>
        string.IsNullOrWhiteSpace(s) ? "" : s.Trim().ToLowerInvariant();

    // -----------------------------------------------------------------------
    // The shipped set
    // -----------------------------------------------------------------------

    /// <summary>
    /// Today's look, to the value. Every profile written before themes existed
    /// resolves to exactly this, which is what makes the change invisible until
    /// somebody asks for a different one.
    /// </summary>
    public static readonly OverlayTheme Minimal = new()
    {
        Name = "minimal",
        Description = "Dark cards, soft corners, green through red. The default.",
    };

    public static readonly OverlayTheme Hud = new()
    {
        Name = "hud",
        Description = "Thin strokes, sharp corners, cyan and amber. Aircraft display.",

        Text = "#FFE8F6FF",
        TextDim = "#FF7FA8BF",
        Good = "#FF5AE6C8",
        Cool = "#FF4FC3F7",
        Warm = "#FFFFC14D",
        Hot = "#FFFF6E5A",
        Panel = "#59001A24",
        Track = "#334FC3F7",
        Line = "#994FC3F7",

        Font = "condensed",
        CornerRadius = 0,
        Density = 0.9,
    };

    public static readonly OverlayTheme Terminal = new()
    {
        Name = "terminal",
        Description = "Monospaced green on near-black. Square, dense, no ornament.",

        Text = "#FF6BE675",
        TextDim = "#FF3E8C46",
        Good = "#FF6BE675",
        Cool = "#FF6BE675",   // one ink on purpose: a terminal has no palette
        Warm = "#FFD8E64A",
        Hot = "#FFE6564A",
        Panel = "#A6000A02",
        Track = "#2E6BE675",
        Line = "#596BE675",

        Font = "mono",
        CornerRadius = 0,
        Density = 0.8,
        TextShadow = false,
    };

    public static readonly OverlayTheme Neon = new()
    {
        Name = "neon",
        Description = "Saturated magenta and cyan on deep translucent panels.",

        Text = "#FFFFFFFF",
        TextDim = "#FFB78CD9",
        Good = "#FF3DF5C4",
        Cool = "#FF41D6FF",
        Warm = "#FFFF4FD8",
        Hot = "#FFFF3B6B",
        Panel = "#8C14001F",
        Track = "#38FF4FD8",
        Line = "#A641D6FF",

        Font = "display",
        CornerRadius = 16,
        Density = 1.15,
    };

    public static readonly IReadOnlyList<OverlayTheme> All =
        new[] { Minimal, Hud, Terminal, Neon };

    /// <summary>
    /// A theme by name. Anything unrecognised — including null — gives
    /// <see cref="Minimal"/>, so a hand-edited profile naming a theme that does
    /// not exist still renders rather than failing.
    /// </summary>
    public static OverlayTheme ByName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return Minimal;

        foreach (OverlayTheme t in All)
            if (string.Equals(t.Name, name.Trim(), StringComparison.OrdinalIgnoreCase))
                return t;

        return Minimal;
    }
}

/// <summary>
/// Font roles, so neither a theme nor the AI ever names a typeface directly.
///
/// A model asked for a font name invents one that is not installed and the
/// failure is silent — the text simply renders in something else. A role always
/// resolves to something present.
/// </summary>
public static class FontRoles
{
    /// <summary>
    /// Every role falls back to Segoe UI, which ships with Windows. Bahnschrift
    /// and Cascadia are present on Windows 10 1709 onwards and 11 respectively;
    /// a machine without them gets the fallback rather than a blank layer.
    /// </summary>
    public static string Resolve(string? role) => Normalise(role) switch
    {
        "condensed" or "narrow" => "Bahnschrift Condensed, Bahnschrift, Segoe UI",

        // A proportional font makes a changing number shuffle sideways, which
        // at 30 fps is genuinely distracting. This is what a live readout wants.
        "mono" or "monospace" or "code" => "Cascadia Mono, Consolas, Segoe UI",

        "display" or "headline" => "Segoe UI Variable Display, Segoe UI",
        _ => "Segoe UI",
    };

    private static string Normalise(string? s) =>
        string.IsNullOrWhiteSpace(s) ? "" : s.Trim().ToLowerInvariant();
}
