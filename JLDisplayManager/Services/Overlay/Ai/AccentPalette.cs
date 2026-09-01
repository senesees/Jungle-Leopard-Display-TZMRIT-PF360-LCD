using System;
using System.Collections.Generic;

using JLDisplayManager.Models.Overlay;

namespace JLDisplayManager.Services.Overlay.Ai;

/// <summary>
/// Normalises the colour words a model writes into the role names a layer
/// stores.
///
/// Note what this deliberately does NOT do any more: resolve to a hex value.
/// A generated layer stores <c>"warm"</c>, and <see cref="Palette.Literal"/>
/// asks the profile's <see cref="OverlayTheme"/> what that means at draw time.
/// That indirection is the whole reason switching a profile's theme restyles
/// every layer in it rather than only affecting whatever is generated next.
///
/// The model still never writes a colour. It writes a role, and the theme owns
/// what roles look like — which is what stops four generated greens from
/// differing by a few points and making a layout read as accidental.
/// </summary>
public static class AccentPalette
{
    public const string Neutral = "text";
    public const string Dim = "dim";
    public const string Good = "good";
    public const string Cool = "cool";
    public const string Warm = "warm";
    public const string Hot = "hot";

    /// <summary>Backing card behind a cluster.</summary>
    public const string Panel = "panel";

    /// <summary>The unfilled part of a bar or gauge.</summary>
    public const string Track = "track";

    /// <summary>
    /// A role name from whatever the model wrote. Anything unrecognised —
    /// including a hex code it wrote despite being told not to — comes back
    /// neutral rather than being trusted.
    /// </summary>
    public static string Resolve(string? accent) => Normalise(accent) switch
    {
        "good" or "green" or "ok" or "safe" => Good,
        "cool" or "blue" or "cold" or "info" => Cool,
        "warm" or "amber" or "orange" or "yellow" => Warm,
        "hot" or "red" or "danger" or "alert" or "critical" => Hot,
        "dim" or "muted" or "grey" or "gray" or "secondary" => Dim,
        _ => Neutral,
    };

    /// <summary>
    /// The green-amber-red ramp for a sensor where higher is worse.
    ///
    /// Applied to load and temperature, which is what makes a generated gauge
    /// go amber at 70 and red at 85 without the model having to know that is
    /// the convention. A sensor with no natural "bad" end — a clock rate, a
    /// network transfer — gets no ramp and keeps its flat accent.
    /// </summary>
    public static List<ColourStop> Ramp(double min, double max)
    {
        double span = max - min;
        if (span <= 0) return new List<ColourStop>();

        return new List<ColourStop>
        {
            new() { AtOrAbove = min, Colour = Good },
            new() { AtOrAbove = min + span * 0.70, Colour = Warm },
            new() { AtOrAbove = min + span * 0.87, Colour = Hot },
        };
    }

    /// <summary>
    /// Whether a sensor's high end is the bad end, from its unit. Percentages
    /// and temperatures are; gigabytes, hertz and watts are not — a fast clock
    /// or a busy network is not a warning.
    /// </summary>
    public static bool HigherIsWorse(string? unit) => unit switch
    {
        "%" or "°C" => true,
        _ => false,
    };

    private static string Normalise(string? s) =>
        string.IsNullOrWhiteSpace(s) ? "" : s.Trim().ToLowerInvariant();
}
