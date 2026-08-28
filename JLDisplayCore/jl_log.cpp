// jl_log.cpp — the one place the core talks to the outside world about what it
// is doing. Default is silence, so a host that installs no sink gets no output.

#include "jl_core.h"

#include <cstdarg>
#include <cstdio>   // _vsnwprintf_s

namespace jl {

    namespace {
        // Thread-local, not global: a host may run a playback thread and a
        // background calibration at the same time, and their progress lines must
        // not land in each other's status. Each thread installs its own sink and
        // a thread that installs none stays silent.
        thread_local LogFn g_sink = nullptr;
        thread_local void* g_user = nullptr;
    }

    void SetLogSink(LogFn fn, void* user)
    {
        g_sink = fn;
        g_user = user;
    }

    void Log(LogLevel level, const wchar_t* fmt, ...)
    {
        if (!g_sink) return;

        wchar_t buf[1024];
        va_list args;
        va_start(args, fmt);
        // _vsnwprintf_s truncates rather than failing, which is what we want for
        // a diagnostic; a clipped message beats a dropped one.
        _vsnwprintf_s(buf, _countof(buf), _TRUNCATE, fmt, args);
        va_end(args);

        g_sink(level, buf, g_user);
    }

}  // namespace jl
