using System;
using System.Globalization;
using System.Text;

using JLDisplayManager.Services.Sensors;

namespace JLDisplayManager.Services.Overlay;

/// <summary>
/// Turns a template such as <c>CPU {cpu.load:0}%  {gpu.temp:0}°C</c> into text.
///
/// Two rules matter more than the syntax. An unknown or unavailable source
/// renders as <c>--</c>, never as an exception and never as the raw token: a
/// profile written on a machine with an NVIDIA card must still draw on one
/// without. And a bad format string degrades to the default rather than
/// throwing, because the template is user input and a typo should not blank the
/// panel.
/// </summary>
public static class TokenFormatter
{
    /// <summary>Shown wherever a value cannot be had.</summary>
    public const string Unavailable = "--";

    public static string Format(string? template, SensorSnapshot values)
    {
        if (string.IsNullOrEmpty(template)) return string.Empty;
        if (template.IndexOf('{') < 0) return template;

        var sb = new StringBuilder(template.Length + 16);

        for (int i = 0; i < template.Length; i++)
        {
            char c = template[i];

            if (c == '{')
            {
                // "{{" is a literal brace, so a template can contain one.
                if (i + 1 < template.Length && template[i + 1] == '{') { sb.Append('{'); i++; continue; }

                int end = template.IndexOf('}', i + 1);
                if (end < 0) { sb.Append(template[i..]); break; }   // unclosed: emit as written

                sb.Append(Resolve(template[(i + 1)..end], values));
                i = end;
                continue;
            }

            if (c == '}' && i + 1 < template.Length && template[i + 1] == '}')
            {
                sb.Append('}');
                i++;
                continue;
            }

            sb.Append(c);
        }

        return sb.ToString();
    }

    private static string Resolve(string body, SensorSnapshot values)
    {
        int colon = body.IndexOf(':');
        string id = (colon >= 0 ? body[..colon] : body).Trim();
        string format = colon >= 0 ? body[(colon + 1)..] : string.Empty;

        if (id.Length == 0) return string.Empty;

        SensorReading r = values[id];
        if (!r.Available) return Unavailable;

        // A text source ignores any format: "{time.now:0.0}" is a mistake, and
        // showing the clock is more useful than showing the mistake.
        if (r.Text != null) return r.Text;

        try
        {
            return format.Length > 0
                ? r.Value.ToString(format, CultureInfo.CurrentCulture)
                : Default(r.Value);
        }
        catch (FormatException)
        {
            return Default(r.Value);
        }
    }

    /// <summary>
    /// What to show when the template did not say. Chosen so a bare {token} is
    /// usually right: percentages and temperatures want no decimals, a figure
    /// under ten usually wants one, and a rate under one wants two.
    /// </summary>
    private static string Default(double v)
    {
        double a = Math.Abs(v);
        if (a >= 100) return v.ToString("0", CultureInfo.CurrentCulture);
        if (a >= 10) return v.ToString("0.#", CultureInfo.CurrentCulture);
        if (a >= 1) return v.ToString("0.0", CultureInfo.CurrentCulture);
        return v.ToString("0.00", CultureInfo.CurrentCulture);
    }
}
