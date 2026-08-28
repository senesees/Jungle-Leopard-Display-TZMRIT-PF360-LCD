// jl_display.cpp — command line client for the Jungle Leopard / TZMRIT PF360
// pump LCD. All the real work lives in JLDisplayCore; this file is argument
// parsing, console output, and Ctrl-C.
//
//     jl_display.exe --info
//     jl_display.exe --light 60
//     jl_display.exe --image anything.png            # any format/size; auto-converted
//     jl_display.exe --image photo.jpg --rotate 180  # screen mounted upside down
//     jl_display.exe --image photo.jpg --stretch     # fill the panel, ignore aspect
//     jl_display.exe --image frame.jpg --once        # send one frame and exit
//     jl_display.exe --video clip.mp4                # play a video
//     jl_display.exe --video clip.gif --loop         # loop it forever
//
// Video calibration (picking a JPEG quality whose frames all fit the panel's
// 80 KB limit) is cached in %LOCALAPPDATA%\\jl_display\\calibration.txt, keyed on
// the file's path, size, timestamp and filter chain. Pass --recalibrate to
// force a fresh survey. The tray app shares that same cache.
//
// The pump head rotates 270 degrees on its magnet, so --rotate compensates for
// however yours ended up mounted. There is no device-side rotation command;
// the vendor app rotates host-side too.
//
// Images are transcoded with ffmpeg to 960x480 JPEG under 80 KB. ffmpeg is
// found next to this exe or on PATH; if the input is ALREADY a conforming
// 960x480 JPEG under the cap, ffmpeg is not needed at all.
//
// Close the vendor app first — only one process can hold the COM port.

#include "jl_core.h"

#include <atomic>
#include <cstdio>
#include <cstdlib>
#include <string>
#include <vector>

// ---------------------------------------------------------------------------
// Console log sink
//
// Progress messages are rewritten in place on one line. Anything else has to
// wipe whatever the last progress line left behind before it prints, or the
// tail of a longer progress line shows up as trailing garbage.
// ---------------------------------------------------------------------------

static bool g_progressPending = false;

static void ClearProgressLine()
{
    if (!g_progressPending) return;
    printf("\r%-70s\r", "");
    g_progressPending = false;
}

static void ConsoleLog(jl::LogLevel level, const wchar_t* text, void*)
{
    if (level == jl::LogLevel::Progress) {
        wprintf(L"\r%s    ", text);
        fflush(stdout);
        g_progressPending = true;
        return;
    }

    ClearProgressLine();
    if (level == jl::LogLevel::Info) wprintf(L"%s\n", text);
    else fwprintf(stderr, L"%s\n", text);
}

// ---------------------------------------------------------------------------

static std::atomic<bool> g_running(true);
static BOOL WINAPI CtrlHandler(DWORD) { g_running = false; return TRUE; }
static bool ShouldAbort(void*) { return !g_running; }

static std::wstring Widen(const char* s)
{
    if (!s || !*s) return L"";
    int n = MultiByteToWideChar(CP_UTF8, 0, s, -1, nullptr, 0);
    std::wstring w(n ? n - 1 : 0, 0);
    if (n > 1) MultiByteToWideChar(CP_UTF8, 0, s, -1, &w[0], n);
    return w;
}

int main(int argc, char** argv)
{
    jl::SetLogSink(ConsoleLog, nullptr);

    jl::RenderOpts opts;
    std::wstring port;
    std::wstring imagePath;
    std::wstring videoPath;
    int  light = -1;
    bool info = false;
    bool once = false;

    for (int i = 1; i < argc; ++i) {
        std::string a = argv[i];
        if (a == "--info")    info = true;
        else if (a == "--once")    once = true;
        else if (a == "--stretch") opts.stretch = true;
        else if (a == "--loop")    opts.loop = true;
        else if (a == "--recalibrate") opts.recalibrate = true;
        else if (a == "--hwaccel" && i + 1 < argc) opts.hwaccel = Widen(argv[++i]);
        else if (a == "--image" && i + 1 < argc) imagePath = Widen(argv[++i]);
        else if (a == "--video" && i + 1 < argc) videoPath = Widen(argv[++i]);
        else if (a == "--light" && i + 1 < argc) light = atoi(argv[++i]);
        else if (a == "--rotate" && i + 1 < argc) opts.rotate = atoi(argv[++i]);
        else if (a == "--fps" && i + 1 < argc) opts.fps = atoi(argv[++i]);
        else if (a == "--quality" && i + 1 < argc) opts.quality = atoi(argv[++i]);
        else if (a == "--port" && i + 1 < argc) port = Widen(argv[++i]);
    }

    if (!info && imagePath.empty() && videoPath.empty() && light < 0) {
        fprintf(stderr,
            "usage: %s [--port COMn] [--rotate 0|90|180|270] [--stretch]\n"
            "         --info\n"
            "         --light 0-100\n"
            "         --image FILE [--once]\n"
            "         --video FILE [--loop] [--fps N] [--quality 2-31]\n"
            "                      [--recalibrate] [--hwaccel auto|d3d11va|cuda|qsv]\n",
            argv[0]);
        return 1;
    }

    std::wstring problem;
    if (!jl::ValidateOpts(opts, problem)) {
        fwprintf(stderr, L"%s\n", problem.c_str());
        return 1;
    }
    if (opts.hwaccel == L"none") opts.hwaccel.clear();

    // Prepare the frame BEFORE opening the port, so a bad image or a missing
    // ffmpeg doesn't leave the device sitting in live mode with nothing to show.
    std::vector<uint8_t> jpeg;
    if (!imagePath.empty() && !jl::PrepareImage(imagePath, opts, jpeg))
        return 1;

    jl::Device device;
    if (!device.Open(port, problem)) {
        fwprintf(stderr, L"%s\n", problem.c_str());
        return 1;
    }
    wprintf(L"connected on %s\n", device.Port().c_str());

    device.Clear();

    if (info) {
        std::string body;
        if (device.GetInfo(body)) printf("%s\n", body.c_str());
        else fprintf(stderr, "no reply\n");
    }

    if (light >= 0) {
        if (light > 100) light = 100;
        printf("brightness -> %d\n", light);
        device.SetBrightness(light);
    }

    if (!jpeg.empty()) {
        device.SendCommand(jl::cmd::Live);
        Sleep(100);

        if (!device.SendImageFrame(jpeg)) return 1;
        printf("sent %zu byte frame\n", jpeg.size());

        if (!once) {
            SetConsoleCtrlHandler(CtrlHandler, TRUE);
            printf("holding live mode (Ctrl-C to stop)...\n");
            DWORD last = GetTickCount();
            while (g_running) {
                Sleep(100);
                if (GetTickCount() - last >= jl::kLiveKeepAliveMs) {
                    device.KeepAlive();   // live mode lapses without this
                    last = GetTickCount();
                }
            }
            printf("\nstopping\n");
            device.FlushEoi();
        }
    }

    if (!videoPath.empty()) {
        SetConsoleCtrlHandler(CtrlHandler, TRUE);
        printf("(Ctrl-C to stop)\n");
        jl::PlaybackStats stats;
        jl::PlayVideo(device, videoPath, opts, ShouldAbort, nullptr, nullptr, nullptr, &stats);
        ClearProgressLine();
    }

    return 0;
}

// ---------------------------------------------------------------------------
// If the image path does not work, check checkIsSPI() in main/util/common.js
// against your model string. SPI-class panels take raw RGB565 big-endian with
// NO length header and no checksum:
//
//     for each pixel:  ((r>>3)<<11) | ((g>>2)<<5) | (b>>3)   as u16 BE
//
// The control commands above are identical either way; only the pixel path
// differs.
// ---------------------------------------------------------------------------
