// jl_util.cpp — string, file and framing helpers.

#include "jl_internal.h"

#include <cstdio>

namespace jl {
    namespace detail {

        std::wstring Widen(const std::string& s)
        {
            if (s.empty()) return L"";
            int n = MultiByteToWideChar(CP_UTF8, 0, s.c_str(), (int)s.size(), nullptr, 0);
            std::wstring w(n, 0);
            MultiByteToWideChar(CP_UTF8, 0, s.c_str(), (int)s.size(), &w[0], n);
            return w;
        }

        bool ReadFileBytes(const std::wstring& path, std::vector<uint8_t>& out)
        {
            // MSVC treats _wfopen as deprecated and newer project templates promote
            // that warning to an error, so use the _s variant where it exists.
#ifdef _MSC_VER
            FILE* f = nullptr;
            if (_wfopen_s(&f, path.c_str(), L"rb") != 0 || !f) return false;
#else
            FILE* f = _wfopen(path.c_str(), L"rb");
            if (!f) return false;
#endif
            fseek(f, 0, SEEK_END);
            long n = ftell(f);
            fseek(f, 0, SEEK_SET);
            out.resize(n > 0 ? (size_t)n : 0);
            size_t got = out.empty() ? 0 : fread(&out[0], 1, out.size(), f);
            fclose(f);
            out.resize(got);
            return got > 0;
        }

        namespace {

            // Offset of the SOFn segment's leading FF, or npos. Every SOFn shares
            // one layout: FF Cn | len | precision | height | width | ncomp |
            // (id, sampling, quant table) x ncomp.
            size_t FindSof(const std::vector<uint8_t>& j)
            {
                if (j.size() < 4 || j[0] != 0xFF || j[1] != 0xD8) return std::string::npos;
                size_t i = 2;
                while (i + 9 < j.size()) {
                    if (j[i] != 0xFF) { ++i; continue; }
                    uint8_t marker = j[i + 1];
                    if (marker == 0xD8 || marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7)) { i += 2; continue; }
                    size_t seglen = (size_t)((j[i + 2] << 8) | j[i + 3]);
                    if (marker >= 0xC0 && marker <= 0xCF &&
                        marker != 0xC4 && marker != 0xC8 && marker != 0xCC) {
                        return i;
                    }
                    i += 2 + seglen;
                }
                return std::string::npos;
            }

        }  // namespace

        bool JpegSize(const std::vector<uint8_t>& j, int& w, int& h)
        {
            const size_t i = FindSof(j);
            if (i == std::string::npos) return false;
            h = (j[i + 5] << 8) | j[i + 6];
            w = (j[i + 7] << 8) | j[i + 8];
            return true;
        }

        // The panel's decoder only handles what its vendor app ever feeds it:
        // baseline, three components, 4:2:0. ffmpeg's mjpeg encoder produces
        // exactly that from video (yuv420p in, yuvj420p out) but picks a 4:4:4
        // layout for RGB stills, which the panel renders as garbage. So a JPEG
        // that is not plainly 4:2:0 must go back through ffmpeg rather than
        // taking the as-is shortcut.
        bool JpegIsBaseline420(const std::vector<uint8_t>& j)
        {
            const size_t i = FindSof(j);
            if (i == std::string::npos) return false;
            if (j[i + 1] != 0xC0) return false;      // SOF0 only; progressive is out
            if (i + 18 >= j.size()) return false;    // room for three component specs
            if (j[i + 9] != 3) return false;         // Y, Cb, Cr
            // Sampling factors pack H in the high nibble and V in the low one, so
            // luma 2x2 against chroma 1x1 is 4:2:0.
            return j[i + 11] == 0x22 && j[i + 14] == 0x11 && j[i + 17] == 0x11;
        }

        // FNV-1a over the bytes, rendered as 16 hex digits. Shared by the
        // calibration cache and the frame packs so both derive their keys from
        // exactly the same material and invalidate together.
        std::string Fnv1aHex(const std::string& s)
        {
            uint64_t h = 1469598103934665603ULL;
            for (unsigned char c : s) { h ^= c; h *= 1099511628211ULL; }
            char buf[17];
            sprintf_s(buf, sizeof(buf), "%016llx", (unsigned long long)h);
            return buf;
        }

        uint16_t Sum16(const std::vector<uint8_t>& b, size_t from, size_t to)
        {
            uint32_t s = 0;
            for (size_t i = from; i < to; ++i) s += b[i];
            return static_cast<uint16_t>(s & 0xFFFF);
        }

        std::vector<uint8_t> BuildFrame(uint8_t command, const uint8_t* payload,
            size_t payloadLen)
        {
            const size_t total = payloadLen + 7;
            std::vector<uint8_t> f;
            f.reserve(total);
            f.push_back(0x55);
            f.push_back(0xAA);
            f.push_back(static_cast<uint8_t>(total & 0xFF));
            f.push_back(static_cast<uint8_t>((total >> 8) & 0xFF));
            f.push_back(command);
            if (payloadLen) f.insert(f.end(), payload, payload + payloadLen);

            uint16_t sum = Sum16(f, 0, f.size());
            f.push_back(static_cast<uint8_t>(sum & 0xFF));
            f.push_back(static_cast<uint8_t>(sum >> 8));
            return f;
        }

        std::vector<uint8_t> BuildImageFrame(const std::vector<uint8_t>& jpeg)
        {
            const uint32_t n = static_cast<uint32_t>(jpeg.size());
            std::vector<uint8_t> f;
            f.reserve(jpeg.size() + 6);
            f.push_back(static_cast<uint8_t>(n & 0xFF));
            f.push_back(static_cast<uint8_t>((n >> 8) & 0xFF));
            f.push_back(static_cast<uint8_t>((n >> 16) & 0xFF));
            f.push_back(static_cast<uint8_t>((n >> 24) & 0xFF));
            f.insert(f.end(), jpeg.begin(), jpeg.end());

            uint16_t sum = Sum16(f, 0, f.size());   // covers the length bytes too
            f.push_back(static_cast<uint8_t>(sum & 0xFF));
            f.push_back(static_cast<uint8_t>(sum >> 8));
            return f;
        }

    }  // namespace detail

    bool ValidateOpts(const RenderOpts& opts, std::wstring& error)
    {
        if (opts.rotate % 90 != 0) {
            error = L"rotate must be 0, 90, 180 or 270";
            return false;
        }
        if (opts.fps < 1 || opts.fps > 60) {
            error = L"fps must be 1-60 (this panel refreshes at 30)";
            return false;
        }
        if (opts.quality != 0 && (opts.quality < 2 || opts.quality > 31)) {
            error = L"quality must be 2 (best) to 31 (worst)";
            return false;
        }
        return true;
    }

}  // namespace jl
