// jl_core.h — public interface of the Jungle Leopard display core.
//
// Everything the CLI and the tray app share lives behind this header: device
// discovery, the wire protocol, ffmpeg transcoding, and the video calibration
// cache. Nothing in here writes to stdout; diagnostics go to a log sink the
// caller installs, so the same code serves a console tool and a GUI.
//
// Protocol recovered from the vendor Electron app (resources/app.asar,
// main/_baseClass/device.js). Verified against firmware version 3.1,
// model TXW818-ST7701S-5.5inch-hor, 960x480.

#pragma once

#include <windows.h>

#include <cstdint>
#include <string>
#include <vector>

namespace jl {

    // -----------------------------------------------------------------------
    // Protocol constants
    //
    // The full opcode table, not just the three this code sends. Each comment
    // is the condition the vendor app checks before it will send that command,
    // read out of main/_baseClass/device.js and evaluated against this panel
    // (TXW818-ST7701S-5.5inch-hor, firmware 3.1). A command marked "not ours"
    // is one the vendor app would refuse to send here at all.
    //
    // OtaBegin and SetSerialNum are recorded and deliberately not implemented.
    // One flashes firmware and the other rewrites the panel identity and
    // reboots, and neither is gated away from this hardware.
    // -----------------------------------------------------------------------

    namespace cmd {
        constexpr uint8_t Restart = 0x01;
        constexpr uint8_t SetLight = 0x03;
        constexpr uint8_t GetDeviceInfo = 0x06;
        constexpr uint8_t OtaBegin = 0x0C;  // F2 FF + u32 LE size, then a raw .bin
        constexpr uint8_t Live = 0x11;  // start / hold live mode
        constexpr uint8_t SetMotionBeforeOff = 0x14;
        constexpr uint8_t SetMotionTimeout = 0x15;  // version >= 2.8
        constexpr uint8_t SetRegion = 0x20;  // UTF-8 string; this is what arms SetMotor
        constexpr uint8_t Close = 0x21;  // version >= 3.1, so ours
        constexpr uint8_t SetSerialNum = 0x23;  // rewrites the serial, then reboots
        constexpr uint8_t SetMotor = 0x25;  // region == "ycc28_v1", not ours
        constexpr uint8_t SetRealTimeTimeout = 0x26;  // version >= 4.1, not ours
    }

    constexpr DWORD  kLiveKeepAliveMs = 1500;        // vendor app's interval
    constexpr DWORD  kStillRefreshMs = 250;         // re-send rate for a held still
    constexpr size_t kMaxJpegBytes = 80 * 1024;   // maxSize for this panel
    constexpr size_t kSafeJpegBytes = 64 * 1024;   // calibration target, leaves headroom
    constexpr int    kPanelWidth = 960;
    constexpr int    kPanelHeight = 480;

    // -----------------------------------------------------------------------
    // Logging
    //
    // Progress messages are transient: a console sink rewrites them in place on
    // one line, a GUI sink drops them into a status label. A sink that receives
    // a non-Progress message after a Progress one is responsible for clearing
    // whatever the progress line left behind.
    //
    // The sink is PER THREAD. A host that plays on one thread while calibrating
    // on another gets two clean streams instead of one interleaved one, and a
    // thread that installs no sink is silent. Every worker thread must install
    // its own.
    // -----------------------------------------------------------------------

    enum class LogLevel { Info, Warn, Error, Progress };

    using LogFn = void (*)(LogLevel level, const wchar_t* text, void* user);

    void SetLogSink(LogFn fn, void* user);
    void Log(LogLevel level, const wchar_t* fmt, ...);

    // -----------------------------------------------------------------------
    // Render options
    //
    // These describe how a source file becomes panel-ready JPEG. Two option
    // sets that differ anywhere here produce different pixels, so the whole
    // struct feeds the calibration cache key.
    // -----------------------------------------------------------------------

    struct RenderOpts {
        bool         stretch = false;   // fill the panel, ignore aspect ratio
        int          rotate = 0;       // 0, 90, 180 or 270, applied before scaling
        int          fps = 30;      // liveRate for this panel
        int          quality = 0;       // 0 = calibrate automatically, else 2..31
        bool         loop = false;
        bool         recalibrate = false;   // ignore any cached calibration
        std::wstring hwaccel;               // empty = software, "auto" = resolve
    };

    // Returns false and writes a reason into `error` if the combination cannot
    // be rendered (bad rotation, fps or quality out of range).
    bool ValidateOpts(const RenderOpts& opts, std::wstring& error);

    // -----------------------------------------------------------------------
    // Cancellation
    //
    // Every long-running call in this library polls one of these. Nothing here
    // is interruptible by any other means: ffmpeg is a child process and the
    // port is a blocking handle, so a host that must stay responsive has to
    // pass an abort callback and run the call off its UI thread.
    // -----------------------------------------------------------------------

    // Return true to stop work at the next opportunity — checked per frame.
    using AbortFn = bool (*)(void* user);

    // -----------------------------------------------------------------------
    // Seeking
    //
    // A seek is a request the player collects rather than a call into it: both
    // play loops are blocking, and the only safe moment to move is between two
    // frames. The host raises one from whatever thread it likes; the loop takes
    // it at the next frame boundary.
    // -----------------------------------------------------------------------

    // Returns the position to jump to in seconds, or a negative number when no
    // seek is pending. Taking one consumes it, so a request fires exactly once.
    using SeekFn = double (*)(void* user);

    // Where playback has reached, in seconds, and how long the item runs for.
    // A duration of zero means it could not be determined — ffprobe is
    // optional, and a stream need not know its own length.
    using PositionFn = void (*)(double seconds, double duration, void* user);

    // -----------------------------------------------------------------------
    // Compositing
    //
    // The panel has no overlay plane, no alpha and no text primitive: it takes
    // whole JPEGs and nothing else. So drawing anything on top of the video
    // means decoding each outgoing frame, blending, and re-encoding it — which
    // is what a Compositor does, once per frame, on the playback thread.
    //
    // Every playback loop takes one optionally. A null Compositor, or one whose
    // overlay is disabled, is the path taken whenever nobody is drawing
    // anything, and it must cost nothing: no decode, no encode, no allocation.
    // -----------------------------------------------------------------------

    // Observes each frame as it goes out, for a live preview. Called on the
    // playback thread; keep it cheap and copy anything you keep. A frame
    // reported here is the one that actually reached the panel, overlay and
    // all, so a preview built from it shows what is really on the glass.
    using FrameFn = void (*)(const std::vector<uint8_t>& jpeg, void* user);

    // Composites the current overlay onto a panel-ready frame, in place, and
    // re-encodes it under the panel's size cap. False means the frame could not
    // be produced at any acceptable quality; the caller drops it exactly as it
    // drops an oversized one.
    using ComposeFn = bool (*)(std::vector<uint8_t>& jpeg, void* user);

    // What the overlay currently looks like, as a counter that changes whenever
    // it does. A still holds one composited frame until this moves, so a static
    // overlay on a still costs nothing after the first frame — without it, a
    // still would re-encode four times a second forever.
    using OverlayVersionFn = uint32_t (*)(void* user);

    struct Compositor {
        ComposeFn        compose = nullptr;
        OverlayVersionFn version = nullptr;
        void*            user = nullptr;

        bool Active() const { return compose != nullptr; }

        bool Apply(std::vector<uint8_t>& jpeg) const
        {
            return compose ? compose(jpeg, user) : true;
        }

        uint32_t Version() const { return version ? version(user) : 0; }
    };

    // -----------------------------------------------------------------------
    // Device
    // -----------------------------------------------------------------------

    // Walks the ports class for the panel's hardware ID. Empty if not attached.
    std::wstring FindPort();

    // A held-open serial session. Only one process on the machine can own the
    // port at a time, so this is deliberately non-copyable and explicit about
    // its lifetime — the tray app keeps exactly one alive for as long as it runs.
    //
    // Every method below is serialised against the others, at the granularity of
    // one whole message. That is the granularity that matters: a brightness
    // command arriving from the UI thread lands cleanly between two video
    // frames rather than halfway through a JPEG.
    class Device {
    public:
        Device();
        ~Device();

        Device(const Device&) = delete;
        Device& operator=(const Device&) = delete;

        // `port` empty means autodetect. On failure `error` explains why, with
        // the access-denied case called out — that one means another program
        // (the vendor app, or the CLI) is holding the device.
        bool Open(const std::wstring& port, std::wstring& error);
        void Close();

        bool IsOpen() const { return h_ != INVALID_HANDLE_VALUE; }
        const std::wstring& Port() const { return port_; }

        // deviceClear(): the raw sentinels the vendor app sends before talking.
        void Clear();

        bool SendCommand(uint8_t command, const std::vector<uint8_t>& payload = {});
        bool SendImageFrame(const std::vector<uint8_t>& jpeg);
        bool ReadReply(std::string& body, DWORD timeoutMs = 3000);

        // Live mode lapses unless this is re-sent about every kLiveKeepAliveMs.
        bool KeepAlive() { return SendCommand(cmd::Live); }

        // Holds a still until `abort` says stop, by re-sending it at
        // kStillRefreshMs. The panel is a live-stream device and only draws a
        // frame once the following one arrives, so a still has to be sent
        // repeatedly rather than once. Blocking; this is what "showing a
        // picture" actually means on this panel.
        //
        // With a Compositor, the overlay is drawn onto `jpeg` and the result
        // re-sent instead — recomposited only when the overlay's version moves,
        // so the four-a-second refresh does not become four encodes a second.
        //
        // `onFrame` fires whenever that composited frame is rebuilt, not on
        // every refresh: a still sends the same bytes over and over, and only
        // the moments they change are worth telling anyone about.
        bool HoldStill(const std::vector<uint8_t>& jpeg, AbortFn abort, void* abortUser,
            const Compositor* comp = nullptr,
            FrameFn onFrame = nullptr, void* frameUser = nullptr);

        // Flushes any partial frame with a bare JPEG end-of-image marker.
        void FlushEoi();

        bool SetBrightness(int percent);
        bool GetInfo(std::string& out);

    private:
        HANDLE       h_ = INVALID_HANDLE_VALUE;
        std::wstring port_;

        // Recursive, so GetInfo can hold it across a command and its reply and
        // still call through the public methods.
        mutable CRITICAL_SECTION cs_{};
    };

    // -----------------------------------------------------------------------
    // Media
    // -----------------------------------------------------------------------

    // Transcodes any image to a 960x480 JPEG under the panel's size cap. Skips
    // ffmpeg entirely when the input already conforms and no rotation is asked
    // for. Blocking; takes up to a few seconds on a large source.
    bool PrepareImage(const std::wstring& path, const RenderOpts& opts,
        std::vector<uint8_t>& jpeg);

    // Resolves the -q:v to use for a video: an explicit opts.quality, else the
    // cached calibration, else a fresh survey (which is then cached). Returns
    // -1 if no quality keeps frames under the cap, or -2 if aborted. Blocking,
    // and a fresh survey on a long video takes tens of seconds — it reports
    // through the Progress log level throughout, and honours `abort`, so a
    // caller that cannot block must pass one.
    int ResolveQuality(const std::wstring& path, const RenderOpts& opts,
        AbortFn abort = nullptr, void* abortUser = nullptr);


    struct PlaybackStats {
        size_t sent = 0;
        size_t dropped = 0;   // frames over the panel's cap, skipped
        size_t passes = 0;   // ffmpeg runs; more than one means it looped
        double fps = 0.0;
    };

    // Streams MJPEG out of ffmpeg and pushes each frame, pacing against the
    // wall clock and holding live mode. Blocks until the source ends, `abort`
    // returns true, or the port dies. Leaves the device in live mode with a
    // flush marker sent.
    //
    // A seek restarts ffmpeg with the new position as its input offset, which
    // is the only way to move within a stream that is being transcoded as it
    // plays. Position is counted from frames emitted since that offset, since
    // the frames arrive at a known rate by construction.
    bool PlayVideo(Device& device, const std::wstring& path, const RenderOpts& opts,
        AbortFn abort, void* abortUser,
        FrameFn onFrame, void* frameUser,
        PlaybackStats* stats,
        SeekFn takeSeek = nullptr, void* seekUser = nullptr,
        PositionFn onPosition = nullptr, void* positionUser = nullptr,
        const Compositor* comp = nullptr);

    // -----------------------------------------------------------------------
    // Preprocessing
    //
    // Everything this panel accepts is a stream of 960x480 baseline 4:2:0
    // JPEGs under kMaxJpegBytes, and what ffmpeg produces for a given source is
    // a pure function of that source's bytes and the render options — which is
    // exactly what the calibration cache already assumes. So the frames can be
    // computed once and replayed, and ffmpeg need not run during playback at
    // all.
    //
    // Off stays the fallback everywhere: a source too big for the memory
    // budget, or a pack that cannot be written, degrades to streaming rather
    // than failing.
    // -----------------------------------------------------------------------

    enum class Preprocess {
        Off = 0,      // transcode continuously while playing, as this always did
        Memory = 1,   // transcode once into RAM, then let ffmpeg exit
        Disk = 2      // transcode once into a pack file, reused across runs
    };

    // How much either mode is allowed to use. Both are settable at runtime —
    // what counts as reasonable depends entirely on the machine, and a limit
    // baked into the binary is a limit nobody can fix.
    //
    // Memory mode refuses a source that would exceed its budget, and says so,
    // which the caller turns back into streaming. Disk packs are instead
    // evicted least-recently-used to stay under theirs, so a full cache slows
    // the next play down rather than failing it.
    constexpr uint64_t kDefaultMemoryBudget = 512ull * 1024 * 1024;   // ~7 min of video
    constexpr uint64_t kDefaultDiskBudget = 8ull * 1024 * 1024 * 1024;

    // Below these, a budget is too small to hold anything useful and every item
    // would fall back to streaming — which is what Off is for. Anything lower is
    // raised to the floor rather than honoured.
    constexpr uint64_t kMinMemoryBudget = 32ull * 1024 * 1024;
    constexpr uint64_t kMinDiskBudget = 128ull * 1024 * 1024;

    // 0 restores the default. The memory budget applies to the next pack built;
    // the disk budget is enforced the next time one is written.
    void     SetMemoryBudget(uint64_t bytes);
    void     SetDiskBudget(uint64_t bytes);
    uint64_t MemoryBudget();
    uint64_t DiskBudget();

    struct PackStats {
        size_t frames = 0;          // frames in playback order
        size_t uniqueFrames = 0;    // distinct frames actually stored
        size_t bytes = 0;
        double buildSeconds = 0.0;  // 0 when it came straight from the cache
        bool   fromCache = false;
    };

    // A built sequence of panel-ready frames.
    //
    // A memory pack owns its bytes. A disk pack maps its file, so the pages stay
    // under the OS's control rather than being read wholesale into the process —
    // which is what makes a 350 MB pack of a five-minute video reasonable.
    //
    // Frame pointers stay valid until Close(), so the pack must outlive any
    // playback reading from it.
    class FramePack {
    public:
        struct Ref { uint64_t offset; uint32_t length; };

        FramePack() = default;
        ~FramePack();

        FramePack(const FramePack&) = delete;
        FramePack& operator=(const FramePack&) = delete;

        bool   IsOpen() const { return base_ != nullptr && !index_.empty(); }
        int    Fps() const { return fps_; }
        size_t FrameCount() const { return index_.size(); }
        size_t Bytes() const { return bytes_; }

        // The bytes of frame `i`, or nullptr past the end.
        const uint8_t* Frame(size_t i, size_t& len) const;

        void Close();

        // Filled in by GetPack. Public only because the builder lives in another
        // translation unit; callers have no reason to touch either.
        void AdoptMemory(std::vector<uint8_t>&& blob, std::vector<Ref>&& index, int fps);
        bool AdoptFile(const std::wstring& path);

    private:
        std::vector<uint8_t> owned_;                 // memory mode
        HANDLE               file_ = INVALID_HANDLE_VALUE;
        HANDLE               mapping_ = nullptr;
        const uint8_t*       view_ = nullptr;        // disk mode
        const uint8_t*       base_ = nullptr;        // whichever is in use
        std::vector<Ref>     index_;
        int                  fps_ = 30;
        size_t               bytes_ = 0;
    };

    // Produces a pack for `path`: loaded from the disk cache when `mode` is Disk
    // and a valid one is already there, otherwise built by running ffmpeg once
    // to completion. Calibrates first when opts.quality is 0, exactly as
    // playback would, so packed frames are byte-for-byte what streaming sends.
    //
    // False means "fall back to streaming" — over the memory budget, ffmpeg
    // failed, or `abort` fired. Blocking, and building a long video takes a
    // while; it reports throughout at the Progress log level.
    bool GetPack(const std::wstring& path, const RenderOpts& opts, Preprocess mode,
        FramePack& pack, AbortFn abort, void* abortUser, PackStats* stats);

    // Plays a built pack, pacing and holding live mode exactly as PlayVideo
    // does. No child process is involved.
    //
    // Seeking here is just an index: the frames are already built and evenly
    // spaced, so a jump costs nothing and lands exactly where it was asked to.
    bool PlayPack(Device& device, const FramePack& pack, const RenderOpts& opts,
        AbortFn abort, void* abortUser,
        FrameFn onFrame, void* frameUser,
        PlaybackStats* stats,
        SeekFn takeSeek = nullptr, void* seekUser = nullptr,
        PositionFn onPosition = nullptr, void* positionUser = nullptr,
        const Compositor* comp = nullptr);

    // PrepareImage with the answer remembered, so a still in a playlist is
    // transcoded once rather than once per rotation. Off transcodes every time.
    bool PrepareImageCached(const std::wstring& path, const RenderOpts& opts,
        Preprocess mode, std::vector<uint8_t>& jpeg);

    // Size of the on-disk pack cache, and a way to empty it. Both are safe to
    // call while something is playing; a mapped pack survives its file being
    // deleted underneath it.
    uint64_t PackCacheBytes();
    void     PackCacheClear();

    // -----------------------------------------------------------------------
    // Overlay
    //
    // A 960x480 premultiplied BGRA surface, and the machinery to blend it onto
    // outgoing frames. The surface is produced elsewhere — the tray app renders
    // its layers with WPF and pushes the pixels down — because everything about
    // fonts, layout and styling belongs where the editor is, and everything
    // about JPEG belongs here.
    //
    // Measured cost at 960x480: decode ~0.9 ms, blend ~0.3 ms, encode ~0.9 ms.
    // Against a 33 ms frame budget at 30 fps that is comfortable, but only
    // because none of it happens at all when no overlay is enabled.
    // -----------------------------------------------------------------------

    class Overlay {
    public:
        Overlay();
        ~Overlay();

        Overlay(const Overlay&) = delete;
        Overlay& operator=(const Overlay&) = delete;

        // WIC objects and the COM apartment they live in belong to the thread
        // that composites. Every playback worker calls Bind on entry and Unbind
        // on exit; composing on an unbound thread fails rather than guessing.
        bool BindThread();
        void UnbindThread();

        void SetEnabled(bool on);
        bool Enabled() const;

        // Replaces the surface, which must be exactly kPanelWidth x
        // kPanelHeight of premultiplied BGRA. Safe from any thread, and never
        // blocks the playback thread for longer than a pointer swap.
        bool Update(const uint8_t* bgraPremultiplied, int width, int height);
        void Clear();

        // Moves whenever the surface does. See OverlayVersionFn.
        uint32_t Version() const;

        // Decode, blend, re-encode. Returns false only when the frame could not
        // be encoded under the panel's cap at any quality down to the floor.
        bool Compose(std::vector<uint8_t>& jpeg);

        struct Stats {
            double   composeMs = 0.0;   // rolling mean of decode + blend
            double   encodeMs = 0.0;   // rolling mean
            float    quality = 0.0f;  // the working quality, 0..1
            uint32_t reencodes = 0;     // frames that needed more than one encode
            uint32_t drops = 0;     // frames that would not fit at all
        };

        Stats Snapshot() const;

    private:
        struct Impl;
        Impl* p_ = nullptr;
    };

    // -----------------------------------------------------------------------
    // ffmpeg
    // -----------------------------------------------------------------------

    // Looks beside the running executable first, then PATH. Empty if absent.
    std::wstring FindFfmpeg();
    std::wstring FindFfprobe(const std::wstring& ffmpegPath);

    // Turns "auto" into a concrete decode method by asking the ffmpeg build what
    // it supports; anything else passes through untouched. Costs a process
    // launch, so resolve once and reuse the answer rather than passing "auto"
    // into every call.
    std::wstring ResolveHwaccel(const std::wstring& ffmpegPath, const std::wstring& requested);

    // Duration in seconds, or 0 if ffprobe is missing or the file is unreadable.
    double ProbeDurationSeconds(const std::wstring& ffmpegPath, const std::wstring& input);

}  // namespace jl
