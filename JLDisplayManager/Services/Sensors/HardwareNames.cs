using System;
using System.Collections.Generic;

namespace JLDisplayManager.Services.Sensors;

/// <summary>
/// Maps the names hardware monitors use onto the ids this app publishes.
///
/// Shared by the LibreHardwareMonitor and HWiNFO providers, because they report
/// the same silicon under different names and a profile must not care which one
/// happens to be running. "Core (Tctl/Tdie)" from LHM and "CPU (Tctl/Tdie)" from
/// HWiNFO are both <c>cpu.temp</c>.
///
/// Deliberately a curated list rather than everything the helper offers. HWiNFO
/// alone exposes several hundred readings, and every one of them would land in
/// the AI's system prompt — which is already 1400 tokens. A short list of
/// sensors people actually put on a panel is worth more than a complete one.
/// </summary>
internal static class HardwareNames
{
    /// <summary>Everything the optional tier can supply, with its range for bars.</summary>
    public static IReadOnlyList<SensorDescriptor> Descriptors { get; } = new List<SensorDescriptor>
    {
        new("cpu.temp",       "CPU temperature",     "CPU",   "°C",  0, 100),

        // Deliberately not called "hottest core". On Ryzen this is the hottest
        // CCD die, and the package figure above it routinely reads HIGHER —
        // measured on an idle 5900X: Tctl/Tdie 51 °C against CCDs Max 35 °C.
        // A name implying it is the larger of the two would look broken.
        new("cpu.temp.die",   "CPU die temperature", "CPU",   "°C",  0, 100),

        new("cpu.power",      "CPU package power",   "CPU",   "W",   0, 250),
        new("cpu.fan",        "CPU fan",             "Fans",  "rpm", 0, 3000),

        new("gpu.temp.hot",   "GPU hotspot",         "GPU",   "°C",  0, 110),

        new("mb.temp",        "Motherboard",         "System", "°C", 0, 100),

        // Six rather than three: a board hands these out unlabelled as "Fan #1"
        // upward, and on this machine the pump sits on #7 with no way to know
        // it. Publishing them all lets someone pick the one that is theirs
        // instead of the mapping silently dropping it.
        new("fan.1",          "Fan 1",               "Fans",  "rpm", 0, 3000),
        new("fan.2",          "Fan 2",               "Fans",  "rpm", 0, 3000),
        new("fan.3",          "Fan 3",               "Fans",  "rpm", 0, 3000),
        new("fan.4",          "Fan 4",               "Fans",  "rpm", 0, 3000),
        new("fan.5",          "Fan 5",               "Fans",  "rpm", 0, 3000),
        new("fan.6",          "Fan 6",               "Fans",  "rpm", 0, 3000),
        new("fan.7",          "Fan 7",               "Fans",  "rpm", 0, 3000),

        // The pump this panel is bolted to. Worth its own id: on an AIO it is
        // the reading people most want next to the coolant temperature.
        new("pump.rpm",       "Pump speed",          "Fans",  "rpm", 0, 5000),
        new("coolant.temp",   "Coolant temperature", "System", "°C", 0, 60),
    };

    /// <summary>
    /// Which id a vendor's sensor name means, or null for one we do not publish.
    ///
    /// <paramref name="hardware"/> is the component the reading belongs to —
    /// "AMD Ryzen 9 5900X", "Nvidia RTX 5070", a motherboard chip — because the
    /// same label means different things under different hardware. "Temperature"
    /// under a GPU is not the CPU's.
    /// </summary>
    public static string? Match(string hardware, string label, SensorKind kind)
    {
        string h = hardware.ToLowerInvariant();
        string l = label.ToLowerInvariant();

        bool isCpu = h.Contains("ryzen") || h.Contains("intel") || h.Contains("core i")
                     || h.Contains("amd cpu") || h.Contains("cpu") || h.Contains("threadripper");
        bool isGpu = h.Contains("nvidia") || h.Contains("geforce") || h.Contains("radeon")
                     || h.Contains("rtx") || h.Contains("gtx") || h.Contains("arc");

        switch (kind)
        {
            case SensorKind.Temperature:
                if (isCpu)
                {
                    // Order matters here, and getting it wrong is silent. A
                    // Ryzen reports SIX labels containing "Tdie" — the package
                    // figure, two per-CCD ones, their max and their average —
                    // so a bare "contains Tdie" test matches all of them and
                    // whichever happens to be published last wins. Measured on
                    // a 5900X: Core (Tctl/Tdie) 49.9, CCD1 48.8, CCD2 35.0.
                    // Reporting 35 as the CPU temperature would look plausible
                    // and be wrong.

                    // Tctl appears only on the package figure, which is the one
                    // that means "the CPU's temperature" on AMD.
                    if (l.Contains("tctl")) return "cpu.temp";

                    if (l.Contains("ccds max") || l.Contains("ccd max")
                        || l.Contains("core max") || l.Contains("hottest"))
                        return "cpu.temp.die";

                    // Any other CCD reading: an individual die or their average.
                    // Too many to publish, and none of them is "the" temperature.
                    if (l.Contains("ccd")) return null;

                    // Intel's equivalent of Tctl.
                    if (l.Contains("package")) return "cpu.temp";

                    return null;
                }

                if (isGpu)
                {
                    // The hotspot, not the memory. "GPU Memory Junction" is VRAM
                    // and reads far higher than the core — publishing it as the
                    // hotspot would overstate the GPU's temperature by 10°C.
                    return l.Contains("hot") ? "gpu.temp.hot" : null;
                }

                if (l.Contains("water") || l.Contains("coolant")) return "coolant.temp";

                // A bare motherboard/system temperature.
                if (l.Contains("motherboard") || l.Contains("system") || l.Contains("mainboard")
                    || l.Contains("temperature 1") || l == "temperature")
                    return "mb.temp";

                return null;

            case SensorKind.Power:
                if (isCpu && (l.Contains("package") || l.Contains("cpu package")))
                    return "cpu.power";
                return null;

            case SensorKind.Fan:
                if (l.Contains("pump")) return "pump.rpm";
                if (l.Contains("cpu")) return "cpu.fan";

                // "Fan #2", "Chassis Fan 1", "System Fan 3" -> fan.1..7.
                // Checked highest-first so "Fan #7" is not claimed by the test
                // for 1 in a two-digit number later on.
                if (l.Contains("fan") || l.Contains("chassis") || l.Contains("sys"))
                    for (int i = 7; i >= 1; i--)
                        if (l.Contains(i.ToString())) return $"fan.{i}";

                return null;

            default:
                return null;
        }
    }
}

/// <summary>The reading kinds both helpers distinguish, reduced to what we use.</summary>
internal enum SensorKind
{
    Other,
    Temperature,
    Fan,
    Power,
    Voltage,
    Clock,
    Load,
}
