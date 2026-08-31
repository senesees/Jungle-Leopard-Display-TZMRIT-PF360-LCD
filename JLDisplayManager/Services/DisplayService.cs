using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

using JLDisplayManager.Interop;
using JLDisplayManager.Models;

namespace JLDisplayManager.Services;

/// <summary>
/// The app's single connection to the panel.
///
/// The native side does all the blocking work on its own threads and publishes a
/// status struct; this class polls that struct on the dispatcher and turns it
/// into properties the UI can bind to. Nothing here ever blocks — the one rule
/// that makes the whole design hang together.
/// </summary>
public sealed class DisplayService : INotifyPropertyChanged, IDisposable
{
    private const int PollMs = 250;
    private const int ReconnectMs = 2000;

    private readonly AppSettings _settings;
    private readonly DispatcherTimer _poll;

    private int _lastFrameCount = -1;
    private int _lastFinishedCount;
    private DateTime _nextReconnectAttempt = DateTime.MinValue;
    private bool _everConnected;

    private NativeMethods.JlState _state = NativeMethods.JlState.Disconnected;
    private bool _connected;
    private string _port = "";
    private string _message = "";
    private string _error = "";
    private double _fps;
    private long _framesSent;
    private long _framesDropped;
    private BitmapSource? _preview;
    private MediaItem? _current;

    public DisplayService(AppSettings settings)
    {
        _settings = settings;

        _poll = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(PollMs),
        };
        _poll.Tick += (_, _) => Poll();
    }

    /// <summary>Raised when an item reached its own end — a video running out.</summary>
    public event EventHandler? ItemFinished;

    // -----------------------------------------------------------------------
    // Bindable state
    // -----------------------------------------------------------------------

    public NativeMethods.JlState State
    {
        get => _state;
        private set
        {
            if (Set(ref _state, value)) Raise(nameof(StateText));
        }
    }

    public bool Connected
    {
        get => _connected;
        private set
        {
            if (Set(ref _connected, value)) Raise(nameof(StateText));
        }
    }

    public string Port
    {
        get => _port;
        private set => Set(ref _port, value);
    }

    public string Message
    {
        get => _message;
        private set => Set(ref _message, value);
    }

    public string Error
    {
        get => _error;
        private set
        {
            if (Set(ref _error, value)) Raise(nameof(HasError));
        }
    }

    public bool HasError => !string.IsNullOrEmpty(_error);

    public double Fps
    {
        get => _fps;
        private set => Set(ref _fps, value);
    }

    public long FramesSent
    {
        get => _framesSent;
        private set => Set(ref _framesSent, value);
    }

    public long FramesDropped
    {
        get => _framesDropped;
        private set => Set(ref _framesDropped, value);
    }

    /// <summary>The last frame actually sent to the panel — a true preview.</summary>
    public BitmapSource? Preview
    {
        get => _preview;
        private set => Set(ref _preview, value);
    }

    public MediaItem? Current
    {
        get => _current;
        private set
        {
            if (Set(ref _current, value)) Raise(nameof(StateText));
        }
    }

    public string StateText => State switch
    {
        NativeMethods.JlState.Disconnected => "Not connected",
        NativeMethods.JlState.Idle => Connected ? $"Connected on {Port} — idle" : "Not connected",
        NativeMethods.JlState.Preparing => $"Preparing {Current?.Name}…",
        NativeMethods.JlState.Calibrating => $"Calibrating {Current?.Name}…",
        NativeMethods.JlState.Preprocessing => $"Preprocessing {Current?.Name}…",
        NativeMethods.JlState.Playing => Current is null ? "Playing" : $"Showing {Current.Name}",
        NativeMethods.JlState.Error => "Error",
        _ => "",
    };

    // -----------------------------------------------------------------------
    // Lifecycle
    // -----------------------------------------------------------------------

    public void Start()
    {
        NativeMethods.VerifyLayout();
        ApplyPreprocess();
        _poll.Start();
        Connect();
    }

    /// <summary>
    /// Pushes the preprocessing mode and both budgets down to the native
    /// session. Takes effect on the next item started, so changing any of them
    /// never disturbs what is playing.
    /// </summary>
    public void ApplyPreprocess()
    {
        NativeMethods.jl_set_preprocess((int)_settings.Preprocess);
        NativeMethods.jl_set_pack_budgets(
            _settings.MemoryBudgetMB * 1024L * 1024L,
            _settings.DiskBudgetMB * 1024L * 1024L);
    }

    /// <summary>Bytes held by the on-disk pack cache.</summary>
    public static long PackCacheBytes() => NativeMethods.jl_pack_cache_bytes();

    /// <summary>
    /// Empties the pack cache. Safe while something is playing — a pack already
    /// mapped runs to the end of its item.
    /// </summary>
    public static void ClearPackCache() => NativeMethods.jl_pack_cache_clear();

    /// <summary>
    /// Opens the port. Failure is not exceptional — the panel is a USB device
    /// that may simply be unplugged — so it reports through <see cref="Error"/>
    /// and lets the reconnect loop keep trying.
    /// </summary>
    public bool Connect()
    {
        string? port = string.IsNullOrWhiteSpace(_settings.Port) ? null : _settings.Port;

        if (NativeMethods.jl_open(port) == NativeMethods.JL_OK)
        {
            _everConnected = true;
            Poll();
            ApplyBrightness();
            Storage.Log($"connected on {Port}");
            return true;
        }

        Poll();
        return false;
    }

    public void Disconnect()
    {
        Current = null;
        NativeMethods.jl_close();
        Preview = null;
        Poll();
    }

    /// <summary>
    /// Releases the device so another program — the CLI, or the vendor app —
    /// can take it. Suppresses reconnection until the user asks for it back.
    /// </summary>
    public void ReleaseDevice()
    {
        _settings.AutoReconnect = false;
        Disconnect();
        Storage.Log("device released at the user's request");
    }

    // -----------------------------------------------------------------------
    // Content
    // -----------------------------------------------------------------------

    /// <summary>
    /// Puts an item on the panel. Returns immediately: the native side spins up
    /// a worker and the poll loop reports what happens next.
    /// </summary>
    /// <param name="forceNoLoop">
    /// Set by the playlist for videos, which must hand over rather than repeat.
    /// </param>
    public bool Play(MediaItem item, bool forceNoLoop = false)
    {
        if (!Connected) return false;

        if (!File.Exists(item.Path))
        {
            Error = $"{item.Name} is missing from disk";
            return false;
        }

        var opts = BuildOpts(item, forceNoLoop);
        Current = item;

        int rc = item.IsVideo
            ? NativeMethods.jl_play_video(item.Path, ref opts)
            : NativeMethods.jl_show_image(item.Path, ref opts);

        if (rc != NativeMethods.JL_OK)
        {
            Current = null;
            Poll();
            return false;
        }

        Poll();
        return true;
    }

    public void Stop()
    {
        Current = null;
        NativeMethods.jl_stop();
        Poll();
    }

    public void ApplyBrightness()
    {
        if (Connected) NativeMethods.jl_set_brightness(_settings.Brightness);
    }

    public NativeMethods.JlRenderOpts BuildOpts(MediaItem item, bool forceNoLoop = false)
    {
        return new NativeMethods.JlRenderOpts
        {
            Stretch = (item.Stretch ?? _settings.Stretch) ? 1 : 0,
            Rotate = item.Rotate ?? _settings.Rotate,
            Fps = _settings.Fps,
            Quality = 0,                       // always let the core calibrate
            Loop = (item.Loop && !forceNoLoop) ? 1 : 0,
            Recalibrate = 0,
            Hwaccel = _settings.Hwaccel,
        };
    }

    public string? DeviceInfo()
    {
        if (!Connected) return null;
        var sb = new StringBuilder(2048);
        return NativeMethods.jl_get_info(sb, sb.Capacity) == NativeMethods.JL_OK
            ? sb.ToString()
            : null;
    }

    public static string? FindFfmpeg()
    {
        var sb = new StringBuilder(512);
        return NativeMethods.jl_ffmpeg_path(sb, sb.Capacity) == NativeMethods.JL_OK
            ? sb.ToString()
            : null;
    }

    // -----------------------------------------------------------------------
    // Polling
    // -----------------------------------------------------------------------

    private void Poll()
    {
        NativeMethods.jl_get_status(out var s);

        State = (NativeMethods.JlState)s.State;
        Connected = s.Connected != 0;
        Port = s.Port;
        Message = s.Message;
        Error = s.Error;
        Fps = s.Fps;
        FramesSent = s.FramesSent;
        FramesDropped = s.FramesDropped;

        if (s.FrameCount != _lastFrameCount)
        {
            _lastFrameCount = s.FrameCount;
            RefreshPreview();
        }

        // A counter rather than a flag, so an item that starts and finishes
        // between two polls still registers exactly once.
        if (s.FinishedCount != _lastFinishedCount)
        {
            _lastFinishedCount = s.FinishedCount;
            ItemFinished?.Invoke(this, EventArgs.Empty);
        }

        if (!Connected) MaybeReconnect();
    }

    private void MaybeReconnect()
    {
        if (!_settings.AutoReconnect) return;
        if (DateTime.UtcNow < _nextReconnectAttempt) return;

        _nextReconnectAttempt = DateTime.UtcNow.AddMilliseconds(ReconnectMs);

        // Checking for the port first keeps the common unplugged case quiet:
        // opening a port that is not there would set an error every two seconds.
        var sb = new StringBuilder(64);
        if (NativeMethods.jl_find_port(sb, sb.Capacity) != NativeMethods.JL_OK) return;

        if (NativeMethods.jl_open(null) == NativeMethods.JL_OK)
        {
            _everConnected = true;
            ApplyBrightness();
            Storage.Log("reconnected");
            Reconnected?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Raised after the reconnect loop gets the device back.</summary>
    public event EventHandler? Reconnected;

    public bool EverConnected => _everConnected;

    private void RefreshPreview()
    {
        int size = NativeMethods.jl_get_last_frame(null, 0);
        if (size <= 0)
        {
            Preview = null;
            return;
        }

        var buffer = new byte[size];
        int got = NativeMethods.jl_get_last_frame(buffer, size);
        if (got <= 0 || got > size) return;

        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.StreamSource = new MemoryStream(buffer, 0, got, writable: false);
            bmp.CacheOption = BitmapCacheOption.OnLoad;   // decode now, release the stream
            bmp.EndInit();
            bmp.Freeze();                                  // usable from any thread
            Preview = bmp;
        }
        catch
        {
            // A torn frame is not worth reporting; the next one is 200 ms away.
        }
    }

    public void Dispose()
    {
        _poll.Stop();
        NativeMethods.jl_close();
    }

    // -----------------------------------------------------------------------

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    private void Raise(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
