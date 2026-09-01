using System;
using System.Collections.Generic;

namespace JLDisplayManager.Services.Sensors;

/// <summary>
/// What a sensor is, independent of what it currently reads. Descriptors are
/// fixed for the life of a provider, so the editor can list everything that
/// exists — and give a bar a sensible default range — without waiting for a
/// value to arrive.
/// </summary>
/// <param name="Id">
/// The token used in overlay templates, e.g. <c>gpu.temp</c>. Lower case, dotted,
/// stable: these end up saved in profiles, so renaming one breaks people's layouts.
/// </param>
/// <param name="Min">Sensible bottom of a bar or gauge for this source.</param>
/// <param name="Max">
/// Sensible top. Not a hard limit — a network rate has no ceiling, and the value
/// is free to exceed this; layers clamp their own fill rather than the reading.
/// </param>
public sealed record SensorDescriptor(
    string Id,
    string Name,
    string Category,
    string Unit,
    double Min = 0,
    double Max = 100,
    bool IsText = false);

/// <summary>
/// One sensor's current value.
///
/// Both the smoothed and the raw number are kept. A bar wants the smoothed one
/// so it glides instead of twitching; a numeric readout usually wants it too,
/// but a peak or a debug view wants the truth.
/// </summary>
public readonly struct SensorReading
{
    public SensorReading(double value, double raw, string? text, bool available)
    {
        Value = value;
        Raw = raw;
        Text = text;
        Available = available;
    }

    /// <summary>Exponentially smoothed. What layers should normally draw.</summary>
    public double Value { get; }

    /// <summary>The last figure the provider actually reported.</summary>
    public double Raw { get; }

    /// <summary>Set only for sources that are text by nature — a clock, a title.</summary>
    public string? Text { get; }

    /// <summary>
    /// False when nothing is supplying this source — no NVIDIA card, no helper
    /// running, a counter this machine does not have. A layer bound to an
    /// unavailable source draws "--" rather than a misleading zero.
    /// </summary>
    public bool Available { get; }

    public static readonly SensorReading Missing = new(0, 0, null, false);
}

/// <summary>
/// Somewhere past readings are kept, so a snapshot can serve a graph without
/// carrying every sensor's history in every copy.
/// </summary>
public interface ISensorHistory
{
    /// <summary>
    /// The last <paramref name="maxSamples"/> readings of one sensor, oldest
    /// first. Empty when nothing has been recorded for it.
    /// </summary>
    double[] History(string id, int maxSamples);

    /// <summary>How far apart those samples are, which is what turns a window in
    /// seconds into a count.</summary>
    int IntervalMs { get; }
}

/// <summary>
/// An immutable view of every sensor at one instant.
///
/// The render thread takes one of these and works from it for the whole frame,
/// so a value cannot change halfway through drawing and two layers reading the
/// same source can never disagree.
/// </summary>
public sealed class SensorSnapshot
{
    private readonly Dictionary<string, SensorReading> _values;
    private readonly ISensorHistory? _history;

    public SensorSnapshot(Dictionary<string, SensorReading> values,
                          IReadOnlyList<SensorDescriptor> descriptors,
                          long version = 0,
                          ISensorHistory? history = null)
    {
        _values = values;
        Descriptors = descriptors;
        Version = version;
        _history = history;
        TakenAt = DateTime.Now;
    }

    public IReadOnlyList<SensorDescriptor> Descriptors { get; }

    public DateTime TakenAt { get; }

    /// <summary>
    /// Which poll these values came from. Two snapshots taken between polls
    /// carry the same version and hold identical readings, which lets a caller
    /// skip work rather than repeat it — the editor repaints five times a
    /// second against sensors that move once.
    ///
    /// Deliberately not <see cref="TakenAt"/>: that is the moment the copy was
    /// made, so it differs every call and can never say "nothing changed".
    /// </summary>
    public long Version { get; }

    public SensorReading this[string id] =>
        _values.TryGetValue(id, out SensorReading r) ? r : SensorReading.Missing;

    public bool Has(string id) => _values.ContainsKey(id);

    /// <summary>
    /// The last <paramref name="seconds"/> of one sensor, oldest first.
    ///
    /// Deliberately not copied into the snapshot. Copying every sensor's history
    /// on every frame to serve the one layer that wants one would be absurd —
    /// 74 arrays for a single sparkline — so this reaches back to the registry
    /// and copies the one buffer asked for.
    ///
    /// The consequence, stated rather than hidden: these samples are read when
    /// you ask, not when the snapshot was taken, so the newest can be up to one
    /// poll newer than <see cref="TakenAt"/>. For a graph that is meaningless —
    /// it is a picture of the past, and one sample at the right-hand edge cannot
    /// disagree with a readout the way two live values could.
    /// </summary>
    public double[] History(string id, double seconds)
    {
        if (_history == null || seconds <= 0) return Array.Empty<double>();

        int interval = Math.Max(1, _history.IntervalMs);
        int wanted = (int)Math.Ceiling(seconds * 1000.0 / interval);

        return _history.History(id, Math.Clamp(wanted, 2, 4096));
    }

    /// <summary>Whether a graph can draw anything at all from this snapshot.</summary>
    public bool HasHistory => _history != null;

    public static readonly SensorSnapshot Empty =
        new(new Dictionary<string, SensorReading>(), Array.Empty<SensorDescriptor>());
}
