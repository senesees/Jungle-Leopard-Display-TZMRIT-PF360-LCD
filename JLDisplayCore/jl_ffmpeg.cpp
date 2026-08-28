// jl_ffmpeg.cpp — locating ffmpeg, driving it, and turning a still image into a
// panel-ready JPEG.

#include "jl_internal.h"

#include <cstdio>
#include <cstdlib>
#include <cctype>

namespace jl {

    namespace detail {

        void FfmpegPipe::Close()
        {
            if (readEnd) { CloseHandle(readEnd); readEnd = nullptr; }
            if (process) {
                // ffmpeg exits on its own when the pipe closes, but don't wait forever.
                if (WaitForSingleObject(process, 1000) != WAIT_OBJECT_0)
                    TerminateProcess(process, 0);
                CloseHandle(process);
                process = nullptr;
            }
        }

        bool RunHidden(const std::wstring& cmdline, DWORD timeoutMs)
        {
            STARTUPINFOW si{};
            si.cb = sizeof(si);
            si.dwFlags = STARTF_USESHOWWINDOW;
            si.wShowWindow = SW_HIDE;
            PROCESS_INFORMATION pi{};

            // CreateProcessW may write to the command line buffer, so it must be writable.
            std::vector<wchar_t> buf(cmdline.begin(), cmdline.end());
            buf.push_back(L'\0');

            if (!CreateProcessW(nullptr, &buf[0], nullptr, nullptr, FALSE,
                CREATE_NO_WINDOW, nullptr, nullptr, &si, &pi)) {
                Log(LogLevel::Error, L"failed to launch ffmpeg: %lu", GetLastError());
                return false;
            }

            DWORD wait = WaitForSingleObject(pi.hProcess, timeoutMs);
            DWORD code = 1;
            if (wait == WAIT_OBJECT_0) GetExitCodeProcess(pi.hProcess, &code);
            else TerminateProcess(pi.hProcess, 1);

            CloseHandle(pi.hProcess);
            CloseHandle(pi.hThread);
            return code == 0;
        }

        bool StartFfmpegPipe(const std::wstring& cmdline, FfmpegPipe& pipe)
        {
            SECURITY_ATTRIBUTES sa{};
            sa.nLength = sizeof(sa);
            sa.bInheritHandle = TRUE;

            HANDLE readEnd = nullptr, writeEnd = nullptr;
            if (!CreatePipe(&readEnd, &writeEnd, &sa, 1 << 20)) return false;
            // The child must not inherit our read end, or the pipe never signals EOF.
            SetHandleInformation(readEnd, HANDLE_FLAG_INHERIT, 0);

            STARTUPINFOW si{};
            si.cb = sizeof(si);
            si.dwFlags = STARTF_USESTDHANDLES | STARTF_USESHOWWINDOW;
            si.wShowWindow = SW_HIDE;
            si.hStdOutput = writeEnd;
            si.hStdError = GetStdHandle(STD_ERROR_HANDLE);
            si.hStdInput = GetStdHandle(STD_INPUT_HANDLE);

            PROCESS_INFORMATION pi{};
            std::vector<wchar_t> buf(cmdline.begin(), cmdline.end());
            buf.push_back(L'\0');

            BOOL ok = CreateProcessW(nullptr, &buf[0], nullptr, nullptr, TRUE,
                CREATE_NO_WINDOW, nullptr, nullptr, &si, &pi);
            CloseHandle(writeEnd);   // our copy; the child holds the other

            if (!ok) {
                CloseHandle(readEnd);
                Log(LogLevel::Error, L"failed to launch ffmpeg: %lu", GetLastError());
                return false;
            }
            CloseHandle(pi.hThread);
            pipe.process = pi.hProcess;
            pipe.readEnd = readEnd;
            return true;
        }

        bool ReadNextJpeg(FfmpegPipe& pipe, std::vector<uint8_t>& acc,
            std::vector<uint8_t>& frame)
        {
            for (;;) {
                // A complete frame starts FF D8 and ends FF D9. Inside entropy-coded
                // data every literal FF is stuffed as FF 00, so a bare FF D9 is
                // unambiguously the end marker.
                if (acc.size() >= 4 && acc[0] == 0xFF && acc[1] == 0xD8) {
                    for (size_t i = 2; i + 1 < acc.size(); ++i) {
                        if (acc[i] == 0xFF && acc[i + 1] == 0xD9) {
                            frame.assign(acc.begin(), acc.begin() + i + 2);
                            acc.erase(acc.begin(), acc.begin() + i + 2);
                            return true;
                        }
                    }
                }
                else if (acc.size() >= 2) {
                    // Resynchronise if we somehow started mid-stream.
                    size_t soi = std::string::npos;
                    for (size_t i = 0; i + 1 < acc.size(); ++i)
                        if (acc[i] == 0xFF && acc[i + 1] == 0xD8) { soi = i; break; }
                    if (soi == std::string::npos) acc.clear();
                    else if (soi > 0) acc.erase(acc.begin(), acc.begin() + soi);
                }

                uint8_t tmp[65536];
                DWORD got = 0;
                if (!ReadFile(pipe.readEnd, tmp, sizeof(tmp), &got, nullptr) || got == 0)
                    return false;    // pipe closed: ffmpeg finished
                acc.insert(acc.end(), tmp, tmp + got);
            }
        }

        std::string ReadAllText(FfmpegPipe& pipe)
        {
            std::string out;
            char tmp[4096];
            DWORD got = 0;
            while (ReadFile(pipe.readEnd, tmp, sizeof(tmp), &got, nullptr) && got > 0)
                out.append(tmp, got);
            return out;
        }

        std::wstring BuildFilter(bool stretch, int rotate)
        {
            std::wstring rot;
            switch (((rotate % 360) + 360) % 360) {
            case 90:  rot = L"transpose=1,"; break;
            case 180: rot = L"hflip,vflip,"; break;
            case 270: rot = L"transpose=2,"; break;
            default:  rot = L"";             break;
            }
            return rot + (stretch
                ? L"scale=960:480"
                : L"scale=960:480:force_original_aspect_ratio=decrease,"
                L"pad=960:480:(ow-iw)/2:(oh-ih)/2:color=black");
        }

        std::wstring ResolveHwaccel(const std::wstring& ffmpeg, const std::wstring& requested)
        {
            if (requested.empty() || requested != L"auto") return requested;

            FfmpegPipe pipe;
            // -loglevel error suppresses the build banner, which ffmpeg writes to
            // stderr — and stderr is inherited from whoever loaded us.
            if (!StartFfmpegPipe(L"\"" + ffmpeg + L"\" -loglevel error -hwaccels", pipe))
                return L"";
            std::string list = ReadAllText(pipe);
            pipe.Close();

            for (char& c : list) c = (char)tolower((unsigned char)c);

            // Preference order: dedicated decode engines first, then the generic
            // DirectX path that works on any Windows GPU.
            const char* order[] = { "cuda", "qsv", "d3d11va", "dxva2", "vulkan" };
            for (const char* name : order) {
                if (list.find(name) != std::string::npos) {
                    std::string n(name);
                    std::wstring w(n.begin(), n.end());
                    Log(LogLevel::Info, L"hwaccel: selected %s", w.c_str());
                    return w;
                }
            }
            Log(LogLevel::Info, L"hwaccel: none available, using software decode");
            return L"";
        }

        std::wstring VideoCommand(const std::wstring& ffmpeg, const std::wstring& input,
            const std::wstring& filter, const std::wstring& hwaccel,
            int fps, int quality, bool loop, bool paced,
            double seconds, bool keyframesOnly)
        {
            std::wstring cl = L"\"" + ffmpeg + L"\" -y -loglevel error";
            // Must precede -i: these are input options.
            if (!hwaccel.empty()) cl += L" -hwaccel " + hwaccel;
            // -skip_frame nokey makes the decoder discard inter-frames without
            // reconstructing them, so a survey costs a fraction of a full decode.
            // Keyframes are intra-coded and therefore the most detailed frames in the
            // stream, which makes them a conservative sample for a size ceiling.
            if (keyframesOnly) cl += L" -skip_frame nokey";
            if (loop)  cl += L" -stream_loop -1";
            // -re makes ffmpeg emit at the source's native rate, which paces playback
            // for us instead of us sleeping between writes.
            if (paced) cl += L" -re";
            cl += L" -i \"" + input + L"\"";
            if (seconds > 0) cl += L" -t " + std::to_wstring(seconds);
            if (keyframesOnly) {
                // Take every surviving frame; no rate conversion.
                cl += L" -vf \"" + filter + L"\" -fps_mode passthrough";
            }
            else {
                cl += L" -vf \"" + filter + L",fps=" + std::to_wstring(fps) + L"\"";
            }
            cl += L" -f image2pipe -vcodec mjpeg -q:v " + std::to_wstring(quality) + L" -";
            return cl;
        }

    }  // namespace detail

    // -----------------------------------------------------------------------

    std::wstring FindFfmpeg()
    {
        // Looking beside the executable first means a copy of ffmpeg.exe next to
        // the binary needs no PATH setup at all.
        wchar_t exePath[MAX_PATH] = L"";
        if (GetModuleFileNameW(nullptr, exePath, MAX_PATH)) {
            std::wstring dir(exePath);
            size_t slash = dir.find_last_of(L"\\/");
            if (slash != std::wstring::npos) {
                std::wstring local = dir.substr(0, slash) + L"\\ffmpeg.exe";
                if (GetFileAttributesW(local.c_str()) != INVALID_FILE_ATTRIBUTES) return local;
            }
        }
        wchar_t found[MAX_PATH] = L"";
        if (SearchPathW(nullptr, L"ffmpeg.exe", nullptr, MAX_PATH, found, nullptr)) return found;
        return L"";
    }

    // ffprobe ships alongside ffmpeg in every standard build, but it is optional
    // here: without it calibration just reports elapsed seconds instead of a
    // percentage.
    std::wstring FindFfprobe(const std::wstring& ffmpegPath)
    {
        size_t slash = ffmpegPath.find_last_of(L"\\/");
        if (slash != std::wstring::npos) {
            std::wstring sibling = ffmpegPath.substr(0, slash) + L"\\ffprobe.exe";
            if (GetFileAttributesW(sibling.c_str()) != INVALID_FILE_ATTRIBUTES) return sibling;
        }
        wchar_t found[MAX_PATH] = L"";
        if (SearchPathW(nullptr, L"ffprobe.exe", nullptr, MAX_PATH, found, nullptr)) return found;
        return L"";
    }

    std::wstring ResolveHwaccel(const std::wstring& ffmpegPath, const std::wstring& requested)
    {
        return detail::ResolveHwaccel(ffmpegPath, requested);
    }

    double ProbeDurationSeconds(const std::wstring& ffmpegPath, const std::wstring& input)
    {
        std::wstring ffprobe = FindFfprobe(ffmpegPath);
        if (ffprobe.empty()) return 0.0;

        std::wstring cl = L"\"" + ffprobe + L"\" -v error"
            L" -show_entries format=duration"
            L" -of default=noprint_wrappers=1:nokey=1"
            L" \"" + input + L"\"";

        detail::FfmpegPipe pipe;
        if (!detail::StartFfmpegPipe(cl, pipe)) return 0.0;
        std::string text = detail::ReadAllText(pipe);
        pipe.Close();

        double secs = atof(text.c_str());
        return (secs > 0.0 && secs < 1e7) ? secs : 0.0;
    }

    // -----------------------------------------------------------------------
    // Still images
    // -----------------------------------------------------------------------

    namespace {

        std::wstring TempJpegPath()
        {
            wchar_t dir[MAX_PATH] = L"";
            GetTempPathW(MAX_PATH, dir);
            return std::wstring(dir) + L"jl_frame_" + std::to_wstring(GetCurrentProcessId()) + L".jpg";
        }

        // Skips ffmpeg entirely when the input already conforms.
        bool AlreadyConforming(const std::vector<uint8_t>& data)
        {
            if (data.size() < 3 || data.size() > kMaxJpegBytes) return false;
            if (data[0] != 0xFF || data[1] != 0xD8) return false;
            int w = 0, h = 0;
            return detail::JpegSize(data, w, h) && w == kPanelWidth && h == kPanelHeight;
        }

        // ffmpeg's -q:v runs 2 (best) to 31 (worst), so we walk it upward until the
        // output fits. The vendor app does the same thing from the other direction,
        // starting at quality 100 and stepping down by 2 (see getSizeBt in device.js).
        bool ConvertWithFfmpeg(const std::wstring& input, const RenderOpts& opts,
            std::vector<uint8_t>& out)
        {
            std::wstring ffmpeg = FindFfmpeg();
            if (ffmpeg.empty()) {
                Log(LogLevel::Error,
                    L"ffmpeg.exe not found (looked beside this exe and on PATH). "
                    L"Install it with: winget install \"FFmpeg (Essentials Build)\" "
                    L"or drop ffmpeg.exe next to this program. Alternatively pass an "
                    L"image that is already 960x480 JPEG under 80 KB.");
                return false;
            }

            const std::wstring filter = detail::BuildFilter(opts.stretch, opts.rotate);
            const std::wstring tmp = TempJpegPath();
            const int qualities[] = { 2, 3, 4, 5, 7, 9, 12, 16, 20, 25, 31 };

            for (int q : qualities) {
                DeleteFileW(tmp.c_str());

                std::wstring cl = L"\"" + ffmpeg + L"\""
                    L" -y -loglevel error"
                    L" -i \"" + input + L"\""
                    L" -vf \"" + filter + L"\""
                    L" -frames:v 1"
                    L" -q:v " + std::to_wstring(q) +
                    L" \"" + tmp + L"\"";

                if (!detail::RunHidden(cl)) {
                    Log(LogLevel::Error, L"ffmpeg failed on %s (is it a readable image?)",
                        input.c_str());
                    DeleteFileW(tmp.c_str());
                    return false;
                }

                std::vector<uint8_t> data;
                if (!detail::ReadFileBytes(tmp, data)) {
                    Log(LogLevel::Error, L"ffmpeg produced no output");
                    DeleteFileW(tmp.c_str());
                    return false;
                }

                if (data.size() <= kMaxJpegBytes) {
                    Log(LogLevel::Info, L"encoded %zu KB at -q:v %d", data.size() / 1024, q);
                    out.swap(data);
                    DeleteFileW(tmp.c_str());
                    return true;
                }
            }

            DeleteFileW(tmp.c_str());
            Log(LogLevel::Error, L"could not get under %zu KB even at lowest quality",
                kMaxJpegBytes / 1024);
            return false;
        }

    }  // namespace

    bool PrepareImage(const std::wstring& path, const RenderOpts& opts,
        std::vector<uint8_t>& jpeg)
    {
        std::vector<uint8_t> raw;
        if (!detail::ReadFileBytes(path, raw)) {
            Log(LogLevel::Error, L"could not read %s", path.c_str());
            return false;
        }

        // The as-is shortcut only applies when no rotation was asked for.
        if (opts.rotate == 0 && AlreadyConforming(raw)) {
            Log(LogLevel::Info, L"input already 960x480 JPEG, %zu KB - using as-is",
                raw.size() / 1024);
            jpeg.swap(raw);
            return true;
        }

        return ConvertWithFfmpeg(path, opts, jpeg);
    }

}  // namespace jl
