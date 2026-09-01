// jl_overlay.cpp — blending a rendered overlay onto outgoing frames.
//
// The panel takes whole baseline 4:2:0 JPEGs under kMaxJpegBytes and nothing
// else: no overlay plane, no alpha, no partial update. So anything drawn on top
// of the video costs a decode, a blend and a re-encode of every frame, and this
// is where that happens.
//
// WIC does the codec work. It is in-box, it is fast enough (measured ~0.9 ms
// each way at 960x480), and — the reason it was chosen over GDI+ — its JPEG
// encoder lets the chroma subsampling be pinned. The panel decodes 4:2:0 only
// and renders anything else as a smeared picture rather than reporting an
// error, which is exactly how GIF playback stayed broken for so long. So
// JpegYCrCbSubsampling is set explicitly on every frame and never left to the
// encoder's discretion.

#include "jl_internal.h"

#include <wincodec.h>

#include <algorithm>
#include <atomic>

#pragma comment(lib, "windowscodecs.lib")
#pragma comment(lib, "ole32.lib")

namespace jl {

    namespace {

        // Quality here is WIC's 0..1 ImageQuality, which is unrelated to
        // ffmpeg's -q:v. Measured against this panel's content: 0.85 is about
        // -q:v 3, 0.55 about -q:v 7. Starting at 0.75 leaves the worst realistic
        // case — a high-detail source under the heaviest overlay — at roughly
        // 85% of the cap, which is the headroom the rate control needs to have
        // somewhere to go.
        constexpr float kStartQuality = 0.75f;
        constexpr float kMinQuality = 0.30f;
        constexpr float kMaxQuality = 0.92f;
        constexpr float kQualityStep = 0.07f;
        constexpr float kQualityRecover = 0.02f;

        // Frames comfortably under the cap before quality is nudged back up.
        // Deliberately slow: a busy scene must not make this oscillate.
        constexpr int    kRecoverAfter = 90;
        constexpr size_t kRecoverBelow = (size_t)(0.62 * kMaxJpegBytes);

        constexpr int kMaxEncodeAttempts = 4;

        template <class T> void SafeRelease(T*& p) { if (p) { p->Release(); p = nullptr; } }

        struct Guard {
            CRITICAL_SECTION& cs;
            explicit Guard(CRITICAL_SECTION& c) : cs(c) { EnterCriticalSection(&cs); }
            ~Guard() { LeaveCriticalSection(&cs); }
            Guard(const Guard&) = delete;
            Guard& operator=(const Guard&) = delete;
        };

        double NowMs()
        {
            static LARGE_INTEGER freq = [] {
                LARGE_INTEGER f{}; QueryPerformanceFrequency(&f); return f;
            }();
            LARGE_INTEGER c{};
            QueryPerformanceCounter(&c);
            return freq.QuadPart ? 1000.0 * c.QuadPart / freq.QuadPart : 0.0;
        }

        // A rolling mean that needs no history buffer. The diagnostics only ever
        // want "roughly what is this costing", not a distribution.
        void Roll(double& mean, double sample)
        {
            mean = mean == 0.0 ? sample : mean * 0.95 + sample * 0.05;
        }

        // Per-thread WIC state. The factory is created on the thread that
        // composites and released when it unbinds, so nothing outlives the COM
        // apartment it was made in.
        thread_local IWICImagingFactory* t_factory = nullptr;
        thread_local bool t_ownsCom = false;

    }  // namespace

    // -----------------------------------------------------------------------

    struct Overlay::Impl {
        // Two surfaces and an index. The producer fills the back one outside the
        // lock and swaps under it, so the playback thread never waits on a
        // render and never sees a half-written overlay.
        std::vector<uint8_t> surface[2];
        int                  front = 0;
        CRITICAL_SECTION     cs{};

        std::atomic<bool>     enabled{ false };
        std::atomic<uint32_t> version{ 0 };
        std::atomic<bool>     hasSurface{ false };

        // Bounding box of the front surface's non-zero alpha, so the blend
        // touches only the rows and columns that can possibly change.
        int x0 = 0, y0 = 0, x1 = -1, y1 = -1;

        // Reused across frames: allocating 1.4 MB per frame at 30 fps is the
        // difference between this being cheap and not.
        std::vector<uint8_t> pixels;   // decoded frame, 24bpp BGR
        static constexpr UINT kStride = kPanelWidth * 3;

        float quality = kStartQuality;
        int   goodRun = 0;

        // Once the floor has failed there is no point spending four encodes a
        // frame discovering that again; one is enough to notice it recovering.
        bool atFloor = false;

        Stats stats{};

        Impl()
        {
            InitializeCriticalSection(&cs);
            pixels.resize((size_t)kPanelWidth * kPanelHeight * 3);
            stats.quality = kStartQuality;
        }

        ~Impl() { DeleteCriticalSection(&cs); }

        // Decodes straight to 24bpp BGR — the JPEG-native layout, so the format
        // converter is very nearly a no-op, and the destination needs no alpha
        // channel because video frames are opaque by construction.
        bool Decode(const std::vector<uint8_t>& jpeg)
        {
            IWICStream* stream = nullptr;
            IWICBitmapDecoder* decoder = nullptr;
            IWICBitmapFrameDecode* frame = nullptr;
            IWICFormatConverter* conv = nullptr;
            bool ok = false;

            if (SUCCEEDED(t_factory->CreateStream(&stream)) &&
                SUCCEEDED(stream->InitializeFromMemory(
                    const_cast<BYTE*>(jpeg.data()), (DWORD)jpeg.size())) &&
                SUCCEEDED(t_factory->CreateDecoderFromStream(
                    stream, nullptr, WICDecodeMetadataCacheOnDemand, &decoder)) &&
                SUCCEEDED(decoder->GetFrame(0, &frame)) &&
                SUCCEEDED(t_factory->CreateFormatConverter(&conv)) &&
                SUCCEEDED(conv->Initialize(frame, GUID_WICPixelFormat24bppBGR,
                    WICBitmapDitherTypeNone, nullptr, 0.0,
                    WICBitmapPaletteTypeCustom)))
            {
                UINT w = 0, h = 0;
                if (SUCCEEDED(frame->GetSize(&w, &h)) &&
                    w == (UINT)kPanelWidth && h == (UINT)kPanelHeight)
                {
                    ok = SUCCEEDED(conv->CopyPixels(
                        nullptr, kStride, (UINT)pixels.size(), pixels.data()));
                }
            }

            SafeRelease(conv);
            SafeRelease(frame);
            SafeRelease(decoder);
            SafeRelease(stream);
            return ok;
        }

        // Source-over with a premultiplied source onto an opaque destination:
        //   dst = src + dst * (1 - a)
        void Blend()
        {
            Guard lock(cs);
            if (x1 < 0) return;

            const std::vector<uint8_t>& ov = surface[front];
            if (ov.size() != (size_t)kPanelWidth * kPanelHeight * 4) return;

            for (int y = y0; y <= y1; ++y) {
                const uint8_t* s = &ov[((size_t)y * kPanelWidth + x0) * 4];
                uint8_t* d = &pixels[(size_t)y * kStride + (size_t)x0 * 3];

                for (int x = x0; x <= x1; ++x, s += 4, d += 3) {
                    const unsigned a = s[3];
                    if (!a) continue;
                    if (a == 255) { d[0] = s[0]; d[1] = s[1]; d[2] = s[2]; continue; }
                    const unsigned inv = 255u - a;
                    d[0] = (uint8_t)(s[0] + (d[0] * inv + 127) / 255);
                    d[1] = (uint8_t)(s[1] + (d[1] * inv + 127) / 255);
                    d[2] = (uint8_t)(s[2] + (d[2] * inv + 127) / 255);
                }
            }
        }

        bool EncodeOnce(float q, std::vector<uint8_t>& out)
        {
            IStream* mem = nullptr;
            IWICStream* stream = nullptr;
            IWICBitmapEncoder* encoder = nullptr;
            IWICBitmapFrameEncode* frame = nullptr;
            IPropertyBag2* bag = nullptr;
            bool ok = false;

            if (FAILED(CreateStreamOnHGlobal(nullptr, TRUE, &mem))) return false;

            if (SUCCEEDED(t_factory->CreateStream(&stream)) &&
                SUCCEEDED(stream->InitializeFromIStream(mem)) &&
                SUCCEEDED(t_factory->CreateEncoder(GUID_ContainerFormatJpeg, nullptr, &encoder)) &&
                SUCCEEDED(encoder->Initialize(stream, WICBitmapEncoderNoCache)) &&
                SUCCEEDED(encoder->CreateNewFrame(&frame, &bag)))
            {
                PROPBAG2 opt[2] = {};
                VARIANT  val[2] = {};

                opt[0].pstrName = const_cast<LPOLESTR>(L"ImageQuality");
                val[0].vt = VT_R4;
                val[0].fltVal = q;

                // Not optional. See the file header.
                opt[1].pstrName = const_cast<LPOLESTR>(L"JpegYCrCbSubsampling");
                val[1].vt = VT_UI1;
                val[1].bVal = WICJpegYCrCbSubsampling420;

                bag->Write(2, opt, val);

                WICPixelFormatGUID fmt = GUID_WICPixelFormat24bppBGR;
                if (SUCCEEDED(frame->Initialize(bag)) &&
                    SUCCEEDED(frame->SetSize(kPanelWidth, kPanelHeight)) &&
                    SUCCEEDED(frame->SetPixelFormat(&fmt)) &&
                    SUCCEEDED(frame->WritePixels(kPanelHeight, kStride,
                        (UINT)pixels.size(), pixels.data())) &&
                    SUCCEEDED(frame->Commit()) &&
                    SUCCEEDED(encoder->Commit()))
                {
                    HGLOBAL hg = nullptr;
                    STATSTG st{};
                    if (SUCCEEDED(GetHGlobalFromStream(mem, &hg)) &&
                        SUCCEEDED(mem->Stat(&st, STATFLAG_NONAME)))
                    {
                        const size_t n = (size_t)st.cbSize.QuadPart;
                        if (void* p = GlobalLock(hg)) {
                            out.resize(n);
                            memcpy(out.data(), p, n);
                            GlobalUnlock(hg);
                            ok = true;
                        }
                    }
                }
            }

            SafeRelease(bag);
            SafeRelease(frame);
            SafeRelease(encoder);
            SafeRelease(stream);
            SafeRelease(mem);
            return ok;
        }

        // Rate control. The calibrated -q:v governs what ffmpeg hands us; it
        // says nothing about what this encoder produces, so the cap has to be
        // enforced here.
        bool Encode(std::vector<uint8_t>& out)
        {
            const int attempts = atFloor ? 1 : kMaxEncodeAttempts;

            for (int i = 0; i < attempts; ++i) {
                if (!EncodeOnce(quality, out)) return false;

                if (out.size() <= kMaxJpegBytes) {
                    if (i > 0) ++stats.reencodes;
                    atFloor = false;

                    // Creep back up only after a long clean run, so recovering
                    // from one busy scene cannot start an oscillation.
                    if (out.size() < kRecoverBelow) {
                        if (++goodRun >= kRecoverAfter) {
                            goodRun = 0;
                            quality = (std::min)(kMaxQuality, quality + kQualityRecover);
                        }
                    }
                    else {
                        goodRun = 0;
                    }
                    return true;
                }

                goodRun = 0;
                if (quality <= kMinQuality) break;
                quality = (std::max)(kMinQuality, quality - kQualityStep);
            }

            // Nothing fits even at the floor. Stop paying for four encodes a
            // frame to keep rediscovering that.
            atFloor = true;
            ++stats.drops;
            return false;
        }
    };

    // -----------------------------------------------------------------------

    Overlay::Overlay() : p_(new Impl()) {}

    Overlay::~Overlay() { delete p_; }

    bool Overlay::BindThread()
    {
        if (t_factory) return true;

        // A worker that has already joined an apartment keeps it; only the ones
        // that have not get one here, and only those undo it.
        const HRESULT hr = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
        t_ownsCom = SUCCEEDED(hr) && hr != S_FALSE;
        if (FAILED(hr) && hr != RPC_E_CHANGED_MODE) return false;

        if (FAILED(CoCreateInstance(CLSID_WICImagingFactory, nullptr,
            CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&t_factory))))
        {
            if (t_ownsCom) { CoUninitialize(); t_ownsCom = false; }
            t_factory = nullptr;
            Log(LogLevel::Warn, L"could not start the image compositor; "
                L"the overlay will not be drawn");
            return false;
        }
        return true;
    }

    void Overlay::UnbindThread()
    {
        SafeRelease(t_factory);
        if (t_ownsCom) { CoUninitialize(); t_ownsCom = false; }
    }

    void Overlay::SetEnabled(bool on)
    {
        if (p_->enabled.exchange(on) == on) return;

        // Toggling changes what a frame should look like just as surely as new
        // pixels do, and a held still only rebuilds when the version moves. Skip
        // this and turning the overlay on leaves the still underneath it bare
        // until something else happens.
        p_->version.fetch_add(1);
    }

    bool Overlay::Enabled() const
    {
        return p_->enabled.load() && p_->hasSurface.load();
    }

    uint32_t Overlay::Version() const { return p_->version.load(); }

    bool Overlay::Update(const uint8_t* bgra, int width, int height)
    {
        if (!bgra || width != kPanelWidth || height != kPanelHeight) return false;

        const size_t bytes = (size_t)kPanelWidth * kPanelHeight * 4;
        const int back = 1 - p_->front;

        std::vector<uint8_t>& dst = p_->surface[back];
        dst.resize(bytes);
        memcpy(dst.data(), bgra, bytes);

        // Bounding box of anything not fully transparent. Computed once here so
        // the per-frame blend does not have to scan for it 30 times a second.
        int x0 = kPanelWidth, y0 = kPanelHeight, x1 = -1, y1 = -1;
        for (int y = 0; y < kPanelHeight; ++y) {
            const uint8_t* row = &dst[(size_t)y * kPanelWidth * 4];
            for (int x = 0; x < kPanelWidth; ++x) {
                if (row[(size_t)x * 4 + 3]) {
                    if (x < x0) x0 = x;
                    if (x > x1) x1 = x;
                    if (y < y0) y0 = y;
                    if (y > y1) y1 = y;
                }
            }
        }

        {
            Guard lock(p_->cs);
            p_->front = back;
            p_->x0 = x0; p_->y0 = y0; p_->x1 = x1; p_->y1 = y1;
        }

        p_->hasSurface.store(x1 >= 0);
        p_->version.fetch_add(1);
        return true;
    }

    void Overlay::Clear()
    {
        {
            Guard lock(p_->cs);
            p_->x1 = -1;
            p_->y1 = -1;
        }
        p_->hasSurface.store(false);
        p_->version.fetch_add(1);
    }

    bool Overlay::Compose(std::vector<uint8_t>& jpeg)
    {
        // The path taken whenever nobody is drawing anything. It must stay free.
        if (!Enabled()) return true;

        if (!t_factory && !BindThread()) return true;   // degrade to no overlay

        const double t0 = NowMs();
        if (!p_->Decode(jpeg)) {
            // A frame we cannot decode is one we cannot draw on; sending it
            // untouched is better than dropping it.
            return true;
        }
        p_->Blend();
        const double t1 = NowMs();

        std::vector<uint8_t> out;
        const bool ok = p_->Encode(out);
        const double t2 = NowMs();

        Roll(p_->stats.composeMs, t1 - t0);
        Roll(p_->stats.encodeMs, t2 - t1);
        p_->stats.quality = p_->quality;

        if (!ok) return false;

        jpeg.swap(out);
        return true;
    }

    Overlay::Stats Overlay::Snapshot() const { return p_->stats; }

}  // namespace jl
