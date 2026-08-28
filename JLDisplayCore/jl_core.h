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
    // -----------------------------------------------------------------------

    namespace cmd {
        constexpr uint8_t Restart = 0x01;
        constexpr uint8_t SetLight = 0x03;
        constexpr uint8_t GetDeviceInfo = 0x06;
        constexpr uint8_t Live = 0x11;  // start / hold live mode
        constexpr uint8_t SetMotionBeforeOff = 0x14;
        constexpr uint8_t SetMotionTimeout = 0x15;
        constexpr uint8_t SetRegion = 0x20;
        constexpr uint8_t Close = 0x21;
        constexpr uint8_t SetMotor = 0x25;
        constexpr uint8_t SetRealTimeTimeout = 0x26;
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
        bool HoldStill(const std::vector<uint8_t>& jpeg, AbortFn abort, void* abortUser);

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

    // Observes each frame as it goes out, for a live preview. Called on the
    // playback thread; keep it cheap and copy anything you keep.
    using FrameFn = void (*)(const std::vector<uint8_t>& jpeg, void* user);

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
    bool PlayVideo(Device& device, const std::wstring& path, const RenderOpts& opts,
        AbortFn abort, void* abortUser,
        FrameFn onFrame, void* frameUser,
        PlaybackStats* stats);

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
