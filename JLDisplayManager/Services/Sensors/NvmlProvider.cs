using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

using JLDisplayManager.Models;

namespace JLDisplayManager.Services.Sensors;

/// <summary>
/// NVIDIA GPU telemetry through NVML, the management library that ships with
/// every driver. Temperature, utilisation, VRAM, power, clocks and fan — the
/// figures Windows itself will not tell us.
///
/// Straight P/Invoke rather than a wrapper package: nvml.dll is already on any
/// machine with an NVIDIA driver, and the portable drop stays free of
/// third-party assemblies.
/// </summary>
public sealed class NvmlProvider : ISensorProvider
{
    private const string Dll = "nvml.dll";

    // NVML_TEMPERATURE_GPU
    private const uint TemperatureGpu = 0;

    // nvmlClockType_t
    private const uint ClockGraphics = 0;
    private const uint ClockMem = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct NvmlUtilization
    {
        public uint Gpu;      // percent of time any kernel was running
        public uint Memory;   // percent of time memory was being read or written
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NvmlMemory
    {
        public ulong Total, Free, Used;
    }

    [DllImport(Dll, EntryPoint = "nvmlInit_v2")] private static extern int NvmlInit();
    [DllImport(Dll, EntryPoint = "nvmlShutdown")] private static extern int NvmlShutdown();

    [DllImport(Dll, EntryPoint = "nvmlDeviceGetHandleByIndex_v2")]
    private static extern int NvmlGetHandle(uint index, out IntPtr device);

    [DllImport(Dll, EntryPoint = "nvmlDeviceGetName")]
    private static extern int NvmlGetName(IntPtr device, StringBuilder name, uint length);

    [DllImport(Dll, EntryPoint = "nvmlDeviceGetTemperature")]
    private static extern int NvmlGetTemperature(IntPtr device, uint sensor, out uint temp);

    [DllImport(Dll, EntryPoint = "nvmlDeviceGetUtilizationRates")]
    private static extern int NvmlGetUtilization(IntPtr device, out NvmlUtilization util);

    [DllImport(Dll, EntryPoint = "nvmlDeviceGetMemoryInfo")]
    private static extern int NvmlGetMemory(IntPtr device, out NvmlMemory memory);

    [DllImport(Dll, EntryPoint = "nvmlDeviceGetPowerUsage")]
    private static extern int NvmlGetPower(IntPtr device, out uint milliwatts);

    [DllImport(Dll, EntryPoint = "nvmlDeviceGetClockInfo")]
    private static extern int NvmlGetClock(IntPtr device, uint type, out uint mhz);

    [DllImport(Dll, EntryPoint = "nvmlDeviceGetFanSpeed")]
    private static extern int NvmlGetFan(IntPtr device, out uint percent);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryW(string path);

    private IntPtr _device = IntPtr.Zero;
    private bool _started;
    private double _vramTotalGb = 8;

    public string Name => _gpuName is null ? "NVIDIA NVML" : $"NVIDIA NVML ({_gpuName})";

    private string? _gpuName;

    public bool Available => _device != IntPtr.Zero;

    public bool Start()
    {
        // System32 covers a normal driver install, but nvml.dll historically
        // lived only in the NVSMI folder, which is not on the default PATH.
        // Loading it explicitly first means the DllImports below resolve to it.
        if (!ProbeLibrary())
        {
            Storage.Log("NVML: nvml.dll not found; NVIDIA GPU sensors unavailable");
            return false;
        }

        try
        {
            int rc = NvmlInit();
            if (rc != 0)
            {
                Storage.Log($"NVML: nvmlInit failed ({rc}); NVIDIA GPU sensors unavailable");
                return false;
            }
            _started = true;

            // Device 0. A multi-GPU machine is a later problem; picking the one
            // the display is on needs more than an index.
            if (NvmlGetHandle(0, out _device) != 0)
            {
                _device = IntPtr.Zero;
                Storage.Log("NVML: no NVIDIA device at index 0");
                return false;
            }

            var sb = new StringBuilder(96);
            if (NvmlGetName(_device, sb, (uint)sb.Capacity) == 0) _gpuName = sb.ToString();

            if (NvmlGetMemory(_device, out NvmlMemory m) == 0 && m.Total > 0)
                _vramTotalGb = m.Total / (1024.0 * 1024 * 1024);

            Storage.Log($"NVML: using {_gpuName ?? "device 0"}, {_vramTotalGb:F1} GB VRAM");
            return true;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException ex)
        {
            // A very old driver without the _v2 entry points. Not worth a
            // fallback ladder; the sensors simply go missing.
            Storage.Log($"NVML: driver too old for this API ({ex.Message})");
            return false;
        }
    }

    private static bool ProbeLibrary()
    {
        string[] candidates =
        {
            "nvml.dll",   // System32, via the normal search order
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "NVIDIA Corporation", "NVSMI", "nvml.dll"),
        };

        foreach (string c in candidates)
            if (LoadLibraryW(c) != IntPtr.Zero) return true;

        return false;
    }

    public IReadOnlyList<SensorDescriptor> Describe()
    {
        // Nothing is described when there is no card: an id with no provider is
        // how the editor knows to grey it out, and claiming these exist on an
        // AMD machine would only produce layers that always read "--".
        if (!Available) return Array.Empty<SensorDescriptor>();

        return new List<SensorDescriptor>
        {
            new("gpu.temp",         "GPU temperature",  "GPU", "°C", 0, 100),
            new("gpu.load",         "GPU load",         "GPU", "%",  0, 100),
            new("gpu.vram.load",    "GPU memory bus",   "GPU", "%",  0, 100),
            new("gpu.vram.used",    "GPU memory in use","GPU", "GB", 0, _vramTotalGb),
            new("gpu.vram.total",   "GPU memory total", "GPU", "GB", 0, _vramTotalGb),
            new("gpu.vram.percent", "GPU memory load",  "GPU", "%",  0, 100),
            new("gpu.power",        "GPU power",        "GPU", "W",  0, 600),
            new("gpu.clock",        "GPU core clock",   "GPU", "MHz",0, 3500),
            new("gpu.clock.mem",    "GPU memory clock", "GPU", "MHz",0, 12000),
            new("gpu.fan",          "GPU fan",          "GPU", "%",  0, 100),
        };
    }

    public void Poll(ISensorSink sink)
    {
        if (_device == IntPtr.Zero) return;

        if (NvmlGetTemperature(_device, TemperatureGpu, out uint temp) == 0)
            sink.Publish("gpu.temp", temp);

        if (NvmlGetUtilization(_device, out NvmlUtilization util) == 0)
        {
            sink.Publish("gpu.load", util.Gpu);
            sink.Publish("gpu.vram.load", util.Memory);
        }

        if (NvmlGetMemory(_device, out NvmlMemory mem) == 0 && mem.Total > 0)
        {
            const double GB = 1024.0 * 1024 * 1024;
            sink.Publish("gpu.vram.used", mem.Used / GB);
            sink.Publish("gpu.vram.total", mem.Total / GB);
            sink.Publish("gpu.vram.percent", 100.0 * mem.Used / mem.Total);
        }

        if (NvmlGetPower(_device, out uint mw) == 0) sink.Publish("gpu.power", mw / 1000.0);
        if (NvmlGetClock(_device, ClockGraphics, out uint core) == 0) sink.Publish("gpu.clock", core);
        if (NvmlGetClock(_device, ClockMem, out uint vmem) == 0) sink.Publish("gpu.clock.mem", vmem);

        // A passively cooled or fanless card returns NOT_SUPPORTED here, which
        // is a fact about the card rather than a failure.
        if (NvmlGetFan(_device, out uint fan) == 0) sink.Publish("gpu.fan", fan);
    }

    public void Dispose()
    {
        if (_started)
        {
            try { NvmlShutdown(); } catch { /* shutdown must not throw */ }
            _started = false;
        }
        _device = IntPtr.Zero;
    }
}
