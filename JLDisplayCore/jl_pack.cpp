// jl_pack.cpp — turning a source into panel-ready frames once, so ffmpeg does
// not have to run for as long as something is playing.
//
// The panel takes a stream of 960x480 baseline 4:2:0 JPEGs under 80 KB, and
// what ffmpeg produces for a given source is a pure function of that source's
// bytes and the render options. The calibration cache already relies on that;
// this file takes it the rest of the way and stores the frames themselves.
//
// A pack is deliberately dull:
//
//     header | frame blobs | index
//
// Identical frames share one blob and the index points at it more than once,
// which collapses GIFs and static footage to a fraction of their nominal size.
// The file is mapped rather than read, so a long video costs address space
// instead of working set.

#include "jl_internal.h"

#include <cstdio>
#include <cstdlib>
#include <algorithm>
#include <atomic>

namespace jl {

    namespace {

        constexpr uint32_t kPackVersion = 1;

        // Set from the host, read by whichever worker is building. Atomic rather
        // than locked: a change lands on the next pack, and a build already
        // running keeps the figure it started with.
        std::atomic<uint64_t> g_memoryBudget{ kDefaultMemoryBudget };
        std::atomic<uint64_t> g_diskBudget{ kDefaultDiskBudget };

        // How many recent unique frames stay in RAM for duplicate detection.
        // Bounded on purpose: a disk pack may be far larger than memory allows,
        // and the duplicates worth catching — a static shot, a short cycling
        // animation — are always near each other. 256 x 80 KB is the ceiling.
        constexpr size_t kDedupeWindow = 256;

#pragma pack(push, 1)
        struct PackHeader {
            char     magic[4];        // "JLP1"
            uint32_t version;
            uint32_t fps;
            uint32_t width;
            uint32_t height;
            uint32_t quality;         // the -q:v every frame was encoded at
            uint32_t frameCount;      // entries in the index
            uint32_t uniqueCount;     // distinct blobs actually stored
            uint32_t reserved;
            uint64_t sourceSize;
            uint64_t sourceMtime;
            uint64_t indexOffset;
            char     key[16];         // the same digest the filename carries
        };

        struct PackIndexEntry {
            uint64_t offset;
            uint32_t length;
        };
#pragma pack(pop)

        static_assert(sizeof(PackHeader) == 76, "pack header layout changed");
        static_assert(sizeof(PackIndexEntry) == 12, "pack index layout changed");

        // -------------------------------------------------------------------
        // Where packs live
        // -------------------------------------------------------------------

        std::wstring PackDirectory()
        {
            std::wstring dir = detail::CacheDirectory() + L"\\packs";
            CreateDirectoryW(dir.c_str(), nullptr);
            return dir;
        }

        std::wstring PackPath(const std::string& key)
        {
            std::wstring wide(key.begin(), key.end());
            return PackDirectory() + L"\\" + wide + L".jlp";
        }

        std::wstring StillPath(const std::string& key)
        {
            std::wstring wide(key.begin(), key.end());
            return PackDirectory() + L"\\" + wide + L".jlf";
        }

        // Everything that changes the bytes we would send: which file, what it
        // contained, how it is framed, how fast, and at what quality. The same
        // material as the calibration key, plus the two things calibration does
        // not care about — the frame rate, and the quality it settled on.
        std::string PackKey(const std::wstring& input, const std::wstring& filter,
            int fps, int quality)
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

            char extra[128];
            sprintf_s(extra, sizeof(extra), "|%llu|%llu|%d|%d|%d|%d|pack%u",
                bytes, mtime, fps, quality, kPanelWidth, kPanelHeight, kPackVersion);
            material += extra;

            return detail::Fnv1aHex(material);
        }

        // A still is the same question with no frame rate: one JPEG, one filter.
        std::string StillKey(const std::wstring& input, const std::wstring& filter)
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
            sprintf_s(extra, sizeof(extra), "|%llu|%llu|%d|%d|still1",
                bytes, mtime, kPanelWidth, kPanelHeight);
            material += extra;

            return detail::Fnv1aHex(material);
        }

        // NTFS stops updating last-access times by default, so a pack's
        // last-WRITE time is stamped forward every time it is used. That makes
        // eviction a real least-recently-used rather than oldest-built-first.
        void Touch(const std::wstring& path)
        {
            HANDLE h = CreateFileW(path.c_str(), FILE_WRITE_ATTRIBUTES,
                FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
            if (h == INVALID_HANDLE_VALUE) return;
            FILETIME now{};
            GetSystemTimeAsFileTime(&now);
            SetFileTime(h, nullptr, nullptr, &now);
            CloseHandle(h);
        }

        struct CacheEntry {
            std::wstring name;
            uint64_t     bytes = 0;
            uint64_t     stamp = 0;
        };

        std::vector<CacheEntry> ScanCache()
        {
            std::vector<CacheEntry> out;
            const std::wstring dir = PackDirectory();

            WIN32_FIND_DATAW fd{};
            HANDLE find = FindFirstFileW((dir + L"\\*").c_str(), &fd);
            if (find == INVALID_HANDLE_VALUE) return out;

            do {
                if (fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) continue;

                std::wstring name = fd.cFileName;
                size_t dot = name.find_last_of(L'.');
                if (dot == std::wstring::npos) continue;
                std::wstring ext = name.substr(dot);
                if (ext != L".jlp" && ext != L".jlf") continue;

                CacheEntry e;
                e.name = name;
                e.bytes = ((uint64_t)fd.nFileSizeHigh << 32) | fd.nFileSizeLow;
                e.stamp = ((uint64_t)fd.ftLastWriteTime.dwHighDateTime << 32)
                    | fd.ftLastWriteTime.dwLowDateTime;
                out.push_back(e);
            } while (FindNextFileW(find, &fd));

            FindClose(find);
            return out;
        }

        // Deletes least-recently-used packs until the cache fits, never touching
        // `keep` — which is the one just written and about to be mapped.
        void EnforceDiskBudget(const std::wstring& keep)
        {
            std::vector<CacheEntry> entries = ScanCache();

            const uint64_t budget = DiskBudget();

            uint64_t total = 0;
            for (const CacheEntry& e : entries) total += e.bytes;
            if (total <= budget) return;

            std::sort(entries.begin(), entries.end(),
                [](const CacheEntry& a, const CacheEntry& b) { return a.stamp < b.stamp; });

            const std::wstring dir = PackDirectory();
            for (const CacheEntry& e : entries) {
                if (total <= budget) break;
                if (keep.find(e.name) != std::wstring::npos) continue;
                if (DeleteFileW((dir + L"\\" + e.name).c_str())) {
                    total -= e.bytes;
                    Log(LogLevel::Info, L"pack cache: evicted %s", e.name.c_str());
                }
            }
        }

        // -------------------------------------------------------------------
        // Building
        // -------------------------------------------------------------------

        // Where a build puts its bytes: straight into a vector, or straight into
        // the file it is about to become.
        struct Sink {
            std::vector<uint8_t>* memory = nullptr;
            FILE* file = nullptr;
            uint64_t offset = 0;

            bool Write(const uint8_t* data, size_t len)
            {
                if (memory) {
                    memory->insert(memory->end(), data, data + len);
                }
                else if (file) {
                    if (fwrite(data, 1, len, file) != len) return false;
                }
                offset += len;
                return true;
            }
        };

        // A bounded ring of recently written unique frames, so a repeat can be
        // pointed at the copy already stored rather than stored again.
        class DedupeWindow {
        public:
            const FramePack::Ref* Find(uint64_t hash, const std::vector<uint8_t>& frame) const
            {
                for (const Entry& e : ring_) {
                    if (e.hash != hash || e.bytes.size() != frame.size()) continue;
                    if (memcmp(e.bytes.data(), frame.data(), frame.size()) == 0) return &e.ref;
                }
                return nullptr;
            }

            void Add(uint64_t hash, const FramePack::Ref& ref, const std::vector<uint8_t>& frame)
            {
                if (ring_.size() < kDedupeWindow) {
                    ring_.push_back({ hash, ref, frame });
                    return;
                }
                ring_[next_] = { hash, ref, frame };
                next_ = (next_ + 1) % kDedupeWindow;
            }

        private:
            struct Entry {
                uint64_t             hash;
                FramePack::Ref       ref;
                std::vector<uint8_t> bytes;
            };
            std::vector<Entry> ring_;
            size_t             next_ = 0;
        };

        uint64_t HashFrame(const std::vector<uint8_t>& f)
        {
            uint64_t h = 1469598103934665603ULL;
            for (uint8_t c : f) { h ^= c; h *= 1099511628211ULL; }
            return h;
        }

        struct BuildResult {
            std::vector<FramePack::Ref> index;
            size_t unique = 0;
            size_t dropped = 0;
            bool   overBudget = false;
            bool   cancelled = false;
        };

        // Runs ffmpeg once, flat out, and feeds every frame into `sink`. This is
        // the calibration survey's loop with the encode kept instead of thrown
        // away — no -re, no wall-clock pacing, no device.
        bool RunBuild(const std::wstring& ffmpeg, const std::wstring& input,
            const std::wstring& filter, const std::wstring& hwaccel,
            int fps, int quality, Sink& sink, size_t budget,
            AbortFn abort, void* abortUser, BuildResult& result)
        {
            detail::FfmpegPipe pipe;
            std::wstring cl = detail::VideoCommand(ffmpeg, input, filter, hwaccel,
                fps, quality, false, false, 0);
            if (!detail::StartFfmpegPipe(cl, pipe)) return false;

            DedupeWindow window;
            std::vector<uint8_t> acc, frame;
            const DWORD began = GetTickCount();
            DWORD lastPrint = 0;
            bool  formatChecked = false;
            bool  wroteFailed = false;

            while (detail::ReadNextJpeg(pipe, acc, frame)) {
                if (abort && abort(abortUser)) { result.cancelled = true; break; }

                // Calibration should have made this impossible, but a frame the
                // panel would refuse must not go into a pack that outlives this
                // run and replays it forever.
                if (frame.size() > kMaxJpegBytes) { ++result.dropped; continue; }

                if (!formatChecked) {
                    formatChecked = true;
                    if (!detail::JpegIsBaseline420(frame))
                        Log(LogLevel::Warn, L"frames are not baseline 4:2:0 - "
                            L"the panel will draw them wrong");
                }

                const uint64_t hash = HashFrame(frame);
                if (const FramePack::Ref* seen = window.Find(hash, frame)) {
                    result.index.push_back(*seen);
                }
                else {
                    if (budget && sink.offset + frame.size() > budget) {
                        result.overBudget = true;
                        break;
                    }
                    FramePack::Ref ref{ sink.offset, (uint32_t)frame.size() };
                    if (!sink.Write(frame.data(), frame.size())) { wroteFailed = true; break; }
                    window.Add(hash, ref, frame);
                    result.index.push_back(ref);
                    ++result.unique;
                }

                DWORD now = GetTickCount();
                if (now - lastPrint >= 250) {
                    lastPrint = now;
                    Log(LogLevel::Progress, L"  preprocessing: %zu frames, %llu MB, %.1fs",
                        result.index.size(),
                        (unsigned long long)(sink.offset / (1024 * 1024)),
                        (now - began) / 1000.0);
                }
            }

            pipe.Close();
            return !wroteFailed;
        }

        // -------------------------------------------------------------------
        // The prepared-still cache
        //
        // Small, in-process, and shared by both preprocess modes. Disk mode puts
        // a copy on disk as well; this sits in front of it so a playlist coming
        // back round does not even re-read the file.
        // -------------------------------------------------------------------

        constexpr size_t kStillCacheEntries = 32;

        CRITICAL_SECTION& StillLock()
        {
            static CRITICAL_SECTION cs = [] {
                CRITICAL_SECTION c;
                InitializeCriticalSection(&c);
                return c;
            }();
            return cs;
        }

        struct StillEntry {
            std::string          key;
            std::vector<uint8_t> jpeg;
        };

        std::vector<StillEntry>& Stills()
        {
            static std::vector<StillEntry> v;
            return v;
        }

        bool StillCacheGet(const std::string& key, std::vector<uint8_t>& out)
        {
            EnterCriticalSection(&StillLock());
            bool found = false;
            for (const StillEntry& e : Stills()) {
                if (e.key == key) { out = e.jpeg; found = true; break; }
            }
            LeaveCriticalSection(&StillLock());
            return found;
        }

        void StillCachePut(const std::string& key, const std::vector<uint8_t>& jpeg)
        {
            EnterCriticalSection(&StillLock());
            std::vector<StillEntry>& v = Stills();
            bool present = false;
            for (const StillEntry& e : v) {
                if (e.key == key) { present = true; break; }
            }
            if (!present) {
                if (v.size() >= kStillCacheEntries) v.erase(v.begin());
                v.push_back({ key, jpeg });
            }
            LeaveCriticalSection(&StillLock());
        }

    }  // namespace

    // -----------------------------------------------------------------------
    // FramePack
    // -----------------------------------------------------------------------

    FramePack::~FramePack()
    {
        Close();
    }

    void FramePack::Close()
    {
        if (view_) { UnmapViewOfFile(view_); view_ = nullptr; }
        if (mapping_) { CloseHandle(mapping_); mapping_ = nullptr; }
        if (file_ != INVALID_HANDLE_VALUE) { CloseHandle(file_); file_ = INVALID_HANDLE_VALUE; }
        owned_.clear();
        owned_.shrink_to_fit();
        index_.clear();
        base_ = nullptr;
        bytes_ = 0;
    }

    const uint8_t* FramePack::Frame(size_t i, size_t& len) const
    {
        if (!base_ || i >= index_.size()) { len = 0; return nullptr; }
        len = index_[i].length;
        return base_ + index_[i].offset;
    }

    void FramePack::AdoptMemory(std::vector<uint8_t>&& blob, std::vector<Ref>&& index, int fps)
    {
        Close();
        owned_ = std::move(blob);
        index_ = std::move(index);
        fps_ = fps > 0 ? fps : 30;
        bytes_ = owned_.size();
        base_ = owned_.empty() ? nullptr : owned_.data();
    }

    bool FramePack::AdoptFile(const std::wstring& path)
    {
        Close();

        // FILE_SHARE_DELETE so clearing the cache while this is playing does not
        // fail; the mapping keeps the bytes alive until playback lets go.
        HANDLE h = CreateFileW(path.c_str(), GENERIC_READ,
            FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
            nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (h == INVALID_HANDLE_VALUE) return false;

        LARGE_INTEGER size{};
        if (!GetFileSizeEx(h, &size) || size.QuadPart < (LONGLONG)sizeof(PackHeader)) {
            CloseHandle(h);
            return false;
        }

        HANDLE m = CreateFileMappingW(h, nullptr, PAGE_READONLY, 0, 0, nullptr);
        if (!m) { CloseHandle(h); return false; }

        const uint8_t* view = (const uint8_t*)MapViewOfFile(m, FILE_MAP_READ, 0, 0, 0);
        if (!view) { CloseHandle(m); CloseHandle(h); return false; }

        auto fail = [&] {
            UnmapViewOfFile(view);
            CloseHandle(m);
            CloseHandle(h);
            return false;
        };

        PackHeader head{};
        memcpy(&head, view, sizeof(head));

        if (memcmp(head.magic, "JLP1", 4) != 0)    return fail();
        if (head.version != kPackVersion)          return fail();
        if (head.width != kPanelWidth)             return fail();
        if (head.height != kPanelHeight)           return fail();
        if (head.frameCount == 0)                  return fail();

        // The digest is in the filename too, and the filename is how this was
        // found. Checking both means a half-written or renamed file cannot be
        // mistaken for the pack that was actually asked for.
        std::string stem;
        size_t slash = path.find_last_of(L"\\/");
        size_t dot = path.find_last_of(L'.');
        if (slash != std::wstring::npos && dot != std::wstring::npos && dot > slash) {
            for (size_t i = slash + 1; i < dot; ++i) stem += (char)(path[i] & 0xFF);
        }
        if (stem.size() != 16 || memcmp(head.key, stem.data(), 16) != 0) return fail();

        const uint64_t indexBytes = (uint64_t)head.frameCount * sizeof(PackIndexEntry);
        if (head.indexOffset < sizeof(PackHeader)) return fail();
        if (head.indexOffset + indexBytes > (uint64_t)size.QuadPart) return fail();

        std::vector<Ref> index;
        index.reserve(head.frameCount);
        for (uint32_t i = 0; i < head.frameCount; ++i) {
            PackIndexEntry e{};
            memcpy(&e, view + head.indexOffset + (uint64_t)i * sizeof(e), sizeof(e));
            if (e.length == 0 || e.length > kMaxJpegBytes)  return fail();
            if (e.offset < sizeof(PackHeader))              return fail();
            if (e.offset + e.length > head.indexOffset)     return fail();
            index.push_back(Ref{ e.offset, e.length });
        }

        file_ = h;
        mapping_ = m;
        view_ = view;
        base_ = view;
        index_ = std::move(index);
        fps_ = (int)head.fps;
        bytes_ = (size_t)size.QuadPart;
        return true;
    }

    // -----------------------------------------------------------------------
    // GetPack
    // -----------------------------------------------------------------------

    bool GetPack(const std::wstring& path, const RenderOpts& opts, Preprocess mode,
        FramePack& pack, AbortFn abort, void* abortUser, PackStats* stats)
    {
        auto aborted = [&] { return abort && abort(abortUser); };

        if (mode == Preprocess::Off) return false;

        const std::wstring ffmpeg = FindFfmpeg();
        if (ffmpeg.empty()) {
            Log(LogLevel::Error, L"ffmpeg.exe not found; preprocessing needs it");
            return false;
        }

        RenderOpts resolved = opts;
        resolved.hwaccel = detail::ResolveHwaccel(ffmpeg, opts.hwaccel);

        const std::wstring filter = detail::BuildFilter(resolved.stretch, resolved.rotate);

        // The quality is part of what the frames ARE, so it has to be settled
        // before the key can name them. On anything but the very first run this
        // is a lookup in the calibration table, not a survey.
        const int quality = ResolveQuality(path, resolved, abort, abortUser);
        if (quality < 0) return false;
        if (aborted()) return false;

        const std::string  key = PackKey(path, filter, resolved.fps, quality);
        const std::wstring packPath = PackPath(key);

        if (stats) *stats = PackStats{};

        // Already built on a previous run.
        if (mode == Preprocess::Disk && pack.AdoptFile(packPath)) {
            Touch(packPath);
            Log(LogLevel::Info, L"preprocessed: %zu frames from cache, %zu MB",
                pack.FrameCount(), pack.Bytes() / (1024 * 1024));
            if (stats) {
                stats->frames = pack.FrameCount();
                stats->bytes = pack.Bytes();
                stats->fromCache = true;
            }
            return true;
        }

        const DWORD began = GetTickCount();
        Log(LogLevel::Info, L"preprocessing at -q:v %d...", quality);

        BuildResult result;

        if (mode == Preprocess::Memory) {
            std::vector<uint8_t> blob;
            Sink sink;
            sink.memory = &blob;

            const uint64_t budget = MemoryBudget();

            if (!RunBuild(ffmpeg, path, filter, resolved.hwaccel, resolved.fps, quality,
                sink, (size_t)budget, abort, abortUser, result)) {
                return false;
            }
            if (result.cancelled || aborted()) return false;

            if (result.overBudget) {
                Log(LogLevel::Warn,
                    L"too large to hold in memory (over %llu MB) - streaming instead",
                    (unsigned long long)(budget / (1024 * 1024)));
                return false;
            }
            if (result.index.empty()) {
                Log(LogLevel::Error, L"preprocessing produced no frames");
                return false;
            }

            const size_t bytes = blob.size();
            pack.AdoptMemory(std::move(blob), std::move(result.index), resolved.fps);

            if (stats) {
                stats->frames = pack.FrameCount();
                stats->uniqueFrames = result.unique;
                stats->bytes = bytes;
                stats->buildSeconds = (GetTickCount() - began) / 1000.0;
            }
        }
        else {
            // Written under a temporary name and moved into place, so a crash or
            // a cancel partway through cannot leave something that looks like a
            // finished pack.
            wchar_t suffix[32];
            _snwprintf_s(suffix, _countof(suffix), _TRUNCATE, L".%lu.tmp", GetCurrentProcessId());
            const std::wstring tempPath = packPath + suffix;

            FILE* f = nullptr;
#ifdef _MSC_VER
            if (_wfopen_s(&f, tempPath.c_str(), L"wb") != 0 || !f) {
#else
            f = _wfopen(tempPath.c_str(), L"wb");
            if (!f) {
#endif
                Log(LogLevel::Warn, L"could not write a pack file - streaming instead");
                return false;
            }

            PackHeader head{};
            memcpy(head.magic, "JLP1", 4);
            head.version = kPackVersion;
            head.fps = (uint32_t)resolved.fps;
            head.width = kPanelWidth;
            head.height = kPanelHeight;
            head.quality = (uint32_t)quality;
            memcpy(head.key, key.data(), 16);

            WIN32_FILE_ATTRIBUTE_DATA fad{};
            if (GetFileAttributesExW(path.c_str(), GetFileExInfoStandard, &fad)) {
                head.sourceSize = ((uint64_t)fad.nFileSizeHigh << 32) | fad.nFileSizeLow;
                head.sourceMtime = ((uint64_t)fad.ftLastWriteTime.dwHighDateTime << 32)
                    | fad.ftLastWriteTime.dwLowDateTime;
            }

            // A placeholder for now, rewritten once the counts are known.
            fwrite(&head, 1, sizeof(head), f);

            Sink sink;
            sink.file = f;
            sink.offset = sizeof(head);

            bool built = RunBuild(ffmpeg, path, filter, resolved.hwaccel, resolved.fps,
                quality, sink, 0, abort, abortUser, result);

            bool good = built && !result.cancelled && !aborted() && !result.index.empty();

            if (good) {
                head.indexOffset = sink.offset;
                head.frameCount = (uint32_t)result.index.size();
                head.uniqueCount = (uint32_t)result.unique;

                for (const FramePack::Ref& r : result.index) {
                    PackIndexEntry e{ r.offset, r.length };
                    if (fwrite(&e, 1, sizeof(e), f) != sizeof(e)) { good = false; break; }
                }
            }

            if (good) {
                fseek(f, 0, SEEK_SET);
                good = fwrite(&head, 1, sizeof(head), f) == sizeof(head);
            }

            fclose(f);

            if (!good) {
                DeleteFileW(tempPath.c_str());
                if (result.cancelled || aborted()) return false;
                Log(LogLevel::Warn, L"could not finish the pack - streaming instead");
                return false;
            }

            DeleteFileW(packPath.c_str());
            if (!MoveFileW(tempPath.c_str(), packPath.c_str())) {
                DeleteFileW(tempPath.c_str());
                Log(LogLevel::Warn, L"could not store the pack - streaming instead");
                return false;
            }

            EnforceDiskBudget(packPath);

            if (!pack.AdoptFile(packPath)) {
                Log(LogLevel::Warn, L"the pack just written would not load - streaming instead");
                return false;
            }

            if (stats) {
                stats->frames = pack.FrameCount();
                stats->uniqueFrames = result.unique;
                stats->bytes = pack.Bytes();
                stats->buildSeconds = (GetTickCount() - began) / 1000.0;
            }
        }

        Log(LogLevel::Info,
            L"preprocessed: %zu frames (%zu distinct), %zu MB, %.1fs - ffmpeg is done",
            pack.FrameCount(), result.unique, pack.Bytes() / (1024 * 1024),
            (GetTickCount() - began) / 1000.0);

        if (result.dropped)
            Log(LogLevel::Warn, L"%zu frames were over the panel's limit and left out",
                result.dropped);

        return true;
    }

    // -----------------------------------------------------------------------
    // PlayPack
    // -----------------------------------------------------------------------

    bool PlayPack(Device& device, const FramePack& pack, const RenderOpts& opts,
        AbortFn abort, void* abortUser,
        FrameFn onFrame, void* frameUser,
        PlaybackStats* stats)
    {
        auto aborted = [&] { return abort && abort(abortUser); };

        if (!device.IsOpen()) {
            Log(LogLevel::Error, L"device is not open");
            return false;
        }
        if (!pack.IsOpen()) {
            Log(LogLevel::Error, L"no preprocessed frames to play");
            return false;
        }

        device.SendCommand(cmd::Live);
        Sleep(100);

        Log(LogLevel::Info, L"playing %zu preprocessed frames at %d fps%s...",
            pack.FrameCount(), pack.Fps(), opts.loop ? L", looping" : L"");

        detail::Pacer pace;
        pace.Start(pack.Fps());

        size_t sent = 0, passes = 0;
        bool ok = true;
        bool running = true;

        // One buffer for the whole run: SendImageFrame and the preview callback
        // both want a vector, and reusing this one keeps the per-frame cost to a
        // copy rather than an allocation.
        std::vector<uint8_t> scratch;
        scratch.reserve(kMaxJpegBytes);

        while (running) {
            ++passes;

            for (size_t i = 0; i < pack.FrameCount() && running; ++i) {
                if (aborted()) { running = false; break; }

                size_t len = 0;
                const uint8_t* bytes = pack.Frame(i, len);
                if (!bytes || len == 0) continue;

                scratch.assign(bytes, bytes + len);

                DWORD now = pace.WaitForSlot();

                if (!device.SendImageFrame(scratch)) { ok = false; running = false; break; }
                ++sent;

                if (onFrame) onFrame(scratch, frameUser);

                if (!pace.KeepAliveIfDue(device, now)) { ok = false; running = false; break; }

                if ((sent % 30) == 0) {
                    double secs = (now - pace.startedAt) / 1000.0;
                    Log(LogLevel::Progress, L"%zu frames, %.1f fps (preprocessed)",
                        sent, secs > 0 ? sent / secs : 0.0);
                }
            }

            if (!opts.loop || aborted()) running = false;
        }

        const double elapsed = (GetTickCount() - pace.startedAt) / 1000.0;

        Log(LogLevel::Info, L"%zu frames sent over %zu pass(es), no ffmpeg", sent, passes);

        if (stats) {
            stats->sent = sent;
            stats->dropped = 0;   // a pack cannot hold a frame the panel would refuse
            stats->passes = passes;
            stats->fps = elapsed > 0 ? sent / elapsed : 0.0;
        }

        device.FlushEoi();
        return ok;
    }

    // -----------------------------------------------------------------------
    // Stills
    // -----------------------------------------------------------------------

    bool PrepareImageCached(const std::wstring& path, const RenderOpts& opts,
        Preprocess mode, std::vector<uint8_t>& jpeg)
    {
        if (mode == Preprocess::Off) return PrepareImage(path, opts, jpeg);

        const std::wstring filter = detail::BuildFilter(opts.stretch, opts.rotate);
        const std::string  key = StillKey(path, filter);

        if (StillCacheGet(key, jpeg)) return true;

        if (mode == Preprocess::Disk) {
            const std::wstring stillPath = StillPath(key);
            if (detail::ReadFileBytes(stillPath, jpeg) && jpeg.size() <= kMaxJpegBytes) {
                Touch(stillPath);
                StillCachePut(key, jpeg);
                Log(LogLevel::Info, L"prepared still from cache, %zu KB", jpeg.size() / 1024);
                return true;
            }
            jpeg.clear();
        }

        if (!PrepareImage(path, opts, jpeg)) return false;

        StillCachePut(key, jpeg);

        if (mode == Preprocess::Disk) {
            const std::wstring stillPath = StillPath(key);
            FILE* f = nullptr;
#ifdef _MSC_VER
            if (_wfopen_s(&f, stillPath.c_str(), L"wb") == 0 && f) {
#else
            f = _wfopen(stillPath.c_str(), L"wb");
            if (f) {
#endif
                fwrite(jpeg.data(), 1, jpeg.size(), f);
                fclose(f);
                EnforceDiskBudget(stillPath);
            }
        }

        return true;
    }

    // -----------------------------------------------------------------------
    // Cache management
    // -----------------------------------------------------------------------

    void SetMemoryBudget(uint64_t bytes)
    {
        if (bytes == 0) bytes = kDefaultMemoryBudget;
        if (bytes < kMinMemoryBudget) bytes = kMinMemoryBudget;

        // A memory pack is addressed as one contiguous allocation, so on a
        // 32-bit build there is a hard ceiling no setting may cross.
        if (bytes > (uint64_t)SIZE_MAX / 2) bytes = (uint64_t)SIZE_MAX / 2;

        g_memoryBudget.store(bytes);
    }

    void SetDiskBudget(uint64_t bytes)
    {
        if (bytes == 0) bytes = kDefaultDiskBudget;
        if (bytes < kMinDiskBudget) bytes = kMinDiskBudget;
        g_diskBudget.store(bytes);
    }

    uint64_t MemoryBudget() { return g_memoryBudget.load(); }
    uint64_t DiskBudget() { return g_diskBudget.load(); }

    uint64_t PackCacheBytes()
    {
        uint64_t total = 0;
        for (const CacheEntry& e : ScanCache()) total += e.bytes;
        return total;
    }

    void PackCacheClear()
    {
        const std::wstring dir = PackDirectory();
        for (const CacheEntry& e : ScanCache())
            DeleteFileW((dir + L"\\" + e.name).c_str());

        EnterCriticalSection(&StillLock());
        Stills().clear();
        LeaveCriticalSection(&StillLock());
    }

}  // namespace jl
