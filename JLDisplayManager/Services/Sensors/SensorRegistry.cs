using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

using JLDisplayManager.Models;

namespace JLDisplayManager.Services.Sensors;

/// <summary>
/// Every sensor the machine can report, polled on a timer and handed to the
/// renderer as an immutable snapshot.
///
/// The split matters: providers run on a pool thread and may block for a
/// millisecond or two talking to PDH or the driver, while the render thread must
/// never wait for them. So readings are accumulated behind a lock and copied out
/// whole, and the renderer works from its own copy for the life of a frame.
/// </summary>
public sealed class SensorRegistry : IDisposable, ISensorHistory
{
    /// <summary>
    /// How many samples of each numeric sensor are kept. 120 is two minutes at
    /// the default poll rate, which is longer than any graph on a 960x480 panel
    /// can usefully show. At ~74 sensors that is about 71 KB — not worth a
    /// smarter structure.
    /// </summary>
    private const int HistoryCapacity = 120;

    /// <summary>
    /// How fast a smoothed value chases the real one. 400 ms is slow enough that
    /// a bar glides rather than twitching on a single busy sample, and fast
    /// enough that it does not feel laggy when load genuinely changes.
    /// </summary>
    private const double SmoothingTauMs = 400.0;

    private readonly List<ISensorProvider> _providers = new();
    private readonly List<SensorDescriptor> _descriptors = new();
    private readonly Dictionary<string, SensorReading> _values = new();
    private readonly Dictionary<string, Ring> _history = new();
    private readonly object _lock = new();

    private readonly Sink _sink;
    private Timer? _timer;
    private int _polling;              // guards against a slow tick re-entering
    private long _lastPollTicks;
    private int _intervalMs = 1000;
    private long _version;

    public SensorRegistry()
    {
        _sink = new Sink(this);
    }

    /// <summary>Providers in the order they were added; first to publish an id wins.</summary>
    public IReadOnlyList<ISensorProvider> Providers => _providers;

    /// <summary>
    /// Adds a provider and starts it. Kept even when it reports unavailable, so
    /// Settings can show it as offline rather than silently omitting it.
    /// </summary>
    public void Add(ISensorProvider provider)
    {
        try
        {
            provider.Start();
        }
        catch (Exception ex)
        {
            Storage.Log($"sensor provider {provider.Name} failed to start: {ex.Message}");
        }

        _providers.Add(provider);

        try
        {
            foreach (SensorDescriptor d in provider.Describe())
            {
                // First registration of an id wins, so a preferred provider added
                // earlier is not displaced by a fallback describing the same
                // source. Publishing follows the same rule.
                if (!_owner.ContainsKey(d.Id))
                {
                    _owner[d.Id] = provider;
                    _descriptors.Add(d);
                }
            }
        }
        catch (Exception ex)
        {
            Storage.Log($"sensor provider {provider.Name} failed to describe: {ex.Message}");
        }
    }

    private readonly Dictionary<string, ISensorProvider> _owner = new();

    public IReadOnlyList<SensorDescriptor> Descriptors => _descriptors;

    /// <summary>Starts polling. Safe to call twice; the interval is clamped.</summary>
    public void Start(int intervalMs)
    {
        _intervalMs = Math.Clamp(intervalMs, 250, 10_000);
        _lastPollTicks = Stopwatch.GetTimestamp();

        _timer?.Dispose();
        _timer = new Timer(_ => Poll(), null, 0, _intervalMs);
    }

    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
    }

    /// <summary>
    /// A copy of everything, as of now. Cheap enough to take once per rendered
    /// frame — a few dozen entries — and the only thing the renderer should read.
    /// </summary>
    public SensorSnapshot Snapshot()
    {
        lock (_lock)
        {
            return new SensorSnapshot(new Dictionary<string, SensorReading>(_values),
                                      _descriptors, _version, this);
        }
    }

    private void Poll()
    {
        // A provider that takes longer than the interval must not stack up ticks
        // behind it; skipping is always better than queueing.
        if (Interlocked.Exchange(ref _polling, 1) == 1) return;

        try
        {
            long now = Stopwatch.GetTimestamp();
            double dtMs = (now - _lastPollTicks) * 1000.0 / Stopwatch.Frequency;
            _lastPollTicks = now;

            // The smoothing factor depends on how long since the last tick, not
            // on the nominal interval — a tick that ran late must not smooth as
            // if it were on time.
            _sink.Alpha = dtMs <= 0 ? 1.0 : 1.0 - Math.Exp(-dtMs / SmoothingTauMs);

            // Bumped once per poll, so a snapshot taken between polls is
            // recognisably the same data. See SensorSnapshot.Version.
            _version++;

            foreach (ISensorProvider p in _providers)
            {
                _sink.Current = p;
                try
                {
                    p.Poll(_sink);
                }
                catch (Exception ex)
                {
                    // One broken provider must not stop the rest from reporting.
                    Storage.Log($"sensor provider {p.Name} threw while polling: {ex.Message}");
                }
            }

            Record();
        }
        finally
        {
            Interlocked.Exchange(ref _polling, 0);
        }
    }

    /// <summary>
    /// One sample of every numeric sensor, after all the providers have had
    /// their say.
    ///
    /// Recorded here rather than inside <see cref="Sink.Publish"/> on purpose: a
    /// provider that skips an id on some tick would otherwise leave that
    /// sensor's history advancing at a different rate from everything else, and
    /// two graphs side by side would be showing different spans of time while
    /// looking identical. Sampling once per tick puts every sensor on the same
    /// grid, and a source that stopped reporting holds its last value — which is
    /// what a graph should show anyway.
    /// </summary>
    private void Record()
    {
        lock (_lock)
        {
            foreach ((string id, SensorReading r) in _values)
            {
                // Text sources have no number to plot.
                if (!r.Available || r.Text != null) continue;

                if (!_history.TryGetValue(id, out Ring? ring))
                {
                    ring = new Ring(HistoryCapacity);
                    _history[id] = ring;
                }

                ring.Add(r.Value);
            }
        }
    }

    public int IntervalMs => _intervalMs;

    public double[] History(string id, int maxSamples)
    {
        lock (_lock)
        {
            return _history.TryGetValue(id, out Ring? ring)
                ? ring.Last(maxSamples)
                : Array.Empty<double>();
        }
    }

    /// <summary>
    /// A fixed-length circular buffer of doubles. Deliberately plain: this is
    /// written once per sensor per second and read a few times a second by one
    /// layer, so there is nothing here worth making clever.
    /// </summary>
    private sealed class Ring
    {
        private readonly double[] _buf;
        private int _next;
        private int _count;

        public Ring(int capacity) { _buf = new double[capacity]; }

        public void Add(double v)
        {
            _buf[_next] = v;
            _next = (_next + 1) % _buf.Length;
            if (_count < _buf.Length) _count++;
        }

        /// <summary>The newest <paramref name="n"/> samples, oldest first.</summary>
        public double[] Last(int n)
        {
            int take = Math.Min(n, _count);
            if (take <= 0) return Array.Empty<double>();

            var outp = new double[take];

            // _next points one past the newest, so the newest is at _next - 1
            // and the run walks backwards from there.
            for (int i = 0; i < take; i++)
            {
                int idx = ((_next - take + i) % _buf.Length + _buf.Length) % _buf.Length;
                outp[i] = _buf[idx];
            }

            return outp;
        }
    }

    public void Dispose()
    {
        Stop();
        foreach (ISensorProvider p in _providers)
        {
            try { p.Dispose(); } catch { /* shutdown must not throw */ }
        }
        _providers.Clear();
    }

    /// <summary>
    /// The sink handed to each provider. Holds the smoothing factor for this
    /// tick and the provider currently being polled, so ownership can be
    /// enforced without every provider having to care.
    /// </summary>
    private sealed class Sink : ISensorSink
    {
        private readonly SensorRegistry _r;

        public Sink(SensorRegistry r) { _r = r; }

        public double Alpha { get; set; } = 1.0;
        public ISensorProvider? Current { get; set; }

        public void Publish(string id, double value)
        {
            if (!Owns(id)) return;
            if (double.IsNaN(value) || double.IsInfinity(value)) return;

            lock (_r._lock)
            {
                double smoothed = _r._values.TryGetValue(id, out SensorReading prev) && prev.Available
                    ? prev.Value + (value - prev.Value) * Alpha
                    : value;   // first reading lands whole rather than easing up from zero

                _r._values[id] = new SensorReading(smoothed, value, null, true);
            }
        }

        public void PublishText(string id, string text)
        {
            if (!Owns(id)) return;

            lock (_r._lock)
            {
                _r._values[id] = new SensorReading(0, 0, text, true);
            }
        }

        // A second provider describing the same id is a fallback, not an
        // override: whoever registered it first keeps it for the session.
        private bool Owns(string id) =>
            Current == null
            || !_r._owner.TryGetValue(id, out ISensorProvider? o)
            || ReferenceEquals(o, Current);
    }
}
