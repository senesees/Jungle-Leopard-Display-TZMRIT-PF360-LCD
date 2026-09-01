using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace JLDisplayManager.Services.Sensors;

/// <summary>
/// The slice of pdh.dll needed to read performance counters.
///
/// English counter names throughout (<c>PdhAddEnglishCounterW</c>): the display
/// names are localised, so <c>\Processor Information(_Total)\% Processor Utility</c>
/// simply does not exist on a German Windows. Using the English API means the
/// same string works everywhere.
/// </summary>
internal static class Pdh
{
    private const string Dll = "pdh.dll";

    public const uint PDH_FMT_DOUBLE = 0x00000200;
    public const uint PDH_FMT_NOCAP100 = 0x00008000;

    public const uint PDH_CSTATUS_VALID_DATA = 0x00000000;
    public const uint PDH_CSTATUS_NEW_DATA = 0x00000001;
    public const uint PDH_MORE_DATA = 0x800007D2;

    [StructLayout(LayoutKind.Sequential)]
    public struct PDH_FMT_COUNTERVALUE
    {
        public uint CStatus;
        public double doubleValue;
    }

    // The array form is a name pointer plus the same union, which is why this
    // needs explicit layout: the value is 8-aligned after the pointer.
    [StructLayout(LayoutKind.Sequential)]
    public struct PDH_FMT_COUNTERVALUE_ITEM
    {
        public IntPtr szName;
        public uint CStatus;
        public double doubleValue;
    }

    [DllImport(Dll, CharSet = CharSet.Unicode)]
    public static extern uint PdhOpenQueryW(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport(Dll, CharSet = CharSet.Unicode)]
    public static extern uint PdhAddEnglishCounterW(IntPtr query, string path, IntPtr userData,
        out IntPtr counter);

    [DllImport(Dll)]
    public static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport(Dll)]
    public static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format,
        out uint type, out PDH_FMT_COUNTERVALUE value);

    [DllImport(Dll, CharSet = CharSet.Unicode)]
    public static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format,
        ref uint bufferSize, out uint itemCount, IntPtr itemBuffer);

    [DllImport(Dll)]
    public static extern uint PdhCloseQuery(IntPtr query);

    /// <summary>
    /// One counter in a query. A wildcard path such as
    /// <c>\GPU Engine(*engtype_3D)\Utilization Percentage</c> resolves to many
    /// instances, which is what <see cref="Sum"/> and <see cref="Values"/> are for.
    /// </summary>
    public sealed class Counter
    {
        public Counter(IntPtr handle, string path) { Handle = handle; Path = path; }

        public IntPtr Handle { get; }
        public string Path { get; }

        /// <summary>
        /// A single-instance counter's value, or null when it has no valid data
        /// yet — which is normal on the very first collection, since a rate
        /// counter needs two samples before it means anything.
        /// </summary>
        public double? Value(bool uncapped = false)
        {
            uint fmt = PDH_FMT_DOUBLE | (uncapped ? PDH_FMT_NOCAP100 : 0);
            uint rc = PdhGetFormattedCounterValue(Handle, fmt, out _, out PDH_FMT_COUNTERVALUE v);
            if (rc != 0) return null;
            if (v.CStatus != PDH_CSTATUS_VALID_DATA && v.CStatus != PDH_CSTATUS_NEW_DATA) return null;
            return v.doubleValue;
        }

        /// <summary>Every instance of a wildcard counter, by instance name.</summary>
        public List<KeyValuePair<string, double>> Values(bool uncapped = false)
        {
            var result = new List<KeyValuePair<string, double>>();
            uint fmt = PDH_FMT_DOUBLE | (uncapped ? PDH_FMT_NOCAP100 : 0);

            uint size = 0, count = 0;
            uint rc = PdhGetFormattedCounterArrayW(Handle, fmt, ref size, out count, IntPtr.Zero);
            if (rc != PDH_MORE_DATA || size == 0) return result;

            IntPtr buf = Marshal.AllocHGlobal((int)size);
            try
            {
                rc = PdhGetFormattedCounterArrayW(Handle, fmt, ref size, out count, buf);
                if (rc != 0) return result;

                int stride = Marshal.SizeOf<PDH_FMT_COUNTERVALUE_ITEM>();
                for (int i = 0; i < count; i++)
                {
                    var item = Marshal.PtrToStructure<PDH_FMT_COUNTERVALUE_ITEM>(buf + i * stride);
                    if (item.CStatus != PDH_CSTATUS_VALID_DATA && item.CStatus != PDH_CSTATUS_NEW_DATA)
                        continue;

                    string name = item.szName == IntPtr.Zero
                        ? string.Empty
                        : Marshal.PtrToStringUni(item.szName) ?? string.Empty;

                    result.Add(new KeyValuePair<string, double>(name, item.doubleValue));
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }

            return result;
        }

        /// <summary>
        /// Every instance added up. What "GPU utilisation" actually means on
        /// Windows: the OS reports one figure per engine per process, and the
        /// number people expect is the total across them.
        /// </summary>
        public double Sum(bool uncapped = false)
        {
            double total = 0;
            foreach (KeyValuePair<string, double> kv in Values(uncapped)) total += kv.Value;
            return total;
        }
    }

    /// <summary>A PDH query and the counters added to it.</summary>
    public sealed class Query : IDisposable
    {
        private IntPtr _handle;

        private Query(IntPtr handle) { _handle = handle; }

        public static Query? Open()
        {
            return PdhOpenQueryW(null, IntPtr.Zero, out IntPtr h) == 0 ? new Query(h) : null;
        }

        /// <summary>
        /// Adds a counter, or returns null when this machine has no such counter
        /// — a perfectly ordinary outcome for GPU engines on a headless box.
        /// </summary>
        public Counter? Add(string path)
        {
            if (_handle == IntPtr.Zero) return null;
            return PdhAddEnglishCounterW(_handle, path, IntPtr.Zero, out IntPtr c) == 0
                ? new Counter(c, path)
                : null;
        }

        /// <summary>
        /// Samples every counter. Rate counters need two collections spaced
        /// apart before they read anything, so the first call after opening
        /// always yields nothing useful.
        /// </summary>
        public bool Collect() => _handle != IntPtr.Zero && PdhCollectQueryData(_handle) == 0;

        public void Dispose()
        {
            if (_handle != IntPtr.Zero)
            {
                PdhCloseQuery(_handle);
                _handle = IntPtr.Zero;
            }
        }
    }
}
