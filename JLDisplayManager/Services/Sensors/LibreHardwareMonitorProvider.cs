using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading;

using JLDisplayManager.Models;

namespace JLDisplayManager.Services.Sensors;

/// <summary>
/// CPU temperature and the rest of the motherboard, from LibreHardwareMonitor's
/// built-in web server.
///
/// This tier exists because Windows genuinely cannot supply these. On the AMD
/// machine this was written against, <c>MSAcpi_ThermalZoneTemperature</c>
/// answers "not supported" and there is no thermal-zone performance counter at
/// all: Ryzen die temperature lives behind the SMU and needs ring-0 access.
/// LibreHardwareMonitor has a signed kernel driver for exactly that, so the
/// honest thing is to read what it already knows rather than ship a driver.
///
/// Raw HTTP and System.Text.Json, no package — the release stays three
/// binaries. Absent or switched off, this provider simply reports nothing and
/// the sensors it would have supplied stay unavailable.
/// </summary>
public sealed class LibreHardwareMonitorProvider : ISensorProvider
{
    /// <summary>LHM's default. Configurable because people move it.</summary>
    public const int DefaultPort = 8085;

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(2) };
    private readonly string _url;

    private bool _available;
    private DateTime _quietUntil = DateTime.MinValue;

    public LibreHardwareMonitorProvider(int port = DefaultPort)
    {
        _url = $"http://localhost:{port}/data.json";
    }

    public string Name => "LibreHardwareMonitor";

    public bool Available => _available;

    public bool Start()
    {
        // Not fatal if it is not running yet: someone may start it later, and
        // Poll retries on its own schedule.
        _available = false;
        return true;
    }

    public IReadOnlyList<SensorDescriptor> Describe() => HardwareNames.Descriptors;

    public void Poll(ISensorSink sink)
    {
        // A dead endpoint costs a connection refusal every tick otherwise, and
        // the poll runs once a second forever.
        if (DateTime.UtcNow < _quietUntil) return;

        string json;
        try
        {
            json = _http.GetStringAsync(_url).GetAwaiter().GetResult();
        }
        catch
        {
            if (_available) Storage.Log("LHM: stopped responding");
            _available = false;
            _quietUntil = DateTime.UtcNow.AddSeconds(15);
            return;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);

            int found = 0;
            Walk(doc.RootElement, "", sink, ref found);

            if (!_available && found > 0) Storage.Log($"LHM: connected, {found} sensor(s) matched");
            _available = found > 0;
        }
        catch (JsonException ex)
        {
            Storage.Log($"LHM: unreadable response ({ex.Message})");
            _available = false;
            _quietUntil = DateTime.UtcNow.AddSeconds(30);
        }
    }

    /// <summary>
    /// Walks LHM's nested tree. Nodes carry a Text label and Children; a leaf
    /// reading also carries a Value like "45.2 °C" and a SensorId whose path
    /// says what kind of reading it is.
    ///
    /// <paramref name="hardware"/> accumulates the nearest named component, so a
    /// "Temperature" leaf can be told apart by what it hangs under.
    /// </summary>
    private static void Walk(JsonElement node, string hardware, ISensorSink sink, ref int found)
    {
        string text = Str(node, "Text");
        string sensorId = Str(node, "SensorId");
        string value = Str(node, "Value");

        if (sensorId.Length > 0 && value.Length > 0)
        {
            SensorKind kind = KindOf(sensorId, Str(node, "Type"));
            string? id = HardwareNames.Match(hardware, text, kind);

            if (id != null && TryValue(value, out double v))
            {
                sink.Publish(id, v);
                found++;
            }
        }
        else if (text.Length > 0 && sensorId.Length == 0)
        {
            // A grouping node. The component names are the ones worth carrying
            // down; "Temperatures" and the machine name are not.
            if (!IsCategory(text)) hardware = text;
        }

        if (!node.TryGetProperty("Children", out JsonElement kids)) return;
        if (kids.ValueKind != JsonValueKind.Array) return;

        foreach (JsonElement child in kids.EnumerateArray())
            Walk(child, hardware, sink, ref found);
    }

    private static bool IsCategory(string text) => text switch
    {
        "Temperatures" or "Fans" or "Powers" or "Voltages" or "Clocks" or "Load"
            or "Controls" or "Data" or "Throughput" or "Sensor" or "Levels" => true,
        _ => false,
    };

    /// <summary>
    /// The reading kind, from the SensorId path — "/amdcpu/0/temperature/2" —
    /// falling back to the Type field. The path is the more reliable of the two
    /// across LHM versions.
    /// </summary>
    private static SensorKind KindOf(string sensorId, string type)
    {
        string s = (sensorId + " " + type).ToLowerInvariant();

        if (s.Contains("temperature")) return SensorKind.Temperature;
        if (s.Contains("fan") || s.Contains("rpm")) return SensorKind.Fan;
        if (s.Contains("power")) return SensorKind.Power;
        if (s.Contains("voltage")) return SensorKind.Voltage;
        if (s.Contains("clock")) return SensorKind.Clock;
        if (s.Contains("load")) return SensorKind.Load;

        return SensorKind.Other;
    }

    /// <summary>
    /// Pulls the number out of "45.2 °C" or "1,234 RPM".
    ///
    /// Invariant parsing on the digits, but LHM formats for the machine's
    /// locale, so a comma may be either a decimal point or a thousands
    /// separator. Taking the leading numeric run and normalising it is more
    /// robust than trusting either reading of the string.
    /// </summary>
    internal static bool TryValue(string text, out double value)
    {
        value = 0;

        int start = -1, end = -1;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            bool numeric = char.IsDigit(c) || c == '.' || c == ',' || c == '-' || c == '+';

            if (numeric && start < 0) start = i;
            if (numeric) end = i;
            else if (start >= 0) break;
        }

        if (start < 0) return false;

        string number = text[start..(end + 1)];

        // A comma followed by exactly three digits at the end is a thousands
        // separator; anything else is a decimal comma.
        int comma = number.LastIndexOf(',');
        if (comma >= 0)
        {
            bool thousands = number.Length - comma - 1 == 3 && number.Contains('.');
            number = thousands ? number.Replace(",", "") : number.Replace(',', '.');
        }

        return double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static string Str(JsonElement node, string name) =>
        node.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? ""
            : "";

    public void Dispose() => _http.Dispose();
}
