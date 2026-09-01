using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

using JLDisplayManager.Models.Overlay;
using JLDisplayManager.Services.Sensors;

namespace JLDisplayManager.Services.Overlay.Ai;

/// <summary>
/// Builds the instructions sent with every generation request.
///
/// Composed at call time rather than baked in, because the important half of it
/// is facts about this machine: which sensors exist, how big the surface is,
/// what is already on it. A prompt fixed at build time would offer the model
/// sensors this computer does not have, and the first thing a user would see is
/// a layer reading "--".
///
/// That sensor list is the single highest-value part of this feature. It is the
/// difference between a model guessing plausible-sounding ids and one writing
/// ids that exist.
/// </summary>
public static class OverlaySystemPrompt
{
    /// <summary>Layers past this are dropped; also told to the model up front.</summary>
    public const int MaxLayers = 20;

    public static string Build(SensorSnapshot sensors, OverlayProfile? current,
        double surfaceWidth, double surfaceHeight)
    {
        var sb = new StringBuilder(4096);

        sb.AppendLine(
            "You design overlays for a small hardware display mounted on a computer's water "
            + "cooler. The overlay draws live system statistics on top of whatever video or "
            + "image is playing.");
        sb.AppendLine();
        sb.Append(FormattableString.Invariant(
            $"The visible surface is {surfaceWidth:0} x {surfaceHeight:0} pixels"));
        sb.AppendLine(surfaceHeight > surfaceWidth ? " (portrait)." : " (landscape).");
        sb.AppendLine();

        AppendContract(sb);
        AppendFields(sb);
        AppendSensors(sb, sensors);
        AppendCurrent(sb, current);
        AppendRules(sb);
        AppendExamples(sb);

        return sb.ToString();
    }

    // -----------------------------------------------------------------------

    private static void AppendContract(StringBuilder sb)
    {
        sb.AppendLine("Reply with JSON and nothing else. No explanation, no code fence.");
        sb.AppendLine();
        sb.AppendLine("{");
        sb.AppendLine("  \"intent\": \"add\" | \"replace\",");
        sb.AppendLine("  \"theme\": \"minimal\" | \"hud\" | \"terminal\" | \"neon\",");
        sb.AppendLine("  \"note\": \"one short line describing what you made\",");
        sb.AppendLine("  \"layers\": [ { ... }, { ... } ]");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("Use \"add\" to put new things on the existing overlay, and \"replace\" "
                      + "when asked for a whole new overlay or a redesign.");
        sb.AppendLine();
        sb.AppendLine("The theme sets the palette, font and corner style for the whole "
                      + "overlay. Choose one when asked for a look or a redesign; it is "
                      + "ignored on an \"add\". Leave it out to keep the current one.");
        sb.AppendLine();

        foreach (OverlayTheme t in OverlayTheme.All)
            sb.Append("  ").Append(t.Name.PadRight(10)).AppendLine(t.Description);

        sb.AppendLine();
    }

    private static void AppendFields(StringBuilder sb)
    {
        sb.AppendLine("Each layer has these fields. All are optional except kind.");
        sb.AppendLine();
        sb.AppendLine("  kind      text | bar | gauge | graph | panel | icon");
        sb.AppendLine("            text  = a readout, e.g. \"CPU  47%\"");
        sb.AppendLine("            bar   = a horizontal level bar");
        sb.AppendLine("            gauge = a circular dial with a number in the middle");
        sb.AppendLine("            graph = a sparkline of the last minute");
        sb.AppendLine("            panel = a card behind a group, or an ornament");
        sb.AppendLine("            icon  = one symbol from the list further down");
        sb.AppendLine("  sensor    an id from the list below. Exactly as written there.");
        sb.AppendLine("  anchor    top-left | top-centre | top-right | middle-left |");
        sb.AppendLine("            center | middle-right | bottom-left | bottom-centre |");
        sb.AppendLine("            bottom-right");
        sb.AppendLine("  size      small | medium | large");
        sb.AppendLine("  label     a short caption such as \"CPU\" or \"GPU\"");
        sb.AppendLine("  icon      for kind \"icon\": a name from the icon list");
        sb.AppendLine("  accent    neutral | good | cool | warm | hot | dim");
        sb.AppendLine("  group     a name; layers sharing one are laid out together");
        sb.AppendLine();
        sb.AppendLine("You do NOT set positions or sizes in pixels. The program places "
                      + "everything from the anchors and groups above.");
        sb.AppendLine();

        AppendStyle(sb);
    }

    /// <summary>
    /// The optional style block. Listed after the required fields and described
    /// as optional twice, because the failure that matters is a model deciding
    /// every layer needs one — that is how a simple request turns into twenty
    /// lines of styling nobody asked for.
    /// </summary>
    private static void AppendStyle(StringBuilder sb)
    {
        sb.AppendLine("Each layer may also carry \"style\". Everything in it is optional, and "
                      + "leaving it out entirely is usually right — the theme already makes "
                      + "things look consistent. Use it when a specific look is asked for.");
        sb.AppendLine();
        sb.AppendLine("  \"style\": {");
        sb.AppendLine("    \"fill\":     an accent name, or two joined for a gradient:");
        sb.AppendLine("                \"warm\" or \"cool->hot\"  (bars and cards)");
        sb.AppendLine("    \"track\":    the unfilled part of a bar or gauge");
        sb.AppendLine("    \"segments\": whole number; splits a bar into blocks");
        sb.AppendLine("    \"ticks\":    whole number; tick marks around a gauge");
        sb.AppendLine("    \"sweep\":    degrees a gauge covers, e.g. 270 or 180");
        sb.AppendLine("    \"font\":     default | condensed | mono | display");
        sb.AppendLine("    \"bold\":     true | false");
        sb.AppendLine("    \"outline\":  number; a dark edge on text, for busy video");
        sb.AppendLine("    \"pill\":     true; a translucent card behind text");
        sb.AppendLine("    \"glow\":     number; a soft halo behind text. 4 to 8 is plenty");
        sb.AppendLine("    \"radius\":   corner rounding");
        sb.AppendLine("    \"opacity\":  0 to 1");
        sb.AppendLine("    \"rotate\":   degrees");
        sb.AppendLine("    \"shape\":    for kind \"panel\": card | ring | arc | bracket |");
        sb.AppendLine("                rule | chevron.  For kind \"graph\": line |");
        sb.AppendLine("                area | bars");
        sb.AppendLine("  }");
        sb.AppendLine();
        sb.AppendLine("  bracket = corner marks framing a group. rule = a divider line.");
        sb.AppendLine("  a graph shows the last minute of a sensor, not its "
                      + "current value.");
        sb.AppendLine("  arc and ring are decoration, not data.");
        sb.AppendLine();

        sb.AppendLine("Icons available:");
        sb.AppendLine();

        // Wrapped rather than one per line: fifty-odd names one to a line is a
        // page of prompt for a flat list.
        var line = new StringBuilder("  ");
        foreach (string name in IconNames.All)
        {
            if (line.Length + name.Length + 2 > 76)
            {
                sb.AppendLine(line.ToString());
                line.Clear().Append("  ");
            }
            line.Append(name).Append("  ");
        }
        if (line.Length > 2) sb.AppendLine(line.ToString());

        sb.AppendLine();
    }

    /// <summary>
    /// The sensors this machine actually reports, grouped and compact. Only
    /// available ones: a sensor with no provider would render "--", so offering
    /// it can only produce a disappointing layer.
    /// </summary>
    private static void AppendSensors(StringBuilder sb, SensorSnapshot sensors)
    {
        sb.AppendLine("Sensors available on this machine. Do not invent others.");
        sb.AppendLine();

        var live = sensors.Descriptors
            .Where(d => sensors[d.Id].Available)
            .GroupBy(d => d.Category)
            .OrderBy(g => g.Key, StringComparer.Ordinal);

        foreach (IGrouping<string, SensorDescriptor> group in live)
        {
            sb.Append("  ").Append(group.Key).AppendLine(":");

            foreach (SensorDescriptor d in group.OrderBy(d => d.Id, StringComparer.Ordinal))
            {
                sb.Append("    ").Append(d.Id);

                if (d.IsText)
                {
                    // Show what it currently reads. A name alone cannot
                    // distinguish date.today from date.long from date.short —
                    // they are all "the date" — so the model would be choosing
                    // between them blind. One live example settles it, and the
                    // handful of text sensors makes this cost a few dozen tokens.
                    string sample = sensors[d.Id].Text ?? "";
                    sb.Append(sample.Length > 0 ? $"  (text, e.g. \"{sample}\")" : "  (text)");
                }
                else if (!string.IsNullOrEmpty(d.Unit))
                {
                    sb.Append(FormattableString.Invariant($"  ({d.Unit}, {d.Min:0.##}-{d.Max:0.##})"));
                }

                sb.Append("  ").AppendLine(d.Name);
            }
        }

        sb.AppendLine();
    }

    private static void AppendCurrent(StringBuilder sb, OverlayProfile? current)
    {
        if (current == null || current.Layers.Count == 0)
        {
            sb.AppendLine("The overlay is currently empty.");
            sb.AppendLine();
            return;
        }

        sb.AppendLine(FormattableString.Invariant($"The overlay currently has {current.Layers.Count} layer(s):"));

        foreach (OverlayLayer l in current.Layers)
        {
            string what = l switch
            {
                TextLayer t => $"text \"{t.Template}\"",
                BarLayer b => $"bar of {b.Source}",
                GaugeLayer g => $"gauge of {g.Source}",
                GraphLayer gr => $"graph of {gr.Source}",
                ShapeLayer => "panel",
                ImageLayer => "image",
                _ => "layer",
            };

            sb.Append("  - ").Append(what).Append(" at ")
              .AppendLine(Describe(l.Anchor));
        }

        sb.AppendLine();
        sb.AppendLine("When adding, do not repeat something that is already there. If the "
                      + "request names a thing that already exists, use \"replace\" and "
                      + "rebuild the overlay rather than adding a second copy of it.");
        sb.AppendLine("Prefer corners that are not already busy, unless asked otherwise.");
        sb.AppendLine();
    }

    private static void AppendRules(StringBuilder sb)
    {
        sb.AppendLine("Rules:");
        sb.AppendLine("  - Use only sensor ids from the list. Never guess one.");
        sb.AppendLine("  - Give related layers the same group, and put a panel in the group "
                      + "when you want a card behind them.");
        sb.AppendLine("  - A readout and its bar belong in one group, readout first.");
        sb.AppendLine("  - Prefer few, legible layers. This display is small and is seen from "
                      + "across a room.");
        sb.AppendLine("  - Leave accent out unless a colour was asked for; loads and "
                      + "temperatures are automatically coloured green through red.");
        sb.AppendLine("  - Leave style out unless a look was asked for. The theme already "
                      + "keeps things consistent.");
        sb.AppendLine("  - An icon beside a readout reads far better than a readout alone, "
                      + "so pair them when there is room.");
        sb.AppendLine(FormattableString.Invariant($"  - At most {MaxLayers} layers."));
        sb.AppendLine();
    }

    /// <summary>
    /// The three shapes of request this feature exists for, each with the answer
    /// it should get. Worth the tokens: examples pin down the output format far
    /// more reliably than describing it does, especially for small models.
    /// </summary>
    private static void AppendExamples(StringBuilder sb)
    {
        sb.AppendLine("Examples.");
        sb.AppendLine();

        sb.AppendLine("\"Add a clock at the center\"");
        sb.AppendLine("{\"intent\":\"add\",\"note\":\"Clock in the middle\",\"layers\":[");
        sb.AppendLine("  {\"kind\":\"text\",\"sensor\":\"time.short\",\"anchor\":\"center\","
                      + "\"size\":\"large\"}]}");
        sb.AppendLine();

        sb.AppendLine("\"Add GPU usage bottom left\"");
        sb.AppendLine("{\"intent\":\"add\",\"note\":\"GPU load, bottom left\",\"layers\":[");
        sb.AppendLine("  {\"kind\":\"panel\",\"anchor\":\"bottom-left\",\"group\":\"gpu\"},");
        sb.AppendLine("  {\"kind\":\"text\",\"sensor\":\"gpu.load\",\"anchor\":\"bottom-left\","
                      + "\"label\":\"GPU\",\"group\":\"gpu\"},");
        sb.AppendLine("  {\"kind\":\"bar\",\"sensor\":\"gpu.load\",\"anchor\":\"bottom-left\","
                      + "\"group\":\"gpu\"}]}");
        sb.AppendLine();

        sb.AppendLine("\"Create a full stylised overlay with CPU and GPU usage\"");
        sb.AppendLine("{\"intent\":\"replace\",\"note\":\"CPU and GPU panels with dials\","
                      + "\"layers\":[");
        sb.AppendLine("  {\"kind\":\"panel\",\"anchor\":\"top-left\",\"group\":\"cpu\"},");
        sb.AppendLine("  {\"kind\":\"text\",\"sensor\":\"cpu.load\",\"anchor\":\"top-left\","
                      + "\"label\":\"CPU\",\"group\":\"cpu\"},");
        sb.AppendLine("  {\"kind\":\"bar\",\"sensor\":\"cpu.load\",\"anchor\":\"top-left\","
                      + "\"group\":\"cpu\"},");
        sb.AppendLine("  {\"kind\":\"panel\",\"anchor\":\"top-right\",\"group\":\"gpu\"},");
        sb.AppendLine("  {\"kind\":\"text\",\"sensor\":\"gpu.load\",\"anchor\":\"top-right\","
                      + "\"label\":\"GPU\",\"group\":\"gpu\"},");
        sb.AppendLine("  {\"kind\":\"bar\",\"sensor\":\"gpu.load\",\"anchor\":\"top-right\","
                      + "\"group\":\"gpu\"},");
        sb.AppendLine("  {\"kind\":\"gauge\",\"sensor\":\"cpu.load\",\"anchor\":\"bottom-left\","
                      + "\"label\":\"CPU\"},");
        sb.AppendLine("  {\"kind\":\"gauge\",\"sensor\":\"gpu.load\",\"anchor\":\"bottom-left\","
                      + "\"label\":\"GPU\"},");
        sb.AppendLine("  {\"kind\":\"text\",\"sensor\":\"time.short\","
                      + "\"anchor\":\"bottom-right\",\"size\":\"large\"}]}");
        sb.AppendLine();

        // A styled example, because a look is what the style block is for and a
        // model shown only plain examples writes only plain layers.
        sb.AppendLine("\"Make a HUD style overlay with CPU and GPU\"");
        sb.AppendLine("{\"intent\":\"replace\",\"theme\":\"hud\",\"note\":\"HUD frame with "
                      + "CPU and GPU\",\"layers\":[");
        sb.AppendLine("  {\"kind\":\"panel\",\"anchor\":\"top-left\",\"group\":\"cpu\","
                      + "\"style\":{\"shape\":\"bracket\",\"fill\":\"cool\"}},");
        sb.AppendLine("  {\"kind\":\"icon\",\"icon\":\"cpu\",\"anchor\":\"top-left\","
                      + "\"group\":\"cpu\"},");
        sb.AppendLine("  {\"kind\":\"text\",\"sensor\":\"cpu.load\",\"anchor\":\"top-left\","
                      + "\"label\":\"CPU\",\"group\":\"cpu\",\"style\":{\"font\":\"condensed\"}},");
        sb.AppendLine("  {\"kind\":\"bar\",\"sensor\":\"cpu.load\",\"anchor\":\"top-left\","
                      + "\"group\":\"cpu\",\"style\":{\"segments\":12}},");
        sb.AppendLine("  {\"kind\":\"icon\",\"icon\":\"gpu\",\"anchor\":\"top-right\","
                      + "\"group\":\"gpu\"},");
        sb.AppendLine("  {\"kind\":\"gauge\",\"sensor\":\"gpu.load\",\"anchor\":\"top-right\","
                      + "\"label\":\"GPU\",\"group\":\"gpu\",\"style\":{\"ticks\":9}}]}");
        sb.AppendLine();

        // A targeted restyle: the shape of request the style block exists for,
        // and the one most likely to be answered with a needless rebuild.
        sb.AppendLine("\"Make the GPU bar segmented and orange\"");
        sb.AppendLine("{\"intent\":\"replace\",\"note\":\"GPU bar restyled\",\"layers\":[");
        sb.AppendLine("  {\"kind\":\"bar\",\"sensor\":\"gpu.load\",\"anchor\":\"top-right\","
                      + "\"style\":{\"segments\":16,\"fill\":\"warm\"}}]}");
        sb.AppendLine();
    }

    /// <summary>The retry instruction, when the first answer would not parse.</summary>
    public const string RetrySuffix =
        "\n\nYour previous reply was not valid JSON. Reply with the JSON object only: "
        + "no prose, no code fence, no trailing commas.";

    private static string Describe(LayerAnchor a) => a switch
    {
        LayerAnchor.TopLeft => "top-left",
        LayerAnchor.TopCentre => "top-centre",
        LayerAnchor.TopRight => "top-right",
        LayerAnchor.MiddleLeft => "middle-left",
        LayerAnchor.Centre => "center",
        LayerAnchor.MiddleRight => "middle-right",
        LayerAnchor.BottomLeft => "bottom-left",
        LayerAnchor.BottomCentre => "bottom-centre",
        _ => "bottom-right",
    };
}
