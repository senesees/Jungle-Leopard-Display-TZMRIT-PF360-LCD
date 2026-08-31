// jl_calibrate.cpp — picking a JPEG quality whose frames all fit the panel's
// 80 KB limit, and remembering the answer.

#include "jl_internal.h"

#include <cstdio>
#include <cstdlib>

namespace jl {
    namespace detail {

        namespace {

            // CacheStore rewrites the whole file, so two threads calibrating at
            // once — which is exactly what a library pre-warming several new
            // videos does — would otherwise lose each other's entries.
            CRITICAL_SECTION& CacheLock()
            {
                static CRITICAL_SECTION cs = [] {
                    CRITICAL_SECTION c;
                    InitializeCriticalSection(&c);
                    return c;
                }();
                return cs;
            }

            struct CacheGuard {
                CacheGuard() { EnterCriticalSection(&CacheLock()); }
                ~CacheGuard() { LeaveCriticalSection(&CacheLock()); }
                CacheGuard(const CacheGuard&) = delete;
                CacheGuard& operator=(const CacheGuard&) = delete;
            };

        }  // namespace

        // Everything this library remembers between runs lives here: the
        // calibration table, and the frame packs beside it.
        std::wstring CacheDirectory()
        {
            wchar_t buf[MAX_PATH] = L"";
            DWORD n = GetEnvironmentVariableW(L"LOCALAPPDATA", buf, MAX_PATH);
            std::wstring dir = (n > 0 && n < MAX_PATH) ? std::wstring(buf) : std::wstring(L".");
            dir += L"\\jl_display";
            CreateDirectoryW(dir.c_str(), nullptr);
            return dir;
        }

        std::wstring CacheFilePath()
        {
            return CacheDirectory() + L"\\calibration.txt";
        }

        std::string CalibrationKey(const std::wstring& input, const std::wstring& filter,
            size_t sizeTarget)
        {
            std::wstring full = input;
            wchar_t abs[MAX_PATH] = L"";
            if (GetFullPathNameW(input.c_str(), MAX_PATH, abs, nullptr)) full = abs;

            unsigned long long bytes = 0, mtime = 0;
            WIN32_FILE_ATTRIBUTE_DATA fad{};
            if (GetFileAttributesExW(full.c_str(), GetFileExInfoStandard, &fad)) {
                bytes = ((unsigned long long)fad.nFileSizeHigh << 32) | fad.nFileSizeLow;
                mtime = ((unsigned long long)fad.ftLastWriteTime.dwHighDateTime << 32)
                    | fad.ftLastWriteTime.dwLowDateTime;
            }

            std::string material;
            for (wchar_t c : full)   material += (char)(c & 0xFF);
            material += "|";
            for (wchar_t c : filter) material += (char)(c & 0xFF);

            char extra[96];
            // "v2" marks keyframe-based calibration; bump it if the method changes again.
            sprintf_s(extra, sizeof(extra), "|%llu|%llu|%zu|v2", bytes, mtime, sizeTarget);
            material += extra;

            return Fnv1aHex(material);
        }

        int CacheLookup(const std::string& key)
        {
            CacheGuard lock;
            std::vector<uint8_t> raw;
            if (!ReadFileBytes(CacheFilePath(), raw)) return 0;

            std::string text(raw.begin(), raw.end());
            size_t pos = 0;
            while (pos < text.size()) {
                size_t eol = text.find('\n', pos);
                if (eol == std::string::npos) eol = text.size();
                std::string line = text.substr(pos, eol - pos);
                pos = eol + 1;

                size_t sp = line.find(' ');
                if (sp == std::string::npos) continue;
                if (line.compare(0, sp, key) == 0) {
                    int q = atoi(line.c_str() + sp + 1);
                    return (q >= 2 && q <= 31) ? q : 0;
                }
            }
            return 0;
        }

        void CacheStore(const std::string& key, int quality)
        {
            CacheGuard lock;
            std::wstring path = CacheFilePath();

            // Rewrite without any previous entry for this key, keeping the file bounded.
            std::vector<std::string> keep;
            std::vector<uint8_t> raw;
            if (ReadFileBytes(path, raw)) {
                std::string text(raw.begin(), raw.end());
                size_t pos = 0;
                while (pos < text.size()) {
                    size_t eol = text.find('\n', pos);
                    if (eol == std::string::npos) eol = text.size();
                    std::string line = text.substr(pos, eol - pos);
                    pos = eol + 1;
                    if (line.empty()) continue;
                    size_t sp = line.find(' ');
                    if (sp != std::string::npos && line.compare(0, sp, key) == 0) continue;
                    keep.push_back(line);
                }
            }
            while (keep.size() >= 500) keep.erase(keep.begin());   // oldest first

            FILE* f = nullptr;
#ifdef _MSC_VER
            if (_wfopen_s(&f, path.c_str(), L"wb") != 0 || !f) return;
#else
            f = _wfopen(path.c_str(), L"wb");
            if (!f) return;
#endif
            for (const std::string& line : keep) fprintf(f, "%s\n", line.c_str());
            fprintf(f, "%s %d\n", key.c_str(), quality);
            fclose(f);
        }

        // Surveying the whole file matters: calibrating on only the first seconds
        // badly underestimates a video that gets busier later. Targets kSafeJpegBytes
        // rather than the hard cap so a busier inter-frame between keyframes still fits.
        int CalibrateQuality(const std::wstring& ffmpeg, const std::wstring& input,
            const std::wstring& filter, const std::wstring& hwaccel,
            AbortFn abort, void* abortUser)
        {
            auto aborted = [&] { return abort && abort(abortUser); };

            const int    candidates[] = { 3, 4, 5, 7, 9, 12, 16, 20, 25, 31 };
            const double duration = ProbeDurationSeconds(ffmpeg, input);
            const DWORD  began = GetTickCount();

            if (duration > 0)
                Log(LogLevel::Info, L"calibrating (keyframes of %.1fs of video)...", duration);
            else
                Log(LogLevel::Info, L"calibrating (keyframes)...");

            for (int q : candidates) {
                FfmpegPipe pipe;
                std::wstring cl = VideoCommand(ffmpeg, input, filter, hwaccel,
                    0, q, false, false, 0, true);
                if (!StartFfmpegPipe(cl, pipe)) return -1;

                std::vector<uint8_t> acc, frame;
                size_t worst = 0, count = 0;
                bool   failed = false;
                DWORD  lastPrint = 0;

                bool cancelled = false;
                while (ReadNextJpeg(pipe, acc, frame)) {
                    if (aborted()) { cancelled = true; break; }

                    if (frame.size() > worst) worst = frame.size();
                    ++count;

                    // No point surveying the rest of the file once this quality is
                    // already too big — jump straight to the next candidate.
                    if (worst > kSafeJpegBytes) { failed = true; break; }

                    DWORD now = GetTickCount();
                    if (now - lastPrint >= 250) {
                        lastPrint = now;
                        Log(LogLevel::Progress, L"  -q:v %-2d  %zu keyframes  worst %zu KB  %.1fs",
                            q, count, worst / 1024, (now - began) / 1000.0);
                    }
                }
                pipe.Close();

                if (cancelled) return -2;

                if (count == 0) {
                    Log(LogLevel::Error,
                        L"ffmpeg decoded no frames - unreadable or unsupported input");
                    return -1;
                }

                if (failed) {
                    Log(LogLevel::Info, L"  -q:v %-2d  too large (%zu KB) - trying lower quality",
                        q, worst / 1024);
                    continue;
                }

                Log(LogLevel::Info, L"calibrated: -q:v %d over %zu keyframes, worst %zu KB (%.1fs)",
                    q, count, worst / 1024, (GetTickCount() - began) / 1000.0);
                return q;
            }

            Log(LogLevel::Error, L"cannot keep frames under %zu KB even at lowest quality",
                kSafeJpegBytes / 1024);
            return -1;
        }

    }  // namespace detail

    int ResolveQuality(const std::wstring& path, const RenderOpts& opts,
        AbortFn abort, void* abortUser)
    {
        if (opts.quality > 0) {
            Log(LogLevel::Info, L"using -q:v %d (calibration skipped)", opts.quality);
            return opts.quality;
        }

        std::wstring ffmpeg = FindFfmpeg();
        if (ffmpeg.empty()) {
            Log(LogLevel::Error, L"ffmpeg.exe not found; video playback needs it");
            return -1;
        }

        const std::wstring filter = detail::BuildFilter(opts.stretch, opts.rotate);
        const std::wstring hwaccel = detail::ResolveHwaccel(ffmpeg, opts.hwaccel);
        const std::string  key = detail::CalibrationKey(path, filter, kSafeJpegBytes);

        int cached = opts.recalibrate ? 0 : detail::CacheLookup(key);
        if (cached > 0) {
            Log(LogLevel::Info, L"using cached calibration: -q:v %d", cached);
            return cached;
        }

        int quality = detail::CalibrateQuality(ffmpeg, path, filter, hwaccel, abort, abortUser);
        if (quality > 0) detail::CacheStore(key, quality);
        return quality;
    }

}  // namespace jl
