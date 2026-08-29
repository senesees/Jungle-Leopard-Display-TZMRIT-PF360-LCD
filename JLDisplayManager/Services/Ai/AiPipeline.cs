using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

using JLDisplayManager.Models;

namespace JLDisplayManager.Services.Ai;

/// <summary>
/// The AI slideshow: generates images in the background and rotates them onto
/// the panel.
///
/// Two clocks, deliberately independent. A producer task keeps the ready queue
/// topped up to BufferSize; a dwell timer on the UI thread moves the panel on
/// whenever something is ready. Generation on a home GPU takes anywhere from
/// ten seconds to several minutes, and coupling the two would mean the panel
/// sat on a stale image for exactly as long as the backend felt like taking.
///
/// This class owns the panel while it is running, which is why it and
/// PlaylistPlayer are mutually exclusive — App arbitrates that.
/// </summary>
public sealed class AiPipeline : INotifyPropertyChanged, IDisposable
{
    /// <summary>Backoff after a failure, doubling to a cap so a dead server stays quiet.</summary>
    private static readonly TimeSpan FirstRetry = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan MaxRetry = TimeSpan.FromMinutes(5);

    /// <summary>How often the rate-floor countdown ticks.</summary>
    private static readonly TimeSpan OneSecond = TimeSpan.FromSeconds(1);

    private readonly AiSettings _ai;
    private readonly AppLibrary _library;
    private readonly DisplayService _display;
    private readonly PromptEnhancer _enhancer;
    private readonly SwarmClient _swarm;
    private readonly DispatcherTimer _dwell;

    /// <summary>Generated and waiting for its turn on the glass.</summary>
    private readonly ConcurrentQueue<MediaItem> _ready = new();

    /// <summary>Signalled whenever the producer should look at its work again.</summary>
    private readonly SemaphoreSlim _wake = new(0);

    private CancellationTokenSource? _cancel;
    private Task? _producer;

    /// <summary>When the last generation began, for the start-to-start rate floor.</summary>
    private DateTime _lastGenerationStarted = DateTime.MinValue;

    /// <summary>
    /// True while the consumer has run dry and is ticking for something to
    /// show. Lets the producer hand over the moment an image lands.
    /// </summary>
    private bool _waitingForImage;

    private bool _running;
    private string _status = "";
    private string _lastError = "";
    private int _generated;
    private int _failures;
    private MediaItem? _current;
    private EnhancedPrompt? _lastEnhanced;

    public AiPipeline(AiSettings ai, AppLibrary library, DisplayService display)
    {
        _ai = ai;
        _library = library;
        _display = display;

        _enhancer = new PromptEnhancer(ai);
        _swarm = new SwarmClient(ai);

        _dwell = new DispatcherTimer();
        _dwell.Tick += (_, _) => ShowNext();

        // A panel that comes back should not wait out the rest of a dwell with
        // nothing on it.
        _display.Reconnected += (_, _) => { if (_running && _current is not null) Replay(); };
    }

    /// <summary>Raised when an image is added to the library, so the UI can show it.</summary>
    public event EventHandler<MediaItem>? ItemGenerated;

    /// <summary>Raised when an item is pruned, so the UI can drop it from the grid.</summary>
    public event EventHandler<MediaItem>? ItemPruned;

    // -----------------------------------------------------------------------
    // Bindable state
    // -----------------------------------------------------------------------

    public bool Running
    {
        get => _running;
        private set => Set(ref _running, value);
    }

    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    /// <summary>The most recent failure, kept visible until something succeeds.</summary>
    public string LastError
    {
        get => _lastError;
        private set
        {
            if (Set(ref _lastError, value)) Raise(nameof(HasError));
        }
    }

    public bool HasError => !string.IsNullOrEmpty(_lastError);

    public int Generated
    {
        get => _generated;
        private set => Set(ref _generated, value);
    }

    /// <summary>
    /// Failed attempts this run. Worth showing: the pipeline retries quietly by
    /// design, so a server that is failing half the time would otherwise look
    /// only like a slow one.
    /// </summary>
    public int Failures
    {
        get => _failures;
        private set => Set(ref _failures, value);
    }

    public int QueueDepth => _ready.Count;

    public MediaItem? Current
    {
        get => _current;
        private set => Set(ref _current, value);
    }

    /// <summary>
    /// The last thing the language model produced, kept whether or not the
    /// image that followed ever arrived.
    ///
    /// Recorded before generation starts on purpose: a SwarmUI failure is
    /// exactly the moment you want to read the prompt that provoked it, and
    /// the item it would otherwise have been attached to never gets made.
    /// </summary>
    public EnhancedPrompt? LastEnhanced
    {
        get => _lastEnhanced;
        private set => Set(ref _lastEnhanced, value);
    }

    /// <summary>
    /// Records a prompt produced outside the pipeline, which means the AI
    /// window's test button: it goes straight at the client, and its answer
    /// is still the model's most recent output.
    /// </summary>
    public void NoteEnhanced(EnhancedPrompt prompt) => LastEnhanced = prompt;

    /// <summary>
    /// One line saying what the pipeline is doing, rendered by both the main
    /// window and the tray menu.
    ///
    /// Computed here rather than formatted at each call site: two descriptions
    /// of the same state, written separately, drift.
    /// </summary>
    public string Summary
    {
        get
        {
            if (!Running)
            {
                if (Generated == 0 && Failures == 0) return "";
                return $"AI stopped — {Generated} generated" + (Failures > 0 ? $", {Failures} failed" : "");
            }

            // What it is doing right now takes precedence: during a generation
            // that is the only part that changes, and it is what you look at.
            string what = Status.Length > 0
                ? char.ToUpperInvariant(Status[0]) + Status[1..]
                : Current is not null
                    ? "Showing " + Current.Name
                    : "Starting…";

            var line = new System.Text.StringBuilder("AI — ").Append(what);

            line.Append(" · ").Append(QueueDepth).Append(" ready");
            if (Generated > 0) line.Append(" · ").Append(Generated).Append(" made");
            if (Failures > 0) line.Append(" · ").Append(Failures).Append(" failed");

            return line.ToString();
        }
    }

    /// <summary>The prompt behind whatever is on the panel, when there is one.</summary>
    public string? CurrentPrompt =>
        Current is { IsGenerated: true } item ? item.EnhancedPrompt : null;

    /// <summary>Whether there is anything worth showing a status line for.</summary>
    public bool HasActivity => Running || Generated > 0 || Failures > 0;

    // -----------------------------------------------------------------------
    // Lifecycle
    // -----------------------------------------------------------------------

    public void Start()
    {
        if (Running) return;

        if (!_ai.Prompts.Any(p => p.Enabled && !string.IsNullOrWhiteSpace(p.Text)))
        {
            LastError = "there are no prompts to generate from";
            return;
        }

        Running = true;
        LastError = "";
        Failures = 0;

        _cancel = new CancellationTokenSource();
        _producer = Task.Run(() => ProduceAsync(_cancel.Token));

        // Always kick the consumer, even with nothing to show yet: ShowNext
        // schedules either a dwell or a short retry, so this one call is what
        // keeps it running for the rest of the session. Starting it only when
        // the queue was already stocked left the display loop dead whenever the
        // pipeline started cold.
        ShowNext();

        Storage.Log("ai: pipeline started");
    }

    public void Stop()
    {
        if (!Running) return;

        Running = false;
        _dwell.Stop();
        _waitingForImage = false;
        Current = null;
        Status = "";

        // The producer is only asked to stop; it is not waited on. Cancelling a
        // generation mid-flight is fine — the request is abandoned and the
        // partial file, if any, never gets written.
        _cancel?.Cancel();
        _cancel = null;
        _producer = null;

        Storage.Log("ai: pipeline stopped");
    }

    /// <summary>
    /// Generates one image now, outside the buffer, and puts it straight on the
    /// panel. The "Generate now" button, and the only path that works while the
    /// pipeline is stopped.
    /// </summary>
    public async Task<MediaItem?> GenerateOnceAsync(CancellationToken ct)
    {
        var item = await GenerateAsync(ct).ConfigureAwait(true);
        if (item is null) return null;

        if (!Running)
        {
            Current = item;
            _display.Play(item);
        }
        else
        {
            _ready.Enqueue(item);
            Raise(nameof(QueueDepth));

            if (_waitingForImage) ShowNext();
        }

        return item;
    }

    // -----------------------------------------------------------------------
    // Producer
    // -----------------------------------------------------------------------

    private async Task ProduceAsync(CancellationToken ct)
    {
        var backoff = FirstRetry;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_ready.Count >= _ai.BufferSize)
                {
                    // Full. Wait to be woken by a frame leaving the queue, with
                    // a ceiling so a settings change is picked up regardless.
                    await _wake.WaitAsync(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
                    continue;
                }

                if (!await WaitOutRateFloorAsync(ct).ConfigureAwait(false)) continue;

                _lastGenerationStarted = DateTime.UtcNow;
                var item = await GenerateAsync(ct).ConfigureAwait(false);

                if (item is null)
                {
                    await Task.Delay(backoff, ct).ConfigureAwait(false);
                    backoff = Next(backoff);
                    continue;
                }

                backoff = FirstRetry;
                _ready.Enqueue(item);

                await OnUiAsync(() =>
                {
                    Raise(nameof(QueueDepth));

                    // Show it at once if the panel is sitting waiting, rather
                    // than letting the retry tick find it up to five seconds
                    // later. Keyed on the consumer's own state: Current is not
                    // a safe proxy, because Generate now sets it while the
                    // pipeline is stopped.
                    if (Running && _waitingForImage) ShowNext();
                }).ConfigureAwait(false);

            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                // The loop itself must not die: it is the only thing keeping
                // the panel fed, and there is no supervisor above it.
                Storage.Log("ai: producer error: " + ex);
                await OnUiAsync(() => LastError = ex.Message).ConfigureAwait(false);

                try
                {
                    await Task.Delay(backoff, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                backoff = Next(backoff);
            }
        }
    }

    /// <summary>
    /// Holds off until the rate floor has elapsed since the last generation
    /// *started*.
    ///
    /// Start to start, not after each success: measuring from the end would add
    /// however long the backend took on top of the interval that was asked for,
    /// and a two-minute render under a five-minute floor would mean seven.
    ///
    /// The wait is counted down out loud. A pipeline that is deliberately idle
    /// looks identical to a broken one unless it says which it is.
    /// </summary>
    /// <returns>False if the wait was interrupted and the loop should restart.</returns>
    private async Task<bool> WaitOutRateFloorAsync(CancellationToken ct)
    {
        if (_ai.GenerateEverySeconds <= 0) return true;
        if (_lastGenerationStarted == DateTime.MinValue) return true;

        var floor = TimeSpan.FromSeconds(_ai.GenerateEverySeconds);
        var remaining = floor - (DateTime.UtcNow - _lastGenerationStarted);

        while (remaining > TimeSpan.Zero)
        {
            if (ct.IsCancellationRequested) return false;

            var slice = remaining < OneSecond ? remaining : OneSecond;
            await OnUiAsync(() => Status = $"next image in {Format(remaining)}").ConfigureAwait(false);
            await Task.Delay(slice, ct).ConfigureAwait(false);

            // Re-derived rather than decremented, so an edit to the interval
            // while waiting takes effect instead of running the old one out.
            remaining = TimeSpan.FromSeconds(_ai.GenerateEverySeconds)
                      - (DateTime.UtcNow - _lastGenerationStarted);
        }

        await OnUiAsync(() => Status = "").ConfigureAwait(false);
        return true;
    }

    private static string Format(TimeSpan span) =>
        span.TotalMinutes >= 1
            ? $"{(int)span.TotalMinutes}m {span.Seconds:00}s"
            : $"{Math.Max(1, (int)Math.Ceiling(span.TotalSeconds))}s";

    private static TimeSpan Next(TimeSpan current)
    {
        var doubled = TimeSpan.FromTicks(current.Ticks * 2);
        return doubled > MaxRetry ? MaxRetry : doubled;
    }

    /// <summary>
    /// One seed through the whole pipeline: pick, enhance, generate, write,
    /// thumbnail, add to the library, prune. Null on failure, with the reason
    /// left in <see cref="LastError"/>.
    /// </summary>
    private async Task<MediaItem?> GenerateAsync(CancellationToken ct)
    {
        string? seed = _enhancer.NextSeed();
        if (seed is null)
        {
            await OnUiAsync(() => LastError = "there are no prompts to generate from")
                .ConfigureAwait(false);
            return null;
        }

        try
        {
            await OnUiAsync(() => Status = "enhancing the prompt…").ConfigureAwait(false);
            var prompt = await _enhancer.EnhanceAsync(seed, ct).ConfigureAwait(false);

            await OnUiAsync(() =>
            {
                LastEnhanced = prompt;
                Status = "generating…";
            }).ConfigureAwait(false);
            var image = await _swarm.GenerateAsync(prompt.Text, ct).ConfigureAwait(false);

            string path = await WriteAsync(image, ct).ConfigureAwait(false);

            var item = new MediaItem
            {
                Path = path,
                Name = NameFor(prompt.Seed),
                IsVideo = false,
                IsGenerated = true,
                SeedPrompt = prompt.Seed,
                EnhancedPrompt = prompt.Text,
                GenModel = image.Model,
                GenSeed = image.Seed,
                GeneratedAt = DateTime.Now,
                DwellSeconds = _ai.DwellSeconds,
            };

            await ThumbnailService.EnsureThumbnailAsync(item).ConfigureAwait(true);

            await OnUiAsync(() =>
            {
                _library.Items.Add(item);
                ItemGenerated?.Invoke(this, item);

                Generated++;
                LastError = "";
                Status = Running ? "" : "ready";

                Prune();
            }).ConfigureAwait(false);

            Storage.Log($"ai: generated {Path.GetFileName(path)} from \"{prompt.Seed}\"");
            return item;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Storage.Log($"ai: generation failed ({ex.Message})");
            await OnUiAsync(() =>
            {
                Failures++;
                LastError = ex.Message;
                Status = "";
            }).ConfigureAwait(false);
            return null;
        }
    }

    private static async Task<string> WriteAsync(SwarmImage image, CancellationToken ct)
    {
        Storage.EnsureDirectories();

        // Timestamped to the millisecond and suffixed if that still collides,
        // so two images finishing together cannot overwrite one another.
        string stem = DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss-fff");
        string path = Path.Combine(Storage.GeneratedDirectory, stem + image.Extension);

        for (int i = 2; File.Exists(path); i++)
            path = Path.Combine(Storage.GeneratedDirectory, $"{stem} ({i}){image.Extension}");

        await File.WriteAllBytesAsync(path, image.Bytes, ct).ConfigureAwait(false);
        return path;
    }

    /// <summary>A short library caption taken from the seed, not the enhanced prompt.</summary>
    private static string NameFor(string seed)
    {
        string flat = seed.Trim();
        foreach (char bad in Path.GetInvalidFileNameChars()) flat = flat.Replace(bad, ' ');
        flat = flat.Trim();

        if (flat.Length == 0) return "Generated";
        return flat.Length <= 40 ? flat : flat[..39].TrimEnd() + "…";
    }

    // -----------------------------------------------------------------------
    // Consumer
    // -----------------------------------------------------------------------

    /// <summary>
    /// Moves the panel on to the next ready image. When the queue is empty the
    /// current image simply stays up and this retries shortly — a held frame
    /// beats a black panel.
    /// </summary>
    private void ShowNext()
    {
        _dwell.Stop();
        if (!Running) return;

        if (!_ready.TryDequeue(out var item))
        {
            // Nothing ready. Hold whatever is on the glass and keep ticking:
            // a held frame beats a black panel, and the producer will nudge us
            // the moment it has something.
            _waitingForImage = true;
            if (Status.Length == 0) Status = "waiting for the next image…";
            Schedule(TimeSpan.FromSeconds(5));
            return;
        }

        _waitingForImage = false;
        Raise(nameof(QueueDepth));
        _wake.Release();

        // Pruned or deleted from under us while it sat in the queue.
        if (!item.FileExists)
        {
            ShowNext();
            return;
        }

        Current = item;
        Status = "";

        if (!_display.Play(item))
        {
            // The device is away. The reconnect handler will put this back up.
            Schedule(TimeSpan.FromSeconds(5));
            return;
        }

        Schedule(TimeSpan.FromSeconds(_ai.DwellSeconds));
    }

    /// <summary>Puts the current image back after a reconnect.</summary>
    private void Replay()
    {
        if (Current is { FileExists: true } item) _display.Play(item);
    }

    private void Schedule(TimeSpan after)
    {
        _dwell.Interval = after;
        _dwell.Start();
    }

    // -----------------------------------------------------------------------
    // Retention
    // -----------------------------------------------------------------------

    /// <summary>
    /// Drops the oldest generated images past the cap. Pinned items never go,
    /// and neither does whatever is on the panel or still queued — pruning a
    /// file out from under the display would be a visible bug.
    /// </summary>
    public void Prune()
    {
        try
        {
            var candidates = _library.Items
                .Where(i => i.IsGenerated && !i.Pinned)
                .Where(i => !ReferenceEquals(i, Current) && !_ready.Contains(i))
                .OrderBy(i => i.GeneratedAt ?? DateTime.MinValue)
                .ToList();

            var doomed = new List<MediaItem>();

            if (_ai.PruneByAge)
            {
                var cutoff = DateTime.Now.AddDays(-_ai.RetentionDays);
                doomed.AddRange(candidates.Where(i => (i.GeneratedAt ?? DateTime.Now) < cutoff));
            }

            // The count cap applies to every generated item, pinned included,
            // but only unpinned ones can be taken to satisfy it.
            int total = _library.Items.Count(i => i.IsGenerated);
            int excess = total - doomed.Count - _ai.RetentionCount;

            if (excess > 0)
            {
                doomed.AddRange(candidates.Except(doomed).Take(excess));
            }

            foreach (var item in doomed) Remove(item);

            if (doomed.Count > 0) Storage.Log($"ai: pruned {doomed.Count} generated image(s)");
        }
        catch (Exception ex)
        {
            // Retention is housekeeping. Failing it must not stop generation.
            Storage.Log($"ai: prune failed ({ex.Message})");
        }
    }

    private void Remove(MediaItem item)
    {
        _library.Items.Remove(item);
        _library.Playlist.RemoveAll(id => id == item.Id);

        try
        {
            if (File.Exists(item.Path)) File.Delete(item.Path);
        }
        catch (Exception ex)
        {
            // Locked by a viewer, most likely. The library entry is gone either
            // way, which is what the user asked for.
            Storage.Log($"ai: could not delete {item.Path} ({ex.Message})");
        }

        ItemPruned?.Invoke(this, item);
    }

    // -----------------------------------------------------------------------

    /// <summary>Drops cached clients so edited settings take effect at once.</summary>
    public void SettingsChanged()
    {
        _enhancer.Invalidate();
        _swarm.ResetSession();
    }

    public SwarmClient Swarm => _swarm;

    public PromptEnhancer Enhancer => _enhancer;

    /// <summary>
    /// Runs an action on the UI thread. The producer touches the library and
    /// the bindable properties, and both belong to the dispatcher.
    /// </summary>
    private static Task OnUiAsync(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        return dispatcher.InvokeAsync(action).Task;
    }

    public void Dispose()
    {
        Stop();
        _enhancer.Dispose();
        _swarm.Dispose();

        // _wake is deliberately not disposed. Stop() only asks the producer to
        // stop; it may still be inside WaitAsync for a moment, and disposing
        // the semaphore under it would throw on a background thread during
        // shutdown. A SemaphoreSlim whose AvailableWaitHandle was never touched
        // holds nothing that needs releasing.
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return false;
        field = value;
        Raise(name!);
        return true;
    }

    /// <summary>
    /// Publishes a property, and the derived ones with it. Summary and its
    /// companions are computed from almost everything here, so raising them
    /// alongside every change is both correct and cheaper than tracking which
    /// change affects which.
    /// </summary>
    private void Raise(string name)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        if (name is nameof(Summary) or nameof(CurrentPrompt) or nameof(HasActivity)) return;

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Summary)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentPrompt)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasActivity)));
    }
}
