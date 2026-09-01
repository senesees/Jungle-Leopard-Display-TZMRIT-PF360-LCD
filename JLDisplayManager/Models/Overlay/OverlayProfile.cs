using System;
using System.Collections.Generic;

namespace JLDisplayManager.Models.Overlay;

/// <summary>
/// A named arrangement of layers. Profiles are the unit people switch between —
/// "Minimal" while gaming, "Full telemetry" while rendering — and the unit they
/// export and share, which is why they live in their own file rather than
/// inside settings.json.
/// </summary>
public sealed class OverlayProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = "Untitled";

    /// <summary>
    /// Which <see cref="OverlayTheme"/> this profile draws with. Stored by name
    /// rather than by value, so improving a shipped theme improves every profile
    /// using it — and so a profile stays readable when hand-edited.
    ///
    /// Empty or unrecognised means <c>minimal</c>, which holds the values every
    /// profile written before themes existed already used.
    /// </summary>
    public string Theme { get; set; } = "minimal";

    /// <summary>Bottom first: list order is z-order, exactly as the editor shows it.</summary>
    public List<OverlayLayer> Layers { get; set; } = new();

    /// <summary>
    /// A copy with its own layer list but the same layer objects.
    ///
    /// The render thread works from one of these because the editor mutates the
    /// live list from the UI thread — see <c>OverlayService.Refresh</c>. Layer
    /// objects stay shared on purpose, so a drag in progress shows through.
    ///
    /// It lives here, beside the fields, rather than in the service: it is a
    /// copy that has to list every property, and doing that anywhere else is how
    /// a newly added field gets silently left behind. <see cref="Theme"/> was,
    /// exactly once, and the symptom was a theme picker that did nothing.
    /// </summary>
    public OverlayProfile ShallowCopy() => new()
    {
        Id = Id,
        Name = Name,
        Theme = Theme,
        Layers = new List<OverlayLayer>(Layers),
    };
}

/// <summary>Everything the overlay feature remembers, saved to overlays.json.</summary>
public sealed class OverlaySettings
{
    /// <summary>Master switch. Off costs nothing at all on the native side.</summary>
    public bool Enabled { get; set; }

    public Guid? ActiveProfileId { get; set; }

    /// <summary>
    /// How often the overlay is redrawn, at most. 10 is plenty: sensors move at
    /// 1 Hz and the render is the most expensive step in the whole pipeline, so
    /// pushing this towards the panel's 30 fps buys nothing but CPU.
    /// </summary>
    public int RenderHz { get; set; } = 10;

    /// <summary>How often sensors are read, in milliseconds.</summary>
    public int SensorPollMs { get; set; } = 1000;

    public List<OverlayProfile> Profiles { get; set; } = new();

    public OverlayProfile? Active()
    {
        if (Profiles.Count == 0) return null;

        foreach (OverlayProfile p in Profiles)
            if (p.Id == ActiveProfileId) return p;

        return Profiles[0];
    }

    /// <summary>
    /// Converts colours and fonts that were written before themes existed into
    /// the roles that follow one.
    ///
    /// Needed because "a literal colour always wins" — the rule that protects a
    /// shade somebody deliberately picked — also protects every colour the
    /// generator baked in before roles existed. Without this, switching theme on
    /// a profile made yesterday changes nothing, which reads as broken rather
    /// than as a rule.
    ///
    /// Deliberately narrow: only the exact values the old palette emitted are
    /// converted. Anything else was chosen by hand and stays untouched. And
    /// every one of these roles resolves under <c>minimal</c> to the very hex it
    /// replaces, so a user who never switches theme sees no change at all.
    ///
    /// Returns how many values were rewritten, for the log.
    /// </summary>
    public static int MigrateToRoles(OverlaySettings settings)
    {
        // The values AccentPalette used to hand out, and the model defaults of
        // the same era.
        var colours = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["#FFFFFFFF"] = "text",
            ["#FFD0D0D0"] = "dim",
            ["#FF4AD995"] = "good",
            ["#FF5AB6FF"] = "cool",
            ["#FFFFB43A"] = "warm",
            ["#FFFF5A52"] = "hot",
            ["#73000000"] = "panel",
            ["#80000000"] = "panel",
            ["#60FFFFFF"] = "track",
            ["#50FFFFFF"] = "track",
            ["#80FFFFFF"] = "line",
        };

        int changed = 0;

        string? Map(string? value)
        {
            if (value == null) return null;
            if (!colours.TryGetValue(value.Trim(), out string? role)) return value;
            changed++;
            return role;
        }

        // The old default. Empty means "follow the theme", and resolves back to
        // Segoe UI under minimal, so this is invisible until a theme is chosen.
        string Font(string f)
        {
            if (!string.Equals(f.Trim(), "Segoe UI", StringComparison.OrdinalIgnoreCase)) return f;
            changed++;
            return "";
        }

        foreach (OverlayProfile profile in settings.Profiles)
        {
            foreach (OverlayLayer layer in profile.Layers)
            {
                switch (layer)
                {
                    case TextLayer t:
                        t.Colour = Map(t.Colour) ?? t.Colour;
                        t.BackgroundColour = Map(t.BackgroundColour);
                        t.FontFamily = Font(t.FontFamily);
                        foreach (ColourStop s in t.Thresholds) s.Colour = Map(s.Colour) ?? s.Colour;
                        break;

                    case BarLayer b:
                        b.TrackColour = Map(b.TrackColour) ?? b.TrackColour;
                        b.FillColour = Map(b.FillColour) ?? b.FillColour;
                        b.FillColourTo = Map(b.FillColourTo);
                        b.BorderColour = Map(b.BorderColour);
                        foreach (ColourStop s in b.Thresholds) s.Colour = Map(s.Colour) ?? s.Colour;
                        break;

                    case GaugeLayer g:
                        g.TrackColour = Map(g.TrackColour) ?? g.TrackColour;
                        g.FillColour = Map(g.FillColour) ?? g.FillColour;
                        g.TickColour = Map(g.TickColour) ?? g.TickColour;
                        g.CentreColour = Map(g.CentreColour) ?? g.CentreColour;
                        g.CaptionColour = Map(g.CaptionColour) ?? g.CaptionColour;
                        g.FontFamily = Font(g.FontFamily);
                        foreach (ColourStop s in g.Thresholds) s.Colour = Map(s.Colour) ?? s.Colour;
                        break;

                    case ShapeLayer sh:
                        sh.FillColour = Map(sh.FillColour);
                        sh.StrokeColour = Map(sh.StrokeColour);
                        break;
                }
            }
        }

        return changed;
    }

    /// <summary>
    /// A first-run profile, so turning the overlay on shows something rather
    /// than a blank panel and an instruction to go and edit some JSON.
    /// Deliberately modest: two corner readouts that stay out of the picture.
    /// </summary>
    public static OverlayProfile CreateDefault()
    {
        var profile = new OverlayProfile { Name = "Essentials" };

        profile.Layers.Add(new ShapeLayer
        {
            Name = "CPU panel",
            Anchor = LayerAnchor.TopLeft,
            X = 14, Y = 14, Width = 250, Height = 86,
            Kind = ShapeKind.Rectangle,
            CornerRadius = -1,   // follow the theme
            FillColour = "panel",
        });

        // Load and clock rather than temperature: nothing supplies cpu.temp
        // without LibreHardwareMonitor or HWiNFO, and a default profile that
        // ships reading "--°C" looks broken rather than looking like a feature
        // waiting to be enabled.
        profile.Layers.Add(new TextLayer
        {
            Name = "CPU readout",
            Anchor = LayerAnchor.TopLeft,
            X = 28, Y = 24, Width = 224, Height = 30,
            Template = "CPU  {cpu.load:0}%   {cpu.clock:0.0} GHz",
            FontSize = 22,
            ThresholdSource = "cpu.load",
            Thresholds =
            {
                new ColourStop { AtOrAbove = 0,  Colour = "text" },
                new ColourStop { AtOrAbove = 75, Colour = "warm" },
                new ColourStop { AtOrAbove = 92, Colour = "hot" },
            },
        });

        profile.Layers.Add(new BarLayer
        {
            Name = "CPU load bar",
            Anchor = LayerAnchor.TopLeft,
            X = 28, Y = 60, Width = 222, Height = 12,
            Source = "cpu.load",
            Thresholds =
            {
                new ColourStop { AtOrAbove = 0,  Colour = "good" },
                new ColourStop { AtOrAbove = 70, Colour = "warm" },
                new ColourStop { AtOrAbove = 90, Colour = "hot" },
            },
        });

        profile.Layers.Add(new ShapeLayer
        {
            Name = "GPU panel",
            Anchor = LayerAnchor.TopRight,
            X = 14, Y = 14, Width = 250, Height = 86,
            Kind = ShapeKind.Rectangle,
            CornerRadius = -1,   // follow the theme
            FillColour = "panel",
        });

        profile.Layers.Add(new TextLayer
        {
            Name = "GPU readout",
            Anchor = LayerAnchor.TopRight,
            X = 28, Y = 24, Width = 224, Height = 30,
            Template = "GPU  {gpu.load:0}%   {gpu.temp:0}°C",
            FontSize = 22,
            ThresholdSource = "gpu.temp",
            Thresholds =
            {
                new ColourStop { AtOrAbove = 0,  Colour = "text" },
                new ColourStop { AtOrAbove = 75, Colour = "warm" },
                new ColourStop { AtOrAbove = 87, Colour = "hot" },
            },
        });

        profile.Layers.Add(new BarLayer
        {
            Name = "GPU load bar",
            Anchor = LayerAnchor.TopRight,
            X = 28, Y = 60, Width = 222, Height = 12,
            Source = "gpu.load",
            Thresholds =
            {
                new ColourStop { AtOrAbove = 0,  Colour = "good" },
                new ColourStop { AtOrAbove = 70, Colour = "warm" },
                new ColourStop { AtOrAbove = 90, Colour = "hot" },
            },
        });

        profile.Layers.Add(new TextLayer
        {
            Name = "Clock",
            Anchor = LayerAnchor.BottomRight,
            X = 20, Y = 16, Width = 200, Height = 44,
            Template = "{time.short}",
            FontSize = 34,
            Align = TextAlign.Right,
            ShadowOffsetX = 2,
            ShadowOffsetY = 2,
        });

        return profile;
    }
}
