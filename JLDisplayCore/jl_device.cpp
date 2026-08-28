// jl_device.cpp — finding the panel, holding its port open, and talking to it.

#include "jl_internal.h"

#include <setupapi.h>
#include <devguid.h>

#include <cwctype>

#pragma comment(lib, "setupapi.lib")

namespace jl {

    namespace {

        const wchar_t* kHwidMatch = L"VID_33C3&PID_7788";

        bool WriteAll(HANDLE h, const uint8_t* data, size_t len)
        {
            size_t off = 0;
            while (off < len) {
                DWORD wrote = 0;
                DWORD chunk = static_cast<DWORD>((len - off > 20480) ? 20480 : (len - off));
                if (!WriteFile(h, data + off, chunk, &wrote, nullptr) || wrote == 0) {
                    Log(LogLevel::Error, L"write failed: %lu", GetLastError());
                    return false;
                }
                off += wrote;
            }
            return true;
        }

        bool WriteAll(HANDLE h, const std::vector<uint8_t>& v)
        {
            return v.empty() ? true : WriteAll(h, &v[0], v.size());
        }

        struct Guard {
            CRITICAL_SECTION& cs;
            explicit Guard(CRITICAL_SECTION& c) : cs(c) { EnterCriticalSection(&cs); }
            ~Guard() { LeaveCriticalSection(&cs); }
            Guard(const Guard&) = delete;
            Guard& operator=(const Guard&) = delete;
        };

    }  // namespace

    std::wstring FindPort()
    {
        std::wstring result;
        HDEVINFO set = SetupDiGetClassDevsW(&GUID_DEVCLASS_PORTS, nullptr, nullptr, DIGCF_PRESENT);
        if (set == INVALID_HANDLE_VALUE) return result;

        SP_DEVINFO_DATA info{};
        info.cbSize = sizeof(info);

        for (DWORD i = 0; SetupDiEnumDeviceInfo(set, i, &info); ++i) {
            wchar_t hwid[1024] = L"";
            SetupDiGetDeviceRegistryPropertyW(set, &info, SPDRP_HARDWAREID, nullptr,
                reinterpret_cast<PBYTE>(hwid), sizeof(hwid), nullptr);
            std::wstring id(hwid);
            for (size_t k = 0; k < id.size(); ++k) id[k] = towupper(id[k]);
            if (id.find(kHwidMatch) == std::wstring::npos) continue;

            HKEY key = SetupDiOpenDevRegKey(set, &info, DICS_FLAG_GLOBAL, 0, DIREG_DEV, KEY_READ);
            if (key == INVALID_HANDLE_VALUE) continue;

            wchar_t portName[64] = L"";
            DWORD size = sizeof(portName);
            if (RegQueryValueExW(key, L"PortName", nullptr, nullptr,
                reinterpret_cast<LPBYTE>(portName), &size) == ERROR_SUCCESS) {
                result = portName;
            }
            RegCloseKey(key);
            if (!result.empty()) break;
        }
        SetupDiDestroyDeviceInfoList(set);
        return result;
    }

    Device::Device()
    {
        InitializeCriticalSection(&cs_);
    }

    Device::~Device()
    {
        Close();
        DeleteCriticalSection(&cs_);
    }

    bool Device::Open(const std::wstring& port, std::wstring& error)
    {
        Guard lock(cs_);
        Close();

        std::wstring name = port;
        if (name.empty()) {
            name = FindPort();
            if (name.empty()) {
                error = L"device not found";
                return false;
            }
        }

        const std::wstring path = L"\\\\.\\" + name;
        HANDLE h = CreateFileW(path.c_str(), GENERIC_READ | GENERIC_WRITE,
            0, nullptr, OPEN_EXISTING, 0, nullptr);
        if (h == INVALID_HANDLE_VALUE) {
            DWORD err = GetLastError();
            wchar_t buf[256];
            // Access denied is by far the most common failure and the least
            // self-explanatory, so name the actual cause rather than the errno.
            if (err == ERROR_ACCESS_DENIED) {
                _snwprintf_s(buf, _countof(buf), _TRUNCATE,
                    L"%s is in use by another program (close the vendor app, "
                    L"the display manager, or any running jl_display first)",
                    name.c_str());
            }
            else {
                _snwprintf_s(buf, _countof(buf), _TRUNCATE,
                    L"open %s failed: %lu", path.c_str(), err);
            }
            error = buf;
            return false;
        }

        DCB dcb{};
        dcb.DCBlength = sizeof(dcb);
        GetCommState(h, &dcb);
        dcb.BaudRate = 115200;      // advisory on CDC-ACM
        dcb.ByteSize = 8;
        dcb.Parity = NOPARITY;
        dcb.StopBits = ONESTOPBIT;
        dcb.fBinary = TRUE;
        dcb.fDtrControl = DTR_CONTROL_ENABLE;
        dcb.fRtsControl = RTS_CONTROL_ENABLE;
        dcb.fOutxCtsFlow = FALSE;
        dcb.fOutxDsrFlow = FALSE;
        dcb.fDsrSensitivity = FALSE;
        dcb.fOutX = FALSE;
        dcb.fInX = FALSE;
        SetCommState(h, &dcb);

        COMMTIMEOUTS to{};
        to.ReadIntervalTimeout = 50;
        to.ReadTotalTimeoutConstant = 500;
        to.WriteTotalTimeoutConstant = 5000;
        SetCommTimeouts(h, &to);

        PurgeComm(h, PURGE_RXCLEAR | PURGE_TXCLEAR);

        h_ = h;
        port_ = name;
        return true;
    }

    void Device::Close()
    {
        Guard lock(cs_);
        if (h_ != INVALID_HANDLE_VALUE) {
            CloseHandle(h_);
            h_ = INVALID_HANDLE_VALUE;
        }
        port_.clear();
    }

    // FF D9 is a JPEG end-of-image marker, used here to flush any partial frame.
    void Device::Clear()
    {
        Guard lock(cs_);
        if (!IsOpen()) return;
        const uint8_t eoi[] = { 0xFF, 0xD9, 0xFF, 0xD9 };
        const uint8_t zero[] = { 0x00, 0x00, 0x00, 0x00 };
        WriteAll(h_, eoi, sizeof(eoi));
        Sleep(50);
        WriteAll(h_, zero, sizeof(zero));
        Sleep(200);
    }

    void Device::FlushEoi()
    {
        Guard lock(cs_);
        if (!IsOpen()) return;
        const uint8_t eoi[] = { 0xFF, 0xD9, 0xFF, 0xD9 };
        WriteAll(h_, eoi, sizeof(eoi));
    }

    bool Device::SendCommand(uint8_t command, const std::vector<uint8_t>& payload)
    {
        Guard lock(cs_);
        if (!IsOpen()) return false;
        std::vector<uint8_t> f = detail::BuildFrame(
            command, payload.empty() ? nullptr : &payload[0], payload.size());
        return WriteAll(h_, f);
    }

    bool Device::SendImageFrame(const std::vector<uint8_t>& jpeg)
    {
        Guard lock(cs_);
        if (!IsOpen()) return false;
        return WriteAll(h_, detail::BuildImageFrame(jpeg));
    }

    bool Device::ReadReply(std::string& body, DWORD timeoutMs)
    {
        Guard lock(cs_);
        if (!IsOpen()) return false;

        std::vector<uint8_t> buf;
        DWORD start = GetTickCount();
        uint8_t tmp[1024];

        while (GetTickCount() - start < timeoutMs) {
            DWORD got = 0;
            if (!ReadFile(h_, tmp, sizeof(tmp), &got, nullptr)) return false;
            if (got) buf.insert(buf.end(), tmp, tmp + got);

            if (buf.size() >= 7 && buf[0] == 0x55 && buf[1] == 0xAA) {
                size_t declared = (size_t)(buf[2] | (buf[3] << 8));
                if (declared >= 7 && buf.size() >= declared) {
                    uint16_t want = static_cast<uint16_t>(buf[declared - 2] | (buf[declared - 1] << 8));
                    uint16_t have = detail::Sum16(buf, 0, declared - 2);
                    if (want != have)
                        Log(LogLevel::Warn, L"warning: checksum %04X, expected %04X", have, want);
                    body.assign(buf.begin() + 5, buf.begin() + declared - 2);
                    return true;
                }
            }
        }
        return false;
    }

    bool Device::SetBrightness(int percent)
    {
        if (percent < 0) percent = 0;
        if (percent > 100) percent = 100;
        std::vector<uint8_t> p{ static_cast<uint8_t>(percent) };
        if (!SendCommand(cmd::SetLight, p)) return false;
        Sleep(100);
        return true;
    }

    bool Device::GetInfo(std::string& out)
    {
        // Held across both halves so a concurrent writer cannot slip a frame in
        // between the request and its reply.
        Guard lock(cs_);
        if (!SendCommand(cmd::GetDeviceInfo)) return false;
        return ReadReply(out);
    }

    bool Device::HoldStill(const std::vector<uint8_t>& jpeg, AbortFn abort, void* abortUser)
    {
        if (!IsOpen()) return false;

        SendCommand(cmd::Live);
        Sleep(100);
        if (!SendImageFrame(jpeg)) return false;

        DWORD last = GetTickCount();
        while (!(abort && abort(abortUser))) {
            Sleep(100);
            if (GetTickCount() - last >= kLiveKeepAliveMs) {
                if (!KeepAlive()) return false;   // live mode lapses without this
                last = GetTickCount();
            }
        }
        return true;
    }

}  // namespace jl
