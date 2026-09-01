using System;
using System.Collections.Generic;

using JLDisplayManager.Models;

namespace JLDisplayManager.Services.Sensors;

/// <summary>
/// CPU, GPU, disk and network load from Windows' own performance counters.
///
/// This is the tier that needs nothing installed and works on any machine and
/// any GPU vendor. It cannot report temperature — Windows exposes none through
/// PDH — which is why the optional LibreHardwareMonitor and HWiNFO providers
/// exist alongside it.
/// </summary>
public sealed class PdhProvider : ISensorProvider
{
    // "% Processor Utility" is the figure Task Manager shows: it accounts for
    // frequency scaling, so a core parked at 800 MHz does not read 100% just
    // because it is never idle. It can legitimately exceed 100 during turbo,
    // hence the uncapped format.
    private const string CpuTotal = @"\Processor Information(_Total)\% Processor Utility";
    private const string CpuPerf = @"\Processor Information(_Total)\% Processor Performance";
    private const string CpuCores = @"\Processor Information(0,*)\% Processor Utility";

    // One instance per engine per process; the total is what people mean by
    // "GPU usage". engtype_3D excludes video decode and copy engines, which
    // would otherwise make playing a video look like GPU load.
    private const string Gpu3D = @"\GPU Engine(*engtype_3D)\Utilization Percentage";
    private const string GpuMem = @"\GPU Process Memory(*)\Local Usage";

    private const string DiskRead = @"\PhysicalDisk(_Total)\Disk Read Bytes/sec";
    private const string DiskWrite = @"\PhysicalDisk(_Total)\Disk Write Bytes/sec";
    private const string DiskBusy = @"\PhysicalDisk(_Total)\% Disk Time";

    private const string NetRecv = @"\Network Interface(*)\Bytes Received/sec";
    private const string NetSent = @"\Network Interface(*)\Bytes Sent/sec";

    private Pdh.Query? _query;
    private Pdh.Counter? _cpuTotal, _cpuPerf, _cpuCores;
    private Pdh.Counter? _gpu3D, _gpuMem;
    private Pdh.Counter? _diskRead, _diskWrite, _diskBusy;
    private Pdh.Counter? _netRecv, _netSent;

    private double _baseClockGhz;
    private int _coreCount;

    public string Name => "Windows performance counters";

    public bool Available => _query != null;

    public bool Start()
    {
        _query = Pdh.Query.Open();
        if (_query == null)
        {
            Storage.Log("PDH: could not open a query; load sensors will be unavailable");
            return false;
        }

        _cpuTotal = _query.Add(CpuTotal) ?? _query.Add(@"\Processor(_Total)\% Processor Time");
        _cpuPerf = _query.Add(CpuPerf);
        _cpuCores = _query.Add(CpuCores);

        _gpu3D = _query.Add(Gpu3D);
        _gpuMem = _query.Add(GpuMem);

        _diskRead = _query.Add(DiskRead);
        _diskWrite = _query.Add(DiskWrite);
        _diskBusy = _query.Add(DiskBusy);

        _netRecv = _query.Add(NetRecv);
        _netSent = _query.Add(NetSent);

        _coreCount = Environment.ProcessorCount;
        _baseClockGhz = ReadBaseClockGhz();

        // Rate counters mean nothing until they have been sampled twice, so get
        // the first sample out of the way now rather than publishing a zero.
        _query.Collect();
        return true;
    }

    public IReadOnlyList<SensorDescriptor> Describe()
    {
        var list = new List<SensorDescriptor>
        {
            new("cpu.load",       "CPU load",           "CPU",     "%",    0, 100),
            new("cpu.clock",      "CPU clock",          "CPU",     "GHz",  0, 6),
            new("gpu.load",       "GPU load",           "GPU",     "%",    0, 100),
            new("gpu.vram.used",  "GPU memory in use",  "GPU",     "GB",   0, 32),
            new("disk.read",      "Disk read",          "Disk",    "MB/s", 0, 1000),
            new("disk.write",     "Disk write",         "Disk",    "MB/s", 0, 1000),
            new("disk.activity",  "Disk activity",      "Disk",    "%",    0, 100),
            new("net.down",       "Network down",       "Network", "MB/s", 0, 100),
            new("net.up",         "Network up",         "Network", "MB/s", 0, 100),
        };

        // Per-core entries are generated rather than listed, since how many
        // there are is a property of the machine.
        for (int i = 0; i < _coreCount; i++)
            list.Add(new SensorDescriptor($"cpu.load.core{i}", $"CPU core {i}", "CPU", "%", 0, 100));

        return list;
    }

    public void Poll(ISensorSink sink)
    {
        if (_query == null || !_query.Collect()) return;

        // Uncapped: turbo genuinely puts "% Processor Utility" above 100, and
        // clamping it here would hide that rather than represent it.
        double? cpu = _cpuTotal?.Value(uncapped: true);
        if (cpu.HasValue) sink.Publish("cpu.load", Math.Clamp(cpu.Value, 0, 100));

        // "% Processor Performance" is a percentage of the nominal clock, so the
        // actual frequency is that fraction of the base clock.
        double? perf = _cpuPerf?.Value(uncapped: true);
        if (perf.HasValue && _baseClockGhz > 0)
            sink.Publish("cpu.clock", _baseClockGhz * perf.Value / 100.0);

        if (_cpuCores != null)
        {
            foreach (KeyValuePair<string, double> kv in _cpuCores.Values(uncapped: true))
            {
                // Instance names look like "0,3" — socket then core.
                int comma = kv.Key.LastIndexOf(',');
                string idx = comma >= 0 ? kv.Key[(comma + 1)..] : kv.Key;
                if (int.TryParse(idx, out int core))
                    sink.Publish($"cpu.load.core{core}", Math.Clamp(kv.Value, 0, 100));
            }
        }

        if (_gpu3D != null) sink.Publish("gpu.load", Math.Clamp(_gpu3D.Sum(), 0, 100));

        // Bytes across every process using the GPU. NVML reports this better on
        // NVIDIA hardware and takes the id first; this is the fallback.
        if (_gpuMem != null) sink.Publish("gpu.vram.used", _gpuMem.Sum() / (1024.0 * 1024 * 1024));

        const double MB = 1024.0 * 1024.0;
        double? dr = _diskRead?.Value();
        double? dw = _diskWrite?.Value();
        double? db = _diskBusy?.Value();
        if (dr.HasValue) sink.Publish("disk.read", dr.Value / MB);
        if (dw.HasValue) sink.Publish("disk.write", dw.Value / MB);
        // "% Disk Time" is a queue-length measure and routinely exceeds 100.
        if (db.HasValue) sink.Publish("disk.activity", Math.Clamp(db.Value, 0, 100));

        if (_netRecv != null) sink.Publish("net.down", _netRecv.Sum() / MB);
        if (_netSent != null) sink.Publish("net.up", _netSent.Sum() / MB);
    }

    /// <summary>
    /// The nominal clock in GHz, which PDH gives as a percentage of. Read from
    /// the registry because Win32_Processor means WMI, and WMI means either a
    /// package reference or a lot of COM interop for one number.
    /// </summary>
    private static double ReadBaseClockGhz()
    {
        try
        {
            using Microsoft.Win32.RegistryKey? key = Microsoft.Win32.Registry.LocalMachine
                .OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            if (key?.GetValue("~MHz") is int mhz && mhz > 0) return mhz / 1000.0;
        }
        catch (Exception ex)
        {
            Storage.Log($"PDH: could not read the base clock: {ex.Message}");
        }
        return 0;
    }

    public void Dispose()
    {
        _query?.Dispose();
        _query = null;
    }
}
