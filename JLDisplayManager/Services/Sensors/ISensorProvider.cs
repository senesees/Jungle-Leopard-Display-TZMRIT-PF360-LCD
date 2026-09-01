using System;
using System.Collections.Generic;

namespace JLDisplayManager.Services.Sensors;

/// <summary>Where a provider puts what it just read.</summary>
public interface ISensorSink
{
    /// <summary>
    /// A numeric reading. Smoothing, staleness and history are the registry's
    /// business; a provider reports the number it measured and nothing more.
    /// </summary>
    void Publish(string id, double value);

    /// <summary>
    /// A reading that is text by nature — a clock, an uptime, a track title.
    /// Never smoothed, and formatted by whoever produced it.
    /// </summary>
    void PublishText(string id, string text);
}

/// <summary>
/// A source of sensor readings.
///
/// Providers are polled on a background thread and must never throw: a machine
/// with no NVIDIA card, no performance counter by that name, or no helper app
/// running is the normal case, not an error. Say so by reporting nothing and
/// leaving <see cref="Available"/> false.
/// </summary>
public interface ISensorProvider : IDisposable
{
    /// <summary>Shown in Settings so it is obvious where a reading comes from.</summary>
    string Name { get; }

    /// <summary>
    /// False when this provider found nothing to talk to. It stays registered
    /// either way, so a helper started later can be picked up without a restart.
    /// </summary>
    bool Available { get; }

    /// <summary>
    /// Everything this provider can supply, whether or not it currently can.
    /// Called once after <see cref="Start"/>; the editor lists these.
    /// </summary>
    IReadOnlyList<SensorDescriptor> Describe();

    /// <summary>
    /// Opens whatever handles the provider needs. Called once. Failing here is
    /// ordinary — return false and the provider simply supplies nothing.
    /// </summary>
    bool Start();

    /// <summary>
    /// Reads current values into <paramref name="sink"/>. Called on a pool
    /// thread at the configured interval. Anything not published this tick keeps
    /// its previous value.
    /// </summary>
    void Poll(ISensorSink sink);
}
