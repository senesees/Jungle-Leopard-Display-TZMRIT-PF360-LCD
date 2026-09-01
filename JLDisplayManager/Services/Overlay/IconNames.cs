using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media;

namespace JLDisplayManager.Services.Overlay;

/// <summary>
/// Named icons, drawn from the icon font Windows already ships.
///
/// Names rather than codepoints for the same reason colours are roles: a model
/// asked for <c>U+E9CA</c> would invent one, and an invented codepoint renders
/// as an empty box rather than as an error. <c>thermometer</c> is guessable,
/// checkable, and stays meaningful if the underlying glyph ever moves.
///
/// Every codepoint below was chosen by rendering the font's private use area to
/// a labelled sheet and looking at it — not from memory. That matters: the
/// obvious guesses are wrong often enough that a map built from recollection
/// would draw a keyboard for "cpu".
/// </summary>
public static class IconNames
{
    /// <summary>
    /// Windows 11's icon font. Windows 10 has <see cref="LegacyFont"/> instead,
    /// which shares most of this range.
    /// </summary>
    public const string Font = "Segoe Fluent Icons";

    public const string LegacyFont = "Segoe MDL2 Assets";

    private static readonly Dictionary<string, int> Map =
        new(StringComparer.OrdinalIgnoreCase)
    {
        // Silicon
        ["cpu"] = 0xE950,
        ["processor"] = 0xE950,
        ["chip"] = 0xE950,
        ["memory"] = 0xE964,
        ["ram"] = 0xE964,

        // Temperature and power
        ["thermometer"] = 0xE9CA,
        ["temperature"] = 0xE9CA,
        ["temp"] = 0xE9CA,
        ["heat"] = 0xE9CA,
        ["power"] = 0xE7E8,
        ["lightning"] = 0xE945,
        ["bolt"] = 0xE945,
        ["flash"] = 0xE945,

        // Moving parts. Fluent Icons has no fan or pump glyph, and gears are
        // the closest honest stand-in — mechanical and rotating. Named here
        // rather than silently dropped, because a hardware panel asks for these
        // constantly and an empty space would read as a bug.
        ["fan"] = 0xE9F5,
        ["pump"] = 0xE9F5,
        ["gears"] = 0xE9F5,

        // Displays and storage
        ["gpu"] = 0xE7F4,
        ["monitor"] = 0xE7F4,
        ["display"] = 0xE7F4,
        ["screen"] = 0xE7F4,
        ["disk"] = 0xEC59,
        ["drive"] = 0xEC59,
        ["storage"] = 0xEC59,
        ["folder"] = 0xE8B7,

        // Network
        ["network"] = 0xEC27,
        ["globe"] = 0xEC27,
        ["internet"] = 0xEC27,
        ["wifi"] = 0xEC3F,
        ["bluetooth"] = 0xEC41,
        ["signal"] = 0xEC3B,
        ["download"] = 0xE896,
        ["upload"] = 0xE898,
        ["transfer"] = 0xE8CB,

        // Time
        ["clock"] = 0xE917,
        ["time"] = 0xE917,
        ["timer"] = 0xE916,
        ["stopwatch"] = 0xE916,
        ["calendar"] = 0xE787,
        ["date"] = 0xE787,

        // Status and measurement
        ["warning"] = 0xE7BA,
        ["alert"] = 0xE7BA,
        ["speed"] = 0xEC48,
        ["speedometer"] = 0xEC48,
        ["gauge"] = 0xEC48,
        ["chart"] = 0xE9D2,
        ["graph"] = 0xE9D2,
        ["activity"] = 0xE9D9,
        ["pulse"] = 0xE9D9,
        ["heart"] = 0xE95E,
        ["settings"] = 0xE9E9,
        ["sliders"] = 0xE9E9,
        ["gamepad"] = 0xE7FC,
        ["game"] = 0xE7FC,
        ["volume"] = 0xE995,
        ["sound"] = 0xE995,
        ["headphones"] = 0xE7F6,
    };

    /// <summary>Every name, for the editor's picker and the AI's prompt.</summary>
    public static IReadOnlyList<string> All { get; } =
        Map.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();

    /// <summary>
    /// The glyph for a name, or null if it is not one we publish. Null is the
    /// caller's cue to drop the layer with a note rather than draw a box.
    /// </summary>
    public static string? Glyph(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;

        return Map.TryGetValue(name.Trim(), out int code)
            ? char.ConvertFromUtf32(code)
            : null;
    }

    /// <summary>Whether a name is one we publish, without building the string.</summary>
    public static bool Known(string? name) =>
        !string.IsNullOrWhiteSpace(name) && Map.ContainsKey(name.Trim());

    // -----------------------------------------------------------------------

    private static string? _resolved;

    /// <summary>
    /// Which icon font this machine actually has, resolved once.
    ///
    /// Fluent on Windows 11, MDL2 on Windows 10. Empty when neither is present,
    /// which the renderer treats as "draw no icons" — a missing glyph is a blank
    /// space, and a blank space is better than a column of empty boxes.
    /// </summary>
    public static string ResolveFont()
    {
        if (_resolved != null) return _resolved;

        foreach (string candidate in new[] { Font, LegacyFont })
        {
            if (Installed(candidate)) return _resolved = candidate;
        }

        Models.Storage.Log("icons: neither Segoe Fluent Icons nor Segoe MDL2 Assets "
                           + "is installed; icon layers will not draw");
        return _resolved = "";
    }

    private static bool Installed(string family)
    {
        foreach (FontFamily f in Fonts.SystemFontFamilies)
            if (string.Equals(f.Source, family, StringComparison.OrdinalIgnoreCase)) return true;

        return false;
    }

    /// <summary>
    /// Whether the resolved font actually has a glyph for this name. Used by the
    /// verification tool; the renderer does not need it, since a missing glyph
    /// simply draws nothing.
    /// </summary>
    public static bool HasGlyph(string name)
    {
        if (!Map.TryGetValue(name, out int code)) return false;

        string family = ResolveFont();
        if (family.Length == 0) return false;

        FontFamily? f = Fonts.SystemFontFamilies
            .FirstOrDefault(x => string.Equals(x.Source, family, StringComparison.OrdinalIgnoreCase));

        if (f == null) return false;

        foreach (Typeface tf in f.GetTypefaces())
            if (tf.TryGetGlyphTypeface(out GlyphTypeface? gt) && gt != null)
                return gt.CharacterToGlyphMap.ContainsKey(code);

        return false;
    }
}
