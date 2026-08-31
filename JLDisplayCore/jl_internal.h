// jl_internal.h — shared internals of the core library. Not installed; the
// calibration survey and the player both drive ffmpeg the same way, and this is
// where that machinery lives.

#pragma once

#include "jl_core.h"

namespace jl {
    namespace detail {

        // -------------------------------------------------------------------
        // Small helpers
        // -------------------------------------------------------------------

        std::wstring Widen(const std::string& s);
        bool ReadFileBytes(const std::wstring& path, std::vector<uint8_t>& out);

        // FNV-1a as 16 hex digits. Both cache keys — calibration and packs —
        // are built from it, so they invalidate on exactly the same events.
        std::string Fnv1aHex(const std::string& s);

        // Pulls width/height out of a JPEG's SOFn marker.
        bool JpegSize(const std::vector<uint8_t>& j, int& w, int& h);

        // True only for the one JPEG flavour the panel's decoder accepts:
        // baseline SOF0, three components, 4:2:0 chroma subsampling.
        bool JpegIsBaseline420(const std::vector<uint8_t>& j);

        uint16_t Sum16(const std::vector<uint8_t>& b, size_t from, size_t to);

        // -------------------------------------------------------------------
        // Framing
        // -------------------------------------------------------------------

        // Mirrors handleData(): 55 AA | len(u16 LE) | cmd | payload | sum(u16 LE)
        std::vector<uint8_t> BuildFrame(uint8_t command, const uint8_t* payload = nullptr,
            size_t payloadLen = 0);

        // Image frames use a DIFFERENT envelope: u32 LE length, raw JPEG, u16 LE sum.
        std::vector<uint8_t> BuildImageFrame(const std::vector<uint8_t>& jpeg);

        // -------------------------------------------------------------------
        // ffmpeg process plumbing
        // -------------------------------------------------------------------

        struct FfmpegPipe {
            HANDLE process = nullptr;
            HANDLE readEnd = nullptr;

            void Close();
        };

        bool StartFfmpegPipe(const std::wstring& cmdline, FfmpegPipe& pipe);

        // Runs a command to completion with no window. True on exit code 0.
        bool RunHidden(const std::wstring& cmdline, DWORD timeoutMs = 60000);

        // Pulls one complete JPEG (SOI..EOI) out of the stream, refilling as
        // needed. False at end of stream.
        bool ReadNextJpeg(FfmpegPipe& pipe, std::vector<uint8_t>& acc,
            std::vector<uint8_t>& frame);

        // Reads a child process's entire stdout as text.
        std::string ReadAllText(FfmpegPipe& pipe);

        // -------------------------------------------------------------------
        // Filter and command construction
        // -------------------------------------------------------------------

        // Rotate BEFORE scaling, so the result is always 960x480 whatever the
        // angle. transpose=1 is clockwise, transpose=2 counter-clockwise;
        // hflip+vflip is an exact, cheap 180 with no resampling.
        std::wstring BuildFilter(bool stretch, int rotate);

        // -------------------------------------------------------------------
        // Frame pacing
        //
        // Streaming and pack playback differ only in where the frames come
        // from; the timing, the keep-alive and the resync after a stall are the
        // same problem and live here once.
        // -------------------------------------------------------------------

        struct Pacer {
            DWORD periodMs = 33;
            DWORD nextFrameAt = 0;
            DWORD lastKeepAlive = 0;
            DWORD startedAt = 0;

            void Start(int fps);

            // Sleeps until this frame's slot, then reports the time it woke at.
            // A caller more than half a second late is resynced rather than
            // left trying to catch up frame by frame.
            DWORD WaitForSlot();

            // Live mode lapses without this; false means the device went away.
            bool KeepAliveIfDue(Device& device, DWORD now);
        };

        // ffmpeg's own "-hwaccel auto" only considers methods needing no explicit
        // device setup, so on Windows it lands on d3d11va or nothing and never
        // picks CUDA. Resolve "auto" ourselves against what the build offers.
        //
        // Note this reports what was COMPILED IN, not whether the hardware is
        // present and working; ffmpeg falls back to software if the device fails.
        std::wstring ResolveHwaccel(const std::wstring& ffmpeg, const std::wstring& requested);

        // `startSeconds` seeks the input before decoding; `seconds` caps how much
        // is decoded after that.
        std::wstring VideoCommand(const std::wstring& ffmpeg, const std::wstring& input,
            const std::wstring& filter, const std::wstring& hwaccel,
            int fps, int quality, bool loop, bool paced,
            double seconds, bool keyframesOnly = false, double startSeconds = 0.0);

        // -------------------------------------------------------------------
        // Calibration cache
        //
        // The chosen quality depends only on the video's content and the filter
        // chain, so it is worth remembering. The key includes the file's size and
        // last-write time, so re-encoding or replacing a video invalidates its
        // entry automatically. Shared with the CLI: same file, same format.
        // -------------------------------------------------------------------

        std::wstring CacheDirectory();
        std::wstring CacheFilePath();
        std::string  CalibrationKey(const std::wstring& input, const std::wstring& filter,
            size_t sizeTarget);
        int  CacheLookup(const std::string& key);
        void CacheStore(const std::string& key, int quality);

        // Finds the lowest -q:v whose frames all fit, by encoding every KEYFRAME
        // in the file. Returns -1 if even the worst quality overflows, or -2 if
        // `abort` asked to stop partway.
        int CalibrateQuality(const std::wstring& ffmpeg, const std::wstring& input,
            const std::wstring& filter, const std::wstring& hwaccel,
            AbortFn abort, void* abortUser);

    }  // namespace detail
}  // namespace jl
