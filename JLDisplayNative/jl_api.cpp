// jl_api.cpp — the single process-wide session behind jl_api.h.
//
// The shape here is dictated by two facts. The device is a blocking serial
// handle driven by a child ffmpeg process, so nothing it does can be made
// non-blocking; and a UI thread must never block. So every content request
// becomes: stop the current worker, start a new one, return. The caller learns
// what happened by polling.

#include "jl_api.h"

#include "jl_core.h"

#include <windows.h>

#include <atomic>
#include <string>
#include <vector>

namespace {

    // -----------------------------------------------------------------------

    struct Guard {
        CRITICAL_SECTION& cs;
        explicit Guard(CRITICAL_SECTION& c) : cs(c) { EnterCriticalSection(&cs); }
        ~Guard() { LeaveCriticalSection(&cs); }
        Guard(const Guard&) = delete;
        Guard& operator=(const Guard&) = delete;
    };

    enum class ItemKind { None, Image, Video };

    struct Session {
        jl::Device  device;

        // Lives for the process, like the device: the host pushes surfaces into
        // it from its render thread whether or not anything is playing, and each
        // worker binds its own WIC state to it while it runs.
        jl::Overlay overlay;

        // Two locks, never held together in the other order. `cmd` serialises
        // whole operations (stop-then-start); `state` guards only the fields the
        // poller reads, so the worker can publish progress without ever blocking
        // on an operation in flight.
        CRITICAL_SECTION cmd{};
        CRITICAL_SECTION state{};

        HANDLE            worker = nullptr;
        std::atomic<bool> abortItem{ false };
        std::atomic<bool> shuttingDown{ false };

        // A pending seek in seconds, or negative for none. Written by whichever
        // thread the host calls from and taken by the playback loop at its next
        // frame; an unconsumed request is simply replaced, so dragging a
        // timeline leaves only the position the user let go of.
        std::atomic<double> seekTo{ -1.0 };

        // Read once by each worker as it starts, so changing the mode never
        // disturbs an item already running. Atomic rather than under `cmd`
        // because the host sets it from its UI thread whenever the user picks a
        // different option.
        std::atomic<int32_t> preprocess{ JL_PRE_OFF };

        // Worker input. Written under `cmd` before the thread starts, read only
        // by that thread, so it needs no lock of its own.
        std::wstring   itemPath;
        jl::RenderOpts itemOpts;
        ItemKind       itemKind = ItemKind::None;

        // Guarded by `state`.
        //
        // `connected` is a mirror of device.IsOpen() rather than a call to it.
        // Asking the device directly would mean taking its lock while holding
        // `state`, and the core takes `state` (through the log sink) while
        // holding the device lock — the inversion is a real deadlock.
        bool     connected = false;
        int32_t  st = JL_STATE_DISCONNECTED;
        int64_t  framesSent = 0;
        int64_t  framesDropped = 0;
        double   fps = 0.0;
        double   positionSeconds = 0.0;
        double   durationSeconds = 0.0;
        int32_t  finishedCount = 0;
        int32_t  frameCount = 0;
        std::wstring message;
        std::wstring error;
        std::wstring port;
        std::vector<uint8_t> lastFrame;
        DWORD    lastFrameAt = 0;
        DWORD    itemStartedAt = 0;   // for the running fps average

        // Resolved once per open and reused: asking ffmpeg what it supports
        // costs a process launch, and the answer cannot change while we run.
        std::wstring hwaccelResolved;
        bool         hwaccelKnown = false;

        Session()
        {
            InitializeCriticalSection(&cmd);
            InitializeCriticalSection(&state);
        }
    };

    Session g;

    // -----------------------------------------------------------------------
    // Status helpers
    // -----------------------------------------------------------------------

    void SetState(int32_t s)
    {
        Guard lock(g.state);
        g.st = s;
    }

    void SetError(const std::wstring& text)
    {
        Guard lock(g.state);
        g.error = text;
        g.st = JL_STATE_ERROR;
    }

    // The device stopped answering — unplugged, or the port went away. The
    // handle is not closed here: the worker cannot take the command lock, and
    // the next jl_open tears everything down anyway. Reporting it is enough for
    // the host to start reconnecting.
    void SetDeviceLost(const std::wstring& text)
    {
        Guard lock(g.state);
        g.error = text;
        g.st = JL_STATE_ERROR;
        g.connected = false;
    }

    // The worker's log sink. Errors are sticky until the next item starts;
    // everything else is the transient "what is it doing right now" line.
    void WorkerLog(jl::LogLevel level, const wchar_t* text, void*)
    {
        Guard lock(g.state);
        if (level == jl::LogLevel::Error) g.error = text;
        else                              g.message = text;
    }

    void CopyTo(wchar_t* dst, size_t cch, const std::wstring& src)
    {
        if (!dst || cch == 0) return;
        size_t n = src.size() < cch - 1 ? src.size() : cch - 1;
        if (n) memcpy(dst, src.c_str(), n * sizeof(wchar_t));
        dst[n] = L'\0';
    }

    // -----------------------------------------------------------------------
    // Worker
    // -----------------------------------------------------------------------

    bool ShouldAbort(void*)
    {
        return g.abortItem.load() || g.shuttingDown.load();
    }

    // Called for every frame that reaches the panel. The counters are cheap and
    // have to be exact, so they update every time; copying the frame itself is
    // up to 80 KB and only a preview, so that part is throttled to 5 Hz.
    void OnFrame(const std::vector<uint8_t>& jpeg, void*)
    {
        const DWORD now = GetTickCount();

        Guard lock(g.state);
        ++g.framesSent;

        const double elapsed = (now - g.itemStartedAt) / 1000.0;
        if (elapsed > 0.0) g.fps = g.framesSent / elapsed;

        if (now - g.lastFrameAt >= 200) {
            g.lastFrameAt = now;
            g.lastFrame = jpeg;
            ++g.frameCount;
        }
    }

    // Consumes the pending seek, so one request moves playback exactly once.
    double TakeSeek(void*)
    {
        return g.seekTo.exchange(-1.0);
    }

    void OnPosition(double seconds, double duration, void*)
    {
        Guard lock(g.state);
        g.positionSeconds = seconds;
        g.durationSeconds = duration;
    }

    // The two hooks every playback loop takes. Kept as free functions rather
    // than lambdas so the Compositor stays a plain C-callable pair.
    bool ComposeFrame(std::vector<uint8_t>& jpeg, void*)
    {
        return g.overlay.Compose(jpeg);
    }

    uint32_t OverlayVersion(void*)
    {
        return g.overlay.Version();
    }

    // Always installed, never conditional on the overlay being on right now:
    // the user can enable it in the middle of a two-hour video and expect it to
    // appear. Costing nothing when off is Overlay::Compose's job — it is two
    // atomic loads and a return — not something to decide once per item.
    jl::Compositor MakeCompositor()
    {
        jl::Compositor c;
        c.compose = ComposeFrame;
        c.version = OverlayVersion;
        return c;
    }

    void PublishFrame(const std::vector<uint8_t>& jpeg)
    {
        Guard lock(g.state);
        g.lastFrameAt = GetTickCount();
        g.lastFrame = jpeg;
        ++g.frameCount;
    }

    // A still's overlay redraw. Deliberately not OnFrame: that one counts
    // frames sent and computes a running fps, and a still has never reported
    // either. All that has changed is which pixels the preview should show.
    void OnStillFrame(const std::vector<uint8_t>& jpeg, void*)
    {
        PublishFrame(jpeg);
    }

    jl::Preprocess CurrentMode()
    {
        switch (g.preprocess.load()) {
        case JL_PRE_MEMORY: return jl::Preprocess::Memory;
        case JL_PRE_DISK:   return jl::Preprocess::Disk;
        default:            return jl::Preprocess::Off;
        }
    }

    // The tail every video path shares, whether its frames came from a pack or
    // straight off ffmpeg's pipe.
    void FinishPlayback(bool ok, const jl::PlaybackStats& stats)
    {
        {
            // framesSent already counted live in OnFrame; taking it from stats
            // too would be the same number, but dropped frames never reach
            // OnFrame so they can only come from here.
            Guard lock(g.state);
            g.framesDropped = (int64_t)stats.dropped;
            if (stats.fps > 0.0) g.fps = stats.fps;
        }

        if (ShouldAbort(nullptr)) return;

        if (!ok) {
            SetDeviceLost(L"playback stopped: the device or ffmpeg failed");
            return;
        }

        // Reached the end on its own. A looping item only gets here if it was
        // stopped, which the abort check above already returned for.
        Guard lock(g.state);
        ++g.finishedCount;
        g.st = JL_STATE_IDLE;
    }

    DWORD WINAPI WorkerProc(LPVOID)
    {
        jl::SetLogSink(WorkerLog, nullptr);   // per-thread; see jl_core.h

        // WIC objects and the COM apartment they live in belong to this thread.
        // Binding costs nothing when no overlay is ever enabled, and unbinding
        // on every exit path is what keeps the apartment from outliving it.
        g.overlay.BindThread();
        struct Unbind {
            ~Unbind() { g.overlay.UnbindThread(); }
        } unbind;

        const jl::Compositor comp = MakeCompositor();

        const std::wstring   path = g.itemPath;
        const ItemKind       kind = g.itemKind;
        jl::RenderOpts       opts = g.itemOpts;
        const jl::Preprocess mode = CurrentMode();

        if (kind == ItemKind::Image) {
            SetState(JL_STATE_PREPARING);

            std::vector<uint8_t> jpeg;
            if (!jl::PrepareImageCached(path, opts, mode, jpeg)) {
                if (!ShouldAbort(nullptr)) SetState(JL_STATE_ERROR);
                return 0;
            }
            if (ShouldAbort(nullptr)) return 0;

            SetState(JL_STATE_PLAYING);
            PublishFrame(jpeg);

            // A still has no natural end: hold it until something else is asked
            // for. finishedCount deliberately does not advance — dwell timing is
            // the host's playlist decision, not ours.
            if (!g.device.HoldStill(jpeg, ShouldAbort, nullptr, &comp,
                    OnStillFrame, nullptr) && !ShouldAbort(nullptr))
                SetDeviceLost(L"lost the device while holding a still");
            else if (!ShouldAbort(nullptr))
                SetState(JL_STATE_IDLE);

            return 0;
        }

        if (kind == ItemKind::Video) {
            // Resolve the quality separately from playing it, so the two states
            // are distinguishable: calibration can take a minute on a long file
            // and the UI has to be able to say so. Doing it here rather than
            // inside each path also means the pack builder and the streamer
            // agree on the answer by construction.
            SetState(JL_STATE_CALIBRATING);

            const int quality = jl::ResolveQuality(path, opts, ShouldAbort, nullptr);
            if (quality == -2 || ShouldAbort(nullptr)) return 0;   // cancelled
            if (quality < 0) {
                SetState(JL_STATE_ERROR);
                return 0;
            }
            opts.quality = quality;   // both paths now skip calibration entirely

            // The pack has to outlive playback: PlayPack reads straight out of
            // it, and for a disk pack that means straight out of the mapping.
            jl::FramePack pack;

            if (mode != jl::Preprocess::Off) {
                SetState(JL_STATE_PREPROCESSING);

                if (jl::GetPack(path, opts, mode, pack, ShouldAbort, nullptr, nullptr)) {
                    if (ShouldAbort(nullptr)) return 0;

                    SetState(JL_STATE_PLAYING);

                    jl::PlaybackStats stats;
                    bool ok = jl::PlayPack(g.device, pack, opts, ShouldAbort, nullptr,
                        OnFrame, nullptr, &stats, TakeSeek, nullptr, OnPosition, nullptr,
                        &comp);

                    FinishPlayback(ok, stats);
                    return 0;
                }

                if (ShouldAbort(nullptr)) return 0;

                // Could not be packed — over the memory budget, or the cache
                // would not take it. GetPack has already said why; streaming
                // still works, so fall through to it rather than failing.
            }

            SetState(JL_STATE_PLAYING);

            jl::PlaybackStats stats;
            bool ok = jl::PlayVideo(g.device, path, opts, ShouldAbort, nullptr,
                OnFrame, nullptr, &stats, TakeSeek, nullptr, OnPosition, nullptr,
                &comp);

            FinishPlayback(ok, stats);
            return 0;
        }

        return 0;
    }

    // Both must be called with `cmd` held.

    void StopWorkerLocked()
    {
        g.abortItem.store(true);
        if (g.worker) {
            WaitForSingleObject(g.worker, INFINITE);
            CloseHandle(g.worker);
            g.worker = nullptr;
        }
        g.abortItem.store(false);
    }

    int32_t StartWorkerLocked(const wchar_t* path, const JlRenderOpts* opts, ItemKind kind)
    {
        if (!path || !*path || !opts) return JL_ERR_BAD_ARGS;
        if (!g.device.IsOpen()) return JL_ERR_NOT_OPEN;

        jl::RenderOpts o;
        o.stretch = opts->stretch != 0;
        o.rotate = opts->rotate;
        o.fps = opts->fps;
        o.quality = opts->quality;
        o.loop = opts->loop != 0;
        o.recalibrate = opts->recalibrate != 0;

        wchar_t hw[32];
        CopyTo(hw, _countof(hw), opts->hwaccel);
        o.hwaccel = hw;
        if (o.hwaccel == L"none") o.hwaccel.clear();

        std::wstring problem;
        if (!jl::ValidateOpts(o, problem)) {
            SetError(problem);
            return JL_ERR_BAD_ARGS;
        }

        // Resolve "auto" once for the life of the session rather than launching
        // ffmpeg to ask the same question for every item.
        if (o.hwaccel == L"auto") {
            if (!g.hwaccelKnown) {
                g.hwaccelResolved = jl::ResolveHwaccel(jl::FindFfmpeg(), L"auto");
                g.hwaccelKnown = true;
            }
            o.hwaccel = g.hwaccelResolved;
        }

        StopWorkerLocked();

        {
            Guard lock(g.state);
            g.error.clear();
            g.message.clear();
            g.framesSent = 0;
            g.framesDropped = 0;
            g.fps = 0.0;
            g.positionSeconds = 0.0;
            g.durationSeconds = 0.0;
            g.itemStartedAt = GetTickCount();
        }

        // A seek aimed at the item being replaced must not land on the new one.
        g.seekTo.store(-1.0);

        g.itemPath = path;
        g.itemOpts = o;
        g.itemKind = kind;

        g.worker = CreateThread(nullptr, 0, WorkerProc, nullptr, 0, nullptr);
        if (!g.worker) {
            SetError(L"could not start the playback thread");
            return JL_ERR_FAILED;
        }
        return JL_OK;
    }

}  // namespace

// ---------------------------------------------------------------------------
// Exports
// ---------------------------------------------------------------------------

int32_t jl_find_port(wchar_t* buf, int32_t cch)
{
    std::wstring port = jl::FindPort();
    CopyTo(buf, (size_t)cch, port);
    return port.empty() ? JL_ERR_FAILED : JL_OK;
}

int32_t jl_open(const wchar_t* port)
{
    Guard lock(g.cmd);

    StopWorkerLocked();
    g.device.Close();

    std::wstring problem;
    if (!g.device.Open(port ? port : L"", problem)) {
        Guard s(g.state);
        g.error = problem;
        g.st = JL_STATE_DISCONNECTED;
        g.connected = false;
        g.port.clear();
        return JL_ERR_FAILED;
    }

    g.device.Clear();

    {
        Guard s(g.state);
        g.error.clear();
        g.message.clear();
        g.port = g.device.Port();
        g.connected = true;
        g.st = JL_STATE_IDLE;
    }
    return JL_OK;
}

void jl_close(void)
{
    Guard lock(g.cmd);

    StopWorkerLocked();
    if (g.device.IsOpen()) {
        g.device.FlushEoi();
        g.device.Close();
    }

    Guard s(g.state);
    g.st = JL_STATE_DISCONNECTED;
    g.connected = false;
    g.port.clear();
    g.lastFrame.clear();
}

int32_t jl_get_info(wchar_t* buf, int32_t cch)
{
    Guard lock(g.cmd);
    if (!g.device.IsOpen()) return JL_ERR_NOT_OPEN;

    std::string body;
    if (!g.device.GetInfo(body)) return JL_ERR_FAILED;

    // The panel answers in ASCII JSON, so a straight widening is exact.
    std::wstring wide(body.begin(), body.end());
    CopyTo(buf, (size_t)cch, wide);
    return JL_OK;
}

int32_t jl_set_brightness(int32_t percent)
{
    // Deliberately not under `cmd`: brightness must work while an item is
    // playing. Device methods are serialised internally, so the command lands
    // between two frames rather than inside one.
    if (!g.device.IsOpen()) return JL_ERR_NOT_OPEN;
    return g.device.SetBrightness(percent) ? JL_OK : JL_ERR_FAILED;
}

int32_t jl_show_image(const wchar_t* path, const JlRenderOpts* opts)
{
    Guard lock(g.cmd);
    return StartWorkerLocked(path, opts, ItemKind::Image);
}

int32_t jl_play_video(const wchar_t* path, const JlRenderOpts* opts)
{
    Guard lock(g.cmd);
    return StartWorkerLocked(path, opts, ItemKind::Video);
}

int32_t jl_calibrate(const wchar_t* path, const JlRenderOpts* opts)
{
    if (!path || !*path || !opts) return JL_ERR_BAD_ARGS;

    // Touches no device state and takes no session lock, so a host can pre-warm
    // several videos on background threads while something else is playing.
    // Blocking by design: the caller already has a thread to spare.
    jl::RenderOpts o;
    o.stretch = opts->stretch != 0;
    o.rotate = opts->rotate;
    o.fps = opts->fps;
    o.quality = 0;                     // calibrating an explicit quality is a no-op
    o.recalibrate = opts->recalibrate != 0;

    wchar_t hw[32];
    CopyTo(hw, _countof(hw), opts->hwaccel);
    o.hwaccel = hw;
    if (o.hwaccel == L"none") o.hwaccel.clear();

    std::wstring problem;
    if (!jl::ValidateOpts(o, problem)) return JL_ERR_BAD_ARGS;

    // Only shutdown can cancel this one; it has no item to be replaced by.
    auto abortOnShutdown = [](void*) -> bool { return g.shuttingDown.load(); };

    int quality = jl::ResolveQuality(path, o, abortOnShutdown, nullptr);
    return quality > 0 ? quality : JL_ERR_FAILED;
}

void jl_stop(void)
{
    Guard lock(g.cmd);
    StopWorkerLocked();

    if (g.device.IsOpen()) g.device.FlushEoi();

    g.seekTo.store(-1.0);

    Guard s(g.state);
    if (g.st != JL_STATE_DISCONNECTED) g.st = JL_STATE_IDLE;
    g.message.clear();
    g.positionSeconds = 0.0;
    g.durationSeconds = 0.0;
}

void jl_seek(double seconds)
{
    // No lock and no worker check: the request is a single atomic, and the
    // playback loop is the only thing that reads it. Taking g.cmd here would
    // block the UI thread behind whatever the worker is doing, which for a
    // dragged timeline is exactly the wrong trade.
    if (seconds < 0.0) seconds = 0.0;
    g.seekTo.store(seconds);
}

void jl_get_status(JlStatus* out)
{
    if (!out) return;

    Guard lock(g.state);
    out->state = g.st;
    out->connected = g.connected ? 1 : 0;
    out->framesSent = g.framesSent;
    out->framesDropped = g.framesDropped;
    out->fps = g.fps;
    out->positionSeconds = g.positionSeconds;
    out->durationSeconds = g.durationSeconds;
    out->finishedCount = g.finishedCount;
    out->frameCount = g.frameCount;
    CopyTo(out->port, _countof(out->port), g.port);
    CopyTo(out->message, _countof(out->message), g.message);
    CopyTo(out->error, _countof(out->error), g.error);

    // Read straight off the overlay rather than mirrored into the session:
    // these are its own atomics and plain arithmetic, and nothing here needs
    // them to agree with the fields above to the exact frame.
    const jl::Overlay::Stats ov = g.overlay.Snapshot();
    out->overlayComposeMs = ov.composeMs;
    out->overlayEncodeMs = ov.encodeMs;
    out->overlayQuality = (int32_t)(ov.quality * 100.0f + 0.5f);
    out->overlayDrops = (int32_t)ov.drops;
}

int32_t jl_get_last_frame(uint8_t* buf, int32_t cap)
{
    Guard lock(g.state);
    const int32_t need = (int32_t)g.lastFrame.size();
    if (need == 0) return 0;
    if (!buf || cap < need) return need;   // asking for the size, or too small
    memcpy(buf, g.lastFrame.data(), (size_t)need);
    return need;
}

void jl_set_preprocess(int32_t mode)
{
    // Anything unrecognised means Off. A host built against a later header must
    // not be able to turn this into an unplayable state.
    if (mode != JL_PRE_MEMORY && mode != JL_PRE_DISK) mode = JL_PRE_OFF;
    g.preprocess.store(mode);
}

int32_t jl_get_preprocess(void)
{
    return g.preprocess.load();
}

void jl_set_pack_budgets(int64_t memoryBytes, int64_t diskBytes)
{
    // A negative figure is nonsense rather than "unlimited"; treat it as 0,
    // which the core reads as "put the default back".
    jl::SetMemoryBudget(memoryBytes > 0 ? (uint64_t)memoryBytes : 0);
    jl::SetDiskBudget(diskBytes > 0 ? (uint64_t)diskBytes : 0);
}

int64_t jl_memory_budget(void)
{
    return (int64_t)jl::MemoryBudget();
}

int64_t jl_disk_budget(void)
{
    return (int64_t)jl::DiskBudget();
}

int64_t jl_pack_cache_bytes(void)
{
    return (int64_t)jl::PackCacheBytes();
}

void jl_pack_cache_clear(void)
{
    jl::PackCacheClear();
}

// ---------------------------------------------------------------------------
// Overlay
//
// Deliberately independent of the session's command lock. The host renders on
// its own thread and pushes surfaces whether or not anything is playing, and
// making that wait on a stop-then-start would stall the render thread behind
// whatever ffmpeg happens to be doing.
// ---------------------------------------------------------------------------

void jl_overlay_set_enabled(int32_t on)
{
    g.overlay.SetEnabled(on != 0);
}

int32_t jl_overlay_update(const uint8_t* bgraPremultiplied, int32_t w, int32_t h)
{
    if (!bgraPremultiplied) return JL_ERR_BAD_ARGS;
    return g.overlay.Update(bgraPremultiplied, w, h) ? JL_OK : JL_ERR_BAD_ARGS;
}

void jl_overlay_clear(void)
{
    g.overlay.Clear();
}

int32_t jl_ffmpeg_path(wchar_t* buf, int32_t cch)
{
    std::wstring path = jl::FindFfmpeg();
    CopyTo(buf, (size_t)cch, path);
    return path.empty() ? JL_ERR_FAILED : JL_OK;
}

// ---------------------------------------------------------------------------

BOOL APIENTRY DllMain(HMODULE, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_DETACH) {
        // Signal only. Joining a thread from under the loader lock deadlocks, so
        // a well-behaved host calls jl_close before unloading; this just makes
        // sure a worker that is still running stops touching the device.
        g.shuttingDown.store(true);
        g.abortItem.store(true);
    }
    return TRUE;
}
