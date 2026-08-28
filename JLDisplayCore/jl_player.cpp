// jl_player.cpp — streaming a video to the panel.

#include "jl_internal.h"

namespace jl {

    bool PlayVideo(Device& device, const std::wstring& path, const RenderOpts& opts,
        AbortFn abort, void* abortUser,
        FrameFn onFrame, void* frameUser,
        PlaybackStats* stats)
    {
        auto aborted = [&] { return abort && abort(abortUser); };

        if (!device.IsOpen()) {
            Log(LogLevel::Error, L"device is not open");
            return false;
        }

        std::wstring ffmpeg = FindFfmpeg();
        if (ffmpeg.empty()) {
            Log(LogLevel::Error, L"ffmpeg.exe not found; video playback needs it");
            return false;
        }

        // Resolve "auto" once here and hand the concrete method down. Passing the
        // resolved value back through ResolveQuality is free — ResolveHwaccel only
        // spawns ffmpeg when it is actually asked to resolve "auto".
        RenderOpts resolved = opts;
        resolved.hwaccel = detail::ResolveHwaccel(ffmpeg, opts.hwaccel);

        const std::wstring filter = detail::BuildFilter(resolved.stretch, resolved.rotate);

        const int quality = ResolveQuality(path, resolved, abort, abortUser);
        if (quality < 0) return false;   // -1 unencodable, -2 cancelled
        if (aborted()) return false;

        device.SendCommand(cmd::Live);
        Sleep(100);

        Log(LogLevel::Info, L"playing at %d fps%s...",
            resolved.fps, resolved.loop ? L", looping" : L"");

        const DWORD framePeriodMs = 1000 / (DWORD)resolved.fps;
        DWORD lastKeepAlive = GetTickCount();
        DWORD startedAt = lastKeepAlive;
        DWORD nextFrameAt = lastKeepAlive;
        size_t sent = 0, dropped = 0, passes = 0;
        bool ok = true;

        // Loop at the application level by restarting ffmpeg. -stream_loop proved
        // unreliable here, and this also recovers if ffmpeg dies mid-playback.
        do {
            detail::FfmpegPipe pipe;
            std::wstring cl = detail::VideoCommand(ffmpeg, path, filter, resolved.hwaccel,
                resolved.fps, quality, false, false, 0);
            if (!detail::StartFfmpegPipe(cl, pipe)) { ok = false; break; }
            ++passes;

            std::vector<uint8_t> acc, frame;
            while (!aborted() && detail::ReadNextJpeg(pipe, acc, frame)) {
                if (frame.size() > kMaxJpegBytes) { ++dropped; continue; }

                // Pace against the wall clock rather than relying on ffmpeg's -re,
                // which does not throttle reliably when writing to a pipe.
                DWORD now = GetTickCount();
                if ((LONG)(nextFrameAt - now) > 0) Sleep(nextFrameAt - now);
                nextFrameAt += framePeriodMs;
                now = GetTickCount();
                if ((LONG)(now - nextFrameAt) > 500) nextFrameAt = now;   // resync if we fell behind

                if (!device.SendImageFrame(frame)) { ok = false; break; }
                ++sent;

                if (onFrame) onFrame(frame, frameUser);

                if (now - lastKeepAlive >= kLiveKeepAliveMs) {
                    device.SendCommand(cmd::Live);
                    lastKeepAlive = now;
                }

                if ((sent % 30) == 0) {
                    double secs = (now - startedAt) / 1000.0;
                    Log(LogLevel::Progress, L"%zu frames, %.1f fps, %zu dropped",
                        sent, secs > 0 ? sent / secs : 0.0, dropped);
                }
            }

            DWORD exitCode = 0;
            if (pipe.process) GetExitCodeProcess(pipe.process, &exitCode);
            pipe.Close();

            if (ok && !aborted() && exitCode != 0 && exitCode != STILL_ACTIVE)
                Log(LogLevel::Warn, L"ffmpeg exited with code %lu", exitCode);

        } while (ok && resolved.loop && !aborted());

        const double elapsed = (GetTickCount() - startedAt) / 1000.0;

        Log(LogLevel::Info, L"%zu frames sent over %zu pass(es)%s", sent, passes,
            dropped ? L", some dropped as oversized" : L"");

        if (stats) {
            stats->sent = sent;
            stats->dropped = dropped;
            stats->passes = passes;
            stats->fps = elapsed > 0 ? sent / elapsed : 0.0;
        }

        device.FlushEoi();
        return ok;
    }

}  // namespace jl
