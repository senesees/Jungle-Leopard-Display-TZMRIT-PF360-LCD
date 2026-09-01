using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;

namespace JLDisplayManager.Services.Sensors;

/// <summary>
/// Memory, battery, uptime and the clock — the sensors that need no counters,
/// no driver and no helper, and so are the only ones guaranteed to work
/// everywhere. Cheap enough that polling them costs nothing measurable.
/// </summary>
public sealed class SystemProvider : ISensorProvider
{
    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys;
        public ulong ullTotalPageFile, ullAvailPageFile;
        public ulong ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

    [DllImport("kernel32.dll")]
    private static extern ulong GetTickCount64();

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

    private double _totalGb;

    public string Name => "Windows system";

    public bool Available => true;

    public bool Start()
    {
        var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (GlobalMemoryStatusEx(ref m)) _totalGb = m.ullTotalPhys / (1024.0 * 1024 * 1024);
        return true;
    }

    public IReadOnlyList<SensorDescriptor> Describe() => new List<SensorDescriptor>
    {
        new("mem.used",         "Memory in use",   "Memory", "GB", 0, Math.Max(1, _totalGb)),
        new("mem.total",        "Memory total",    "Memory", "GB", 0, Math.Max(1, _totalGb)),
        new("mem.percent",      "Memory load",     "Memory", "%",  0, 100),
        new("battery.percent",  "Battery",         "System", "%",  0, 100),
        new("battery.charging", "Battery charging","System", "",   0, 1),
        new("sys.uptime.hours", "Uptime",          "System", "h",  0, 720),

        // Text by nature: a clock formatted here rather than by every layer that
        // wants one, so "{time.now}" needs no format string to be useful.
        new("sys.uptime",       "Uptime",           "System", "", IsText: true),

        new("time.now",         "Time",             "Time", "", IsText: true),
        new("time.short",       "Time (no seconds)","Time", "", IsText: true),
        new("time.12",          "Time (12 hour)",   "Time", "", IsText: true),

        new("date.today",       "Date",             "Date", "", IsText: true),
        new("date.long",        "Date (long)",      "Date", "", IsText: true),
        new("date.short",       "Date (numeric)",   "Date", "", IsText: true),
        new("date.iso",         "Date (ISO)",       "Date", "", IsText: true),
        new("date.day",         "Day name",         "Date", "", IsText: true),
        new("date.month",       "Month name",       "Date", "", IsText: true),
        new("date.year",        "Year",             "Date", "", IsText: true),
    };

    public void Poll(ISensorSink sink)
    {
        var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (GlobalMemoryStatusEx(ref m))
        {
            const double GB = 1024.0 * 1024 * 1024;
            double total = m.ullTotalPhys / GB;
            double used = (m.ullTotalPhys - m.ullAvailPhys) / GB;
            sink.Publish("mem.total", total);
            sink.Publish("mem.used", used);
            sink.Publish("mem.percent", m.dwMemoryLoad);
        }

        if (GetSystemPowerStatus(out SYSTEM_POWER_STATUS p))
        {
            // 255 means "unknown", which is what a desktop with no battery
            // reports. Publishing it as a percentage would draw a full bar.
            if (p.BatteryLifePercent <= 100) sink.Publish("battery.percent", p.BatteryLifePercent);
            if (p.ACLineStatus <= 1) sink.Publish("battery.charging", p.ACLineStatus);
        }

        TimeSpan up = TimeSpan.FromMilliseconds(GetTickCount64());
        sink.Publish("sys.uptime.hours", up.TotalHours);
        sink.PublishText("sys.uptime",
            up.TotalDays >= 1
                ? $"{(int)up.TotalDays}d {up.Hours}h {up.Minutes}m"
                : $"{up.Hours}h {up.Minutes}m");

        DateTime now = DateTime.Now;
        sink.PublishText("time.now", now.ToString("HH:mm:ss"));
        sink.PublishText("time.short", now.ToString("HH:mm"));

        // Lower case, because "1:05 PM" shouts on a panel this size.
        sink.PublishText("time.12", now.ToString("h:mm tt").ToLower(CultureInfo.CurrentCulture));

        sink.PublishText("date.today", $"{now:ddd} {Ordinal(now.Day)} {now:MMM}");
        sink.PublishText("date.long", $"{now:dddd} {Ordinal(now.Day)} {now:MMMM}");
        sink.PublishText("date.short", now.ToString("d"));
        sink.PublishText("date.iso", now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        sink.PublishText("date.day", now.ToString("dddd"));
        sink.PublishText("date.month", now.ToString("MMMM"));
        sink.PublishText("date.year", now.ToString("yyyy"));
    }

    /// <summary>
    /// A day number with its English ordinal suffix: 1st, 2nd, 3rd, 4th.
    ///
    /// Written out because .NET has no format specifier for it — "d" gives a
    /// bare number and there is no way to ask for the suffix.
    ///
    /// Only applied in English. The suffix is a property of the language, not
    /// of the date, so gluing "st" onto a French month would produce
    /// "1st septembre". Everywhere else gets the plain number, which is what
    /// those languages write anyway.
    /// </summary>
    internal static string Ordinal(int day)
    {
        if (!CultureInfo.CurrentCulture.TwoLetterISOLanguageName
                .Equals("en", StringComparison.OrdinalIgnoreCase))
            return day.ToString(CultureInfo.CurrentCulture);

        // 11th, 12th and 13th break the pattern the last digit would suggest.
        string suffix = (day % 100) is >= 11 and <= 13
            ? "th"
            : (day % 10) switch
            {
                1 => "st",
                2 => "nd",
                3 => "rd",
                _ => "th",
            };

        return day + suffix;
    }

    public void Dispose() { }
}
