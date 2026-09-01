using System;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;

using JLDisplayManager.Models;

namespace JLDisplayManager.Services.Sensors;

/// <summary>
/// The same readings as <see cref="LibreHardwareMonitorProvider"/>, from HWiNFO
/// instead — because a great many people already run HWiNFO and will not want a
/// second monitoring app just for this.
///
/// HWiNFO publishes a shared memory block rather than a socket, which makes this
/// cheaper than the HTTP path: no request, no JSON, just a mapped view read in
/// place. It has to be switched on in HWiNFO (Settings → Shared Memory Support)
/// and that support times out after 12 hours in the free version, which is worth
/// knowing when it stops for no apparent reason.
///
/// Layout below is HWiNFO's published SDK structure. Sizes are load-bearing: the
/// header names the stride of each array, so the block is walked with those
/// rather than with <c>Marshal.SizeOf</c>, and a future revision that adds a
/// field keeps working.
/// </summary>
public sealed class HwInfoProvider : ISensorProvider
{
    private const string MapName = "Global\\HWiNFO_SENS_SM2";

    /// <summary>'SiWH' little-endian, HWiNFO's marker for a valid block.</summary>
    private const uint Signature = 0x53695748;

    [StructLayout(LayoutKind.Sequential)]
    private struct SharedMemHeader
    {
        public uint Signature;
        public uint Version;
        public uint Revision;
        public long PollTime;
        public uint SensorSectionOffset;
        public uint SensorElementSize;
        public uint SensorElementCount;
        public uint ReadingSectionOffset;
        public uint ReadingElementSize;
        public uint ReadingElementCount;
    }

    // Only the parts that are read. The trailing min/max/average of a reading
    // are skipped rather than declared, since nothing here wants them.
    private const int SensorNameOrigOffset = 8;      // after id + instance
    private const int SensorNameLength = 128;

    private const int ReadingTypeOffset = 0;
    private const int ReadingSensorIndexOffset = 4;
    private const int ReadingLabelOrigOffset = 12;   // after type + index + id
    private const int ReadingLabelLength = 128;
    private const int ReadingUnitLength = 16;
    private const int ReadingValueOffset = 12 + 128 + 128 + 16;

    private MemoryMappedFile? _map;
    private bool _available;
    private DateTime _quietUntil = DateTime.MinValue;

    public string Name => "HWiNFO";

    public bool Available => _available;

    public bool Start()
    {
        TryOpen();
        return true;
    }

    public IReadOnlyList<SensorDescriptor> Describe() => HardwareNames.Descriptors;

    private bool TryOpen()
    {
        try
        {
            _map = MemoryMappedFile.OpenExisting(MapName, MemoryMappedFileRights.Read);
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;   // HWiNFO not running, or shared memory not enabled
        }
        catch (UnauthorizedAccessException)
        {
            Storage.Log("HWiNFO: shared memory found but not readable");
            return false;
        }
    }

    public void Poll(ISensorSink sink)
    {
        if (_map == null)
        {
            if (DateTime.UtcNow < _quietUntil) return;
            if (!TryOpen())
            {
                _quietUntil = DateTime.UtcNow.AddSeconds(15);
                return;
            }
        }

        try
        {
            using MemoryMappedViewAccessor view =
                _map!.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);

            view.Read(0, out SharedMemHeader header);

            if (header.Signature != Signature)
            {
                _available = false;
                return;
            }

            // Sensor names first: a reading only carries an index into them, and
            // the component name is what tells a CPU temperature from a GPU one.
            var hardware = new string[header.SensorElementCount];
            for (uint i = 0; i < header.SensorElementCount; i++)
            {
                long at = header.SensorSectionOffset + i * (long)header.SensorElementSize;
                hardware[i] = ReadAscii(view, at + SensorNameOrigOffset, SensorNameLength);
            }

            int found = 0;

            for (uint i = 0; i < header.ReadingElementCount; i++)
            {
                long at = header.ReadingSectionOffset + i * (long)header.ReadingElementSize;

                uint type = view.ReadUInt32(at + ReadingTypeOffset);
                uint sensorIndex = view.ReadUInt32(at + ReadingSensorIndexOffset);
                string label = ReadAscii(view, at + ReadingLabelOrigOffset, ReadingLabelLength);

                string component = sensorIndex < hardware.Length ? hardware[sensorIndex] : "";

                string? id = HardwareNames.Match(component, label, KindOf(type));
                if (id == null) continue;

                sink.Publish(id, view.ReadDouble(at + ReadingValueOffset));
                found++;
            }

            if (!_available && found > 0) Storage.Log($"HWiNFO: connected, {found} sensor(s) matched");
            _available = found > 0;
        }
        catch (Exception ex)
        {
            // HWiNFO closing pulls the mapping out from under us mid-read.
            Storage.Log($"HWiNFO: read failed ({ex.Message})");
            _map?.Dispose();
            _map = null;
            _available = false;
            _quietUntil = DateTime.UtcNow.AddSeconds(15);
        }
    }

    /// <summary>HWiNFO's SENSOR_READING_TYPE.</summary>
    private static SensorKind KindOf(uint type) => type switch
    {
        1 => SensorKind.Temperature,
        2 => SensorKind.Voltage,
        3 => SensorKind.Fan,
        5 => SensorKind.Power,
        6 => SensorKind.Clock,
        7 => SensorKind.Load,
        _ => SensorKind.Other,
    };

    /// <summary>
    /// A fixed-width, NUL-terminated ASCII field. Read byte by byte because the
    /// declared width is almost always longer than the string in it, and the
    /// bytes past the terminator are undefined rather than blank.
    /// </summary>
    private static string ReadAscii(MemoryMappedViewAccessor view, long offset, int length)
    {
        Span<byte> buffer = stackalloc byte[length];

        for (int i = 0; i < length; i++)
        {
            byte b = view.ReadByte(offset + i);
            if (b == 0) return System.Text.Encoding.ASCII.GetString(buffer[..i]);
            buffer[i] = b;
        }

        return System.Text.Encoding.ASCII.GetString(buffer);
    }

    public void Dispose()
    {
        _map?.Dispose();
        _map = null;
    }
}
