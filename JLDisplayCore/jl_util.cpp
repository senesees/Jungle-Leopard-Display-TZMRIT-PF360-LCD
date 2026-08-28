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

        bool JpegSize(const std::vector<uint8_t>& j, int& w, int& h)
        {
            if (j.size() < 4 || j[0] != 0xFF || j[1] != 0xD8) return false;
            size_t i = 2;
            while (i + 9 < j.size()) {
                if (j[i] != 0xFF) { ++i; continue; }
                uint8_t marker = j[i + 1];
                if (marker == 0xD8 || marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7)) { i += 2; continue; }
                size_t seglen = (size_t)((j[i + 2] << 8) | j[i + 3]);
                if (marker >= 0xC0 && marker <= 0xCF &&
                    marker != 0xC4 && marker != 0xC8 && marker != 0xCC) {
                    h = (j[i + 5] << 8) | j[i + 6];
                    w = (j[i + 7] << 8) | j[i + 8];
                    return true;
                }
                i += 2 + seglen;
            }
            return false;
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
