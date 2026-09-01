using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Windows.Media;

using JLDisplayManager.Models.Overlay;

namespace JLDisplayManager.Services.Overlay;

/// <summary>
/// Turns the colour strings a profile stores into frozen WPF brushes and pens.
///
/// Colours are text in the model on purpose: a profile is JSON people can read,
/// hand-edit and share, and "#CC000000" says more than a packed integer. The
/// cost is a parse, which is why everything here is cached and frozen — the
/// same handful of colours is asked for several times per layer per frame, and
/// frozen brushes can cross the render and UI threads.
/// </summary>
public static class Palette
{
    private static readonly ConcurrentDictionary<string, Brush?> Brushes = new();
    private static readonly ConcurrentDictionary<(string, double), Pen?> Pens = new();

    /// <summary>
    /// Turns whatever a layer stored into a literal colour.
    ///
    /// A layer may hold a role name — <c>"warm"</c> — or a literal
    /// <c>#AARRGGBB</c>. Roles resolve through the theme, which is what lets one
    /// theme change restyle a whole profile; anything else passes through
    /// untouched, which is what keeps every profile written before themes
    /// existed rendering exactly as it did.
    ///
    /// Resolving to a literal before caching matters: the cache is keyed on the
    /// colour string, so caching a role name directly would serve one theme's
    /// amber to another.
    /// </summary>
    public static string? Literal(string? colour, OverlayTheme? theme)
    {
        if (string.IsNullOrWhiteSpace(colour)) return null;

        // A literal always wins, so a hand-picked colour is never reinterpreted
        // as a role by a theme that happens to define that word.
        if (colour[0] == '#') return colour;

        return (theme ?? OverlayTheme.Minimal).Role(colour) ?? colour;
    }

    /// <summary>A brush for a role name or a literal colour. See <see cref="Literal"/>.</summary>
    public static Brush? Brush(string? colour, OverlayTheme? theme) =>
        Brush(Literal(colour, theme));

    public static Pen? Pen(string? colour, double width, OverlayTheme? theme) =>
        Pen(Literal(colour, theme), width);

    /// <summary>
    /// Accepts "#AARRGGBB", "#RRGGBB", "#ARGB", "#RGB" and the standard colour
    /// names. Null or unparseable gives null, which every caller treats as
    /// "draw nothing" rather than as an error.
    /// </summary>
    public static Brush? Brush(string? colour)
    {
        if (string.IsNullOrWhiteSpace(colour)) return null;

        return Brushes.GetOrAdd(colour, c =>
        {
            try
            {
                var brush = new SolidColorBrush(Parse(c));
                brush.Freeze();
                return brush;
            }
            catch
            {
                return null;
            }
        });
    }

    public static Pen? Pen(string? colour, double width)
    {
        if (string.IsNullOrWhiteSpace(colour) || width <= 0) return null;

        return Pens.GetOrAdd((colour, width), key =>
        {
            Brush? b = Brush(key.Item1);
            if (b == null) return null;

            var pen = new Pen(b, key.Item2);
            pen.Freeze();
            return pen;
        });
    }

    public static Color Parse(string colour)
    {
        string s = colour.Trim();

        if (s.StartsWith('#'))
        {
            string hex = s[1..];

            // Shorthand: #RGB and #ARGB, each nibble doubled, as CSS does it.
            if (hex.Length is 3 or 4)
            {
                var expanded = new char[hex.Length * 2];
                for (int i = 0; i < hex.Length; i++)
                {
                    expanded[i * 2] = hex[i];
                    expanded[i * 2 + 1] = hex[i];
                }
                hex = new string(expanded);
            }

            if (hex.Length == 6) hex = "FF" + hex;

            if (hex.Length == 8
                && uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture,
                                 out uint v))
            {
                return Color.FromArgb(
                    (byte)((v >> 24) & 0xFF), (byte)((v >> 16) & 0xFF),
                    (byte)((v >> 8) & 0xFF), (byte)(v & 0xFF));
            }

            throw new FormatException($"'{colour}' is not a colour");
        }

        object parsed = ColorConverter.ConvertFromString(s)
                        ?? throw new FormatException($"'{colour}' is not a colour");
        return (Color)parsed;
    }
}
