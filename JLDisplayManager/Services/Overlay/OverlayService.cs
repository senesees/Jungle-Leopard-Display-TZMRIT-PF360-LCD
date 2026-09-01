using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

using JLDisplayManager.Interop;
using JLDisplayManager.Models;
using JLDisplayManager.Models.Overlay;
using JLDisplayManager.Services.Sensors;

namespace JLDisplayManager.Services.Overlay;

/// <summary>
/// Renders the active overlay profile and pushes it to the panel.
///
/// Runs on its own STA thread with its own Dispatcher rather than on the UI
/// thread, for two reasons. The tray app must not hitch while rendering — the
/// render is the most expensive step in the whole overlay pipeline at roughly
/// 3 ms — and the overlay has to keep updating while the main window is hidden,
/// which is this app's normal state.
/// </summary>
public sealed class OverlayService : IDisposable
{
    private const int W = OverlayRenderer.PanelWidth;
    private const int H = OverlayRenderer.PanelHeight;

    private readonly Func<bool> _isPlaying;
    private readonly AppSettings _settings;
    private readonly object _stateLock = new();

    private Thread? _thread;
    private Dispatcher? _dispatcher;
    private DispatcherTimer? _timer;

    // Reused every frame: a 960x480 surface is 1.8 MB, and allocating one ten
    // times a second would be the most expensive thing here by a wide margin.
    private RenderTargetBitmap? _target;
    private DrawingVisual? _visual;
    private byte[]? _pixels;

    private OverlaySettings _overlays = new();
    private OverlayProfile? _profile;

    /// <summary>
    /// The render thread's own copy of the profile.
    ///
    /// It exists because the editor mutates <see cref="_profile"/>'s layer list
    /// from the UI thread — adding, removing, reordering — while this thread
    /// walks it ten times a second. Sharing the live list crashed with
    /// "Collection was modified; enumeration operation may not execute" the
    /// moment a layer was deleted with the panel running.
    ///
    /// Refresh is already called after every edit, so re-snapshotting there
    /// keeps this current without the render thread ever holding a lock across
    /// a draw. The copy is shallow on purpose: layer *objects* are still shared,
    /// so a drag in progress shows through immediately, and a torn read of an X
    /// coordinate costs one frame of staleness rather than an exception.
    /// </summary>
    private OverlayProfile? _render;

    private string _lastSignature = "";
    private bool _enabled;

    public OverlayService(Func<bool> isPlaying, AppSettings settings)
    {
        _isPlaying = isPlaying;
        _settings = settings;
        Sensors = new SensorRegistry();
    }

    /// <summary>
    /// The design surface for the current mounting, which the editor's canvas
    /// has to match. Swaps to 480x960 at 90 and 270 degrees.
    /// </summary>
    public (double Width, double Height) DesignSize =>
        OverlayRenderer.DesignSize(_settings.Rotate);

    /// <summary>Shared with the editor, so it draws against the same live values.</summary>
    public SensorRegistry Sensors { get; }

    /// <summary>Frames actually rendered and pushed. Diagnostics only.</summary>
    public long Rendered { get; private set; }

    /// <summary>Ticks that found nothing had changed and did no work.</summary>
    public long Skipped { get; private set; }

    // -----------------------------------------------------------------------

    /// <summary>
    /// Starts the sensors and the render thread. Providers are registered in
    /// preference order: NVML before PDH, because both describe gpu.load and
    /// the first to claim an id keeps it.
    /// </summary>
    public void Start(OverlaySettings overlays)
    {
        _overlays = overlays;
        _enabled = overlays.Enabled;

        // Through Refresh rather than assigning _profile, so the render thread's
        // copy exists before the first tick.
        Refresh(overlays.Active());

        // Order is preference: the first provider to claim an id keeps it.
        // NVML before PDH because it asks the driver rather than summing engine
        // counters, and the hardware monitors before neither of those — they are
        // the only source of CPU temperature at all.
        Sensors.Add(new NvmlProvider());

        if (_settings.UseLibreHardwareMonitor)
            Sensors.Add(new LibreHardwareMonitorProvider(_settings.LibreHardwareMonitorPort));

        if (_settings.UseHwInfo) Sensors.Add(new HwInfoProvider());

        Sensors.Add(new PdhProvider());
        Sensors.Add(new SystemProvider());
        Sensors.Start(overlays.SensorPollMs);

        NativeMethods.jl_overlay_set_enabled(_enabled ? 1 : 0);

        _thread = new Thread(RenderLoop)
        {
            Name = "JL overlay renderer",
            IsBackground = true,
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    /// <summary>
    /// Turns compositing on or off. The native side treats off as free, so this
    /// genuinely costs nothing when the feature is unused.
    /// </summary>
    public void SetEnabled(bool on)
    {
        lock (_stateLock)
        {
            _enabled = on;
            _overlays.Enabled = on;
            _lastSignature = "";   // force a redraw when it comes back on
        }

        NativeMethods.jl_overlay_set_enabled(on ? 1 : 0);
        if (!on) NativeMethods.jl_overlay_clear();
    }

    public bool Enabled
    {
        get { lock (_stateLock) return _enabled; }
    }

    /// <summary>
    /// Switches profile, or picks up edits to the current one. Clearing the
    /// signature is what makes an edit appear on the next tick rather than
    /// whenever a sensor next happens to move.
    ///
    /// Call it after any change to the active profile, including edits to layers
    /// already in it: this is what re-snapshots the list for the render thread,
    /// so an edit that skips it is an edit the panel will not show.
    /// </summary>
    public void Refresh(OverlayProfile? profile)
    {
        OverlayProfile? active = profile ?? _overlays.Active();

        // Copied on the caller's thread, which is the one that owns the list.
        // Doing it here rather than in Tick is the whole fix: the render thread
        // then only ever reads a list nobody else can touch.
        OverlayProfile? copy = active?.ShallowCopy();

        lock (_stateLock)
        {
            _profile = active;
            _render = copy;
            _lastSignature = "";
        }
    }

    public OverlayProfile? Profile
    {
        get { lock (_stateLock) return _profile; }
    }

    // -----------------------------------------------------------------------

    private void RenderLoop()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;

        _visual = new DrawingVisual();
        _target = new RenderTargetBitmap(W, H, 96, 96, PixelFormats.Pbgra32);
        _pixels = new byte[W * H * 4];

        int hz = Math.Clamp(_overlays.RenderHz, 1, 30);
        _timer = new DispatcherTimer(DispatcherPriority.Normal, _dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(1000.0 / hz),
        };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();

        Dispatcher.Run();
    }

    private void Tick()
    {
        OverlayProfile? profile;
        bool enabled;

        lock (_stateLock)
        {
            profile = _render;   // never _profile: see the field's remarks
            enabled = _enabled;
        }

        if (!enabled || profile == null) return;

        // Read once per tick: the user can change the mounting in Settings while
        // this is running, and the design surface has to follow.
        int rotate = _settings.Rotate;
        (double dw, double dh) = OverlayRenderer.DesignSize(rotate);

        var ctx = new LayerContext
        {
            IsPlaying = SafeIsPlaying(),
            AssetDirectory = Storage.OverlayAssetDirectory,
            Width = dw,
            Height = dh,
        };

        SensorSnapshot values = Sensors.Snapshot();

        // Rendering is the expensive step, so the cheap question comes first:
        // would this frame look any different from the last one? On an idle
        // machine the answer is usually no.
        // Rotation belongs in the signature: turning the panel changes every
        // pixel without moving a single sensor.
        string signature = rotate + "|" + Signature(profile, values, ctx);
        lock (_stateLock)
        {
            if (signature == _lastSignature) { Skipped++; return; }
            _lastSignature = signature;
        }

        try
        {
            using (DrawingContext dc = _visual!.RenderOpen())
            {
                // The layers are drawn in the orientation the viewer sees, then
                // turned into the panel's buffer. ffmpeg pre-rotates the video
                // so the pump head's physical turn cancels it out; without the
                // same turn here, the overlay would come out square to the
                // buffer and therefore sideways on the glass.
                Transform? turn = OverlayRenderer.RotationTransform(rotate);
                if (turn != null) dc.PushTransform(turn);

                OverlayRenderer.DrawProfile(dc, profile, values, ctx);

                if (turn != null) dc.Pop();
            }

            // Clear first: RenderTargetBitmap accumulates otherwise, and last
            // frame's text would stay behind this one's.
            _target!.Clear();
            _target.Render(_visual);

            // Pbgra32 is already premultiplied, which is exactly what the native
            // blend expects — no conversion pass on either side.
            _target.CopyPixels(_pixels!, W * 4, 0);

            NativeMethods.jl_overlay_update(_pixels!, W, H);
            Rendered++;
        }
        catch (Exception ex)
        {
            // A render that throws must not kill the thread, or the overlay
            // silently stops for the rest of the session.
            Storage.Log($"overlay render failed: {ex.Message}");
        }
    }

    private bool SafeIsPlaying()
    {
        try { return _isPlaying(); }
        catch { return false; }
    }

    /// <summary>
    /// What this frame would show, as a string. Compared against the last frame
    /// to decide whether drawing is worth it.
    ///
    /// Built from rendered text and quantised fractions rather than from raw
    /// sensor values, because that is what actually reaches the glass: smoothed
    /// values drift continuously and never settle, but "62°C" stays "62°C" for
    /// a long time, and a bar whose fill moves by a fifth of a pixel has not
    /// changed in any sense the viewer can see.
    /// </summary>
    private static string Signature(OverlayProfile profile, SensorSnapshot values, LayerContext ctx)
    {
        var sb = new StringBuilder(256);
        sb.Append(profile.Id.ToString("N")).Append(ctx.IsPlaying ? '1' : '0').Append('|');

        foreach (OverlayLayer layer in profile.Layers)
        {
            if (!layer.Enabled) continue;

            sb.Append(layer.Id.ToString("N")[..8]).Append(':');

            switch (layer)
            {
                case TextLayer t:
                    sb.Append(TokenFormatter.Format(t.Template, values));
                    Append(sb, values, t.ThresholdSource);
                    break;

                case BarLayer b:
                    Append(sb, values, b.Source);
                    break;

                case GaugeLayer g:
                    Append(sb, values, g.Source);
                    sb.Append(TokenFormatter.Format(g.CentreTemplate, values));
                    sb.Append(TokenFormatter.Format(g.Caption, values));
                    break;

                case GraphLayer gr:
                    // A graph is the one layer whose picture changes when the
                    // reading does not: the window slides, so an unchanged value
                    // still shifts every sample one place left. Keyed on the
                    // poll version rather than the value, which is precisely
                    // "a new sample exists" and nothing more.
                    Append(sb, values, gr.Source);
                    sb.Append(values.Version);
                    break;
            }

            // Visibility can flip without any drawn value changing.
            Append(sb, values, layer.VisibleSource);
            sb.Append('|');
        }

        return sb.ToString();
    }

    private static void Append(StringBuilder sb, SensorSnapshot values, string? source)
    {
        if (string.IsNullOrEmpty(source)) return;

        SensorReading r = values[source];
        if (!r.Available) { sb.Append('~'); return; }

        // Two decimals is finer than a 960-pixel panel can show for any bar, so
        // this never hides a visible change.
        sb.Append(Math.Round(r.Value, 2).ToString("0.##"));
    }

    // -----------------------------------------------------------------------

    public void Dispose()
    {
        try
        {
            NativeMethods.jl_overlay_set_enabled(0);
            NativeMethods.jl_overlay_clear();
        }
        catch
        {
            // The native side may already be gone during shutdown.
        }

        Sensors.Dispose();

        Dispatcher? d = _dispatcher;
        if (d != null)
        {
            d.Invoke(() => _timer?.Stop());
            d.InvokeShutdown();
        }

        _thread?.Join(1000);
    }
}
