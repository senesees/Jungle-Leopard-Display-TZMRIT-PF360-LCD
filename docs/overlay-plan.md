# Overlay layers — implementation plan

Draw live system statistics on top of whatever is playing: CPU/GPU load and
temperature, memory, disk, network, clocks, fans, plus bars and gauges, all
positioned and styled by the user.

Status: **planned, not started.** This document is the design; delete it or fold
it into `README.md` once the feature ships.

---

## 1. The constraint that shapes everything

The panel accepts exactly one thing: a stream of 960×480 baseline 4:2:0 JPEGs
under `kMaxJpegBytes` (80 KB), re-sent continuously because it draws frame *N*
only when frame *N+1* arrives. There is no hardware overlay plane, no partial
update, no alpha channel, no text primitive.

So "on top of the video" can only mean **decode → composite → re-encode, per
frame**. Nothing else is available. Three consequences follow, and they drive
every decision below:

1. **The core needs a JPEG codec it does not currently have.** Today every JPEG
   is produced by ffmpeg as a child process; nothing in `JLDisplayCore` decodes
   or encodes an image in-process. WIC (`windowscodecs.dll`, in-box) fills that
   gap — critically, it can *pin* 4:2:0 subsampling
   (`WICJpegYCrCbSubsampling420`), which the panel requires and which GDI+ will
   silently abandon at high quality.
2. **The calibrated `-q:v` stops governing the wire size.** Our re-encode does.
   That means an adaptive quality loop with a hard cap, described in §4.3.
3. **It costs real CPU per frame.** Budget in §7; it must be provably free when
   no overlay is enabled.

---

## 2. Architecture

```
  JLDisplayManager (C#)                     JLDisplayCore / Native (C++)
  ─────────────────────                     ────────────────────────────
  SensorRegistry ─┐
  OverlayProfile ─┼─> OverlayRenderer
  (POCO layers)   │   (dedicated STA thread,
                  │    DrawingVisual → RTB)
                  │            │
                  │            │ 960×480 BGRA, premultiplied
                  │            v
                  │   jl_overlay_update() ──> overlay slot
                  │                           (double-buffered + version)
                  │                                    │
  OverlayEditor ──┘                                    v
  paints its canvas with the           frame ─> WIC decode to BGRA
  SAME static DrawLayer()                    ─> alpha blend overlay
  ⇒ WYSIWYG by construction                  ─> WIC encode (4:2:0, adaptive q)
                                             ─> Device::SendImageFrame
                                             ─> onFrame (preview sees it too)
```

Two properties fall out for free:

- The manager's existing live preview reads `jl_get_last_frame`, which is now
  post-composite — the preview stays truthful with no extra work.
- The overlay is composited in **panel space**, i.e. after the host-side
  rotation ffmpeg already applies. Text is therefore always upright relative to
  the glass regardless of how the pump head is mounted, which is what you want.

### Deliberate non-goal

`Jungle Leopard Display.exe` (the CLI) gets **no overlay**. The renderer is WPF,
and the CLI is native and deliberately dependency-free. Stated up front so it
does not read as an oversight; §11 notes what it would take to change.

---

## 3. Native: the compose hook

One new concept in `jl_core.h`, threaded through the three places a frame
reaches the panel.

```cpp
// Composites the current overlay onto a panel-ready frame, in place, and
// re-encodes it under the panel's size cap. Returns false if the frame could
// not be produced at any acceptable quality, in which case the caller drops it
// exactly as it drops an oversized one today.
//
// Called on the playback thread, once per frame. An implementation with
// nothing to draw must return true having touched nothing — that is the path
// taken whenever no overlay is enabled, and it must cost nothing.
using ComposeFn = bool (*)(std::vector<uint8_t>& jpeg, void* user);
```

Threading through:

| Function | File | Change |
|---|---|---|
| `PlayVideo` | `jl_player.cpp` | new trailing `ComposeFn compose, void* composeUser` params, defaulted to `nullptr`. Call it after `ReadNextJpeg`, before `SendImageFrame`. |
| `PlayPack` | `jl_pack.cpp` | same; call it on `scratch` after `assign`, before `SendImageFrame`. |
| `Device::HoldStill` | `jl_device.cpp` | same, plus an overlay-version check so a still recomposites only when the overlay actually changed (see §4.4). |

`PlayVideo` needs one ordering change: today it drops a frame larger than
`kMaxJpegBytes` *before* sending. With a compose hook active that check moves
**after** compositing, since it is our encoder, not ffmpeg's, that decides the
final size. An oversized intermediate is still perfectly decodable.

`formatChecked` / `JpegIsBaseline420` likewise now validates our encoder's
output rather than ffmpeg's. That is the assertion that catches a WIC
configuration mistake immediately instead of as a mangled picture.

### New file: `JLDisplayCore/jl_overlay.cpp`

Owns the codec. Public surface in `jl_core.h`:

```cpp
// A 960×480 BGRA surface with premultiplied alpha, plus a version that the
// producer bumps on every change. Compositing is skipped entirely while
// `enabled` is false, so an unused overlay costs one atomic load per frame.
class Overlay {
public:
    void SetEnabled(bool on);
    bool Enabled() const;

    // Replaces the surface. Rejects anything that is not kPanelWidth ×
    // kPanelHeight. Safe from any thread; the playback thread never blocks on
    // a producer mid-update.
    bool Update(const uint8_t* bgraPremultiplied, int width, int height);
    void Clear();

    uint32_t Version() const;

    // The ComposeFn implementation. Decodes, blends, re-encodes.
    bool Compose(std::vector<uint8_t>& jpeg);

    struct Stats { double composeMs, encodeMs; float quality; uint32_t reencodes, drops; };
    Stats Snapshot() const;
};
```

Implementation notes that matter:

- **COM.** The playback thread must `CoInitializeEx(nullptr, COINIT_MULTITHREADED)`
  and create its own `IWICImagingFactory`. Do this at thread entry in
  `WorkerProc`, not per frame.
- **Reuse everything.** One decoded-BGRA scratch buffer, one BGRA staging
  buffer, one `IWICStream` over a growable `IStream` for the encode. Per-frame
  allocation at 30 fps is the difference between this being cheap and not.
- **Blend.** Source-over with premultiplied alpha, per row, in the overlay's
  dirty bounding box only. Fully-transparent rows are skipped. A typical
  overlay touches under 15% of the frame, so the blend itself is noise next to
  the codec.
- **Link** `windowscodecs.lib` and `ole32.lib`; add the file to
  `JLDisplayCore.vcxproj` and `.vcxproj.filters`.

---

## 4. Native: correctness details

### 4.1 Double buffering

The producer (C#, ~10 Hz) and the consumer (playback thread, 30 Hz) must never
tear or block each other. Two `std::vector<uint8_t>` slots plus an index and a
version counter under a small critical section; `Update` fills the back buffer
outside the lock and swaps under it. The compose path takes the lock only to
read the index, then reads the front buffer lock-free — the producer never
touches the front buffer.

### 4.2 Zero cost when off

`Compose` returns immediately on `!enabled`. No decode, no encode, no
allocation. Turning the feature off must restore byte-identical behaviour to
today, and that is a thing to actually verify by comparing frames, not assume.

### 4.3 Adaptive quality (rate control)

The calibrated `-q:v` still governs what ffmpeg hands us; it no longer governs
what reaches the panel. So:

- Track a working quality `q` in `[0.30, 0.92]`, **starting at 0.75**.
- Encode. If `size > kMaxJpegBytes`, drop `q` by 0.07 and retry, up to 3 times.
  Remember the reduced `q` as a ceiling.
- If `size < 0.62 × kMaxJpegBytes` for 90 consecutive frames, nudge `q` up by
  0.02. Slow recovery, so a busy scene does not oscillate.
- If even `q = 0.30` overflows, drop the frame — the existing `dropped`
  behaviour, and the counter the UI already shows. **Back off to one encode
  attempt per frame** once the floor has failed, until something changes;
  otherwise unencodable content burns four encodes a frame forever.

> **The 0.75 start is measured, not guessed** (§9, phase 0). WIC's `ImageQuality`
> and ffmpeg's `-q:v` are unrelated scales, and the naive 0.85 lands far too
> high: on real frames it re-encodes *larger* than the source and leaves only
> 800 bytes of headroom under the cap with a heavy overlay. The measured
> equivalences on this panel's content:
>
> | WIC `ImageQuality` | roughly equals |
> |---|---|
> | 0.85 | ffmpeg `-q:v 3` |
> | 0.75 | ffmpeg `-q:v 4–5` |
> | 0.55 | ffmpeg `-q:v 7` |
>
> At 0.75 the worst case measured — a high-detail source re-encoded with the
> heaviest overlay — peaks at **69.6 KB against the 80 KB cap (85%)**, with the
> adaptive loop never firing. That is the headroom this design needs.

**Double compression is real and accepted.** Video frames are encoded by ffmpeg
at the calibrated quality, then re-encoded by us. The alternative — asking
ffmpeg for near-lossless intermediates whenever an overlay is on — removes the
generation loss but makes packs several times larger, invalidates the pack and
calibration cache keys, and breaks the "a video calibrated in one program is
instant in the other" property that `AGENTS.md` calls out. **Keep the caches
untouched and accept the mild loss.** The panel is 960×480 at arm's length on a
pump head; this is not where quality is lost.

### 4.4 Stills

`HoldStill` re-sends the same bytes every `kStillRefreshMs` (250 ms). With an
overlay it must instead:

- decode the base JPEG **once** and keep the BGRA,
- recomposite and re-encode only when `Overlay::Version()` has changed,
- otherwise re-send the last composited bytes unchanged.

So a still with a clock on it costs one encode per second, not four, and a
still with a static overlay costs nothing after the first frame.

### 4.5 API additions (`jl_api.h` / `jl_api.cpp`)

```c
JLAPI void    jl_overlay_set_enabled(int32_t on);
JLAPI int32_t jl_overlay_update(const uint8_t* bgraPremultiplied, int32_t w, int32_t h);
JLAPI void    jl_overlay_clear(void);
```

and four fields appended to `JlStatus`, so the existing 250 ms poll carries the
diagnostics with no extra P/Invoke:

```c
    double  overlayComposeMs;   // rolling mean, decode + blend
    double  overlayEncodeMs;    // rolling mean
    int32_t overlayQuality;     // working q × 100
    int32_t overlayReencodes;   // frames that needed a second encode
```

> **`AGENTS.md` rule.** `JlStatus` is *blitted*, not marshalled. Appending
> those 24 bytes takes it from **1144 to 1168**. Update the C# struct in
> `Interop/NativeMethods.cs` **and** the expected size in `VerifyLayout()` in
> the same commit, or startup will (correctly) throw.

---

## 5. Managed: the layer model

Plain POCOs — no WPF types in the model, because the renderer runs on its own
thread and WPF objects have thread affinity. Persisted to a **new file**,
`%LOCALAPPDATA%\JungleLeopardDisplay\overlays.json`, so a profile can be
exported and shared without dragging `settings.json` along.

```
Models/Overlay/
  OverlaySettings.cs   enabled, activeProfileId, renderHz, sensorPollMs, providers
  OverlayProfile.cs    id, name, List<OverlayLayer>
  OverlayLayer.cs      abstract base + [JsonDerivedType] polymorphism
  TextLayer.cs  BarLayer.cs  GaugeLayer.cs  ShapeLayer.cs  ImageLayer.cs
```

### Common to every layer

| Property | Notes |
|---|---|
| `Id`, `Name`, `Enabled`, `Locked` | `Locked` is editor-only: ignored while dragging |
| `Anchor` | 9-point (TopLeft … BottomRight) — an overlay anchored bottom-right stays put if the layout is ever rescaled |
| `X`, `Y`, `Width`, `Height` | pixels in panel space, offset from the anchor |
| `Rotation`, `Opacity` | degrees; 0–1 |
| `Z` | list order is z-order; the layer list is the stack |
| `VisibleWhen` | `Always` \| `Playing` \| `Idle` \| `SensorAbove(source, value)` — so a temperature warning can appear only when it matters |

### `TextLayer`

Template string with tokens: `CPU {cpu.load:0}%  {cpu.temp:0}°C`.

Font family, size, weight, style, stretch; letter spacing; line height;
horizontal + vertical alignment; foreground (solid or linear gradient);
**outline** (width + colour); **shadow** (offset, blur, colour); **background
pill** (fill, corner radius, padding, border); `MaxWidth` with wrap or ellipsis.

Token format is `{source:format}` where `format` is a standard .NET numeric
format string — `{gpu.vram.used:0.0}`, `{time.now:HH\:mm}`. An unknown source
renders as `--`, never as an exception and never as the raw token.

**Threshold colouring**: an optional list of `(atOrAbove, colour)` stops
evaluated against a named source, so text goes amber at 70 °C and red at 85 °C.
The same mechanism is shared by `BarLayer` and `GaugeLayer`.

### `BarLayer`

Source + `Min`/`Max` (or `Auto` for a source that declares its own range).
Orientation horizontal/vertical, and `Direction` so a bar can fill right-to-left
or bottom-to-top. Track: fill, corner radius, border, inset. Fill: solid,
linear gradient, or threshold stops. `Segments` (0 = continuous) with
`SegmentGap` for the classic blocky VU look. Optional inline label with its own
template and placement (inside-start / inside-end / above / below).

### `GaugeLayer`

The arc. Start angle, sweep angle, thickness, round or flat caps, track and
fill as above, optional tick marks every *n* units, optional centred text
template. This is the layer that suits a 960×480 pump head best and is worth
getting right.

### `ShapeLayer`

Rect / rounded rect / ellipse / line. Fill, stroke, corner radius. The backing
panels and dividers that make a stat cluster read as designed rather than as
floating text.

### `ImageLayer`

A static PNG (icons, logos, frames), stretch mode and opacity. Copied into
`%LOCALAPPDATA%\JungleLeopardDisplay\overlay-assets\` on add, so a profile stays
portable and does not break when the source file moves.

---

## 6. Managed: sensors

```
Services/Sensors/
  SensorRegistry.cs      name → SensorReading; snapshot for the render thread
  SensorReading.cs       value, unit, min, max, timestamp, stale
  ISensorProvider.cs
  PdhProvider.cs                   built-in
  SystemProvider.cs                built-in
  NvmlProvider.cs                  built-in (NVIDIA)
  LibreHardwareMonitorProvider.cs  optional
  HwInfoProvider.cs                optional
```

Providers are polled on a pool timer (`sensorPollMs`, default 1000, floor 250)
and write into the registry. The render thread reads an immutable snapshot, so
sensors and rendering never contend.

**Smoothing.** Each numeric source keeps an exponential moving average with a
configurable time constant (default 400 ms), so bars glide instead of jumping.
Per-layer opt-out for anything that must show the raw value.

### Built-in tier — zero setup, always present

| Source | Mechanism |
|---|---|
| `cpu.load`, `cpu.load.coreN` | PDH `\Processor Information(_Total)\% Processor Utility` |
| `cpu.clock` | PDH `\Processor Information(_Total)\% Processor Performance` × base clock |
| `mem.used` / `mem.total` / `mem.percent` | `GlobalMemoryStatusEx` |
| `gpu.load` | PDH `\GPU Engine(*engtype_3D)\Utilization Percentage`, summed — vendor-agnostic |
| `disk.read`, `disk.write`, `disk.activity` | PDH `\PhysicalDisk(_Total)\*` |
| `net.up`, `net.down` | PDH `\Network Interface(*)\Bytes Sent/Received/sec` |
| `sys.uptime`, `time.now`, `date.today` | `GetTickCount64`, `DateTime` |
| `battery.percent`, `battery.charging` | `GetSystemPowerStatus` |
| `gpu.temp`, `gpu.hotspot`, `gpu.load`, `gpu.vram.*`, `gpu.power`, `gpu.clock`, `gpu.fan` | **NVML** — `nvml.dll`, present in `System32` on this machine (RTX 5070). Straight P/Invoke, ships with the driver |
| `media.title`, `media.position`, `media.duration`, `media.fps`, `media.dropped` | the manager's own `DisplayService` |

PDH is `pdh.dll` via P/Invoke — `PdhOpenQuery` / `PdhAddEnglishCounterW` /
`PdhCollectQueryData`. **English** counter names, so a non-English Windows does
not break it.

### Optional tier — for CPU temperature

Windows exposes no reliable CPU **die** temperature without a kernel driver.
`MSAcpi_ThermalZoneTemperature` is usually a chipset zone, frequently absent,
and misleading when present. So CPU temp, motherboard sensors and fans come
from a helper the user already runs, or not at all:

- **LibreHardwareMonitor** — enable its web server, then
  `GET http://localhost:8085/data.json`. Raw `HttpClient` + `System.Text.Json`.
- **HWiNFO** — the `Global\HWiNFO_SENS_SM2` shared memory block, read with
  `MemoryMappedFile`. Documented layout, no dependency.

Both honour the no-NuGet rule. Neither is required: a source with no provider
reads `--`, the layer still draws, and Settings says plainly which provider is
supplying what and which are offline. Sources appear as `cpu.temp`,
`cpu.package.power`, `fan.N`, `mb.temp` under whichever provider answers first,
in a user-ordered preference list.

---

## 7. Managed: the renderer

`Services/Overlay/OverlayRenderer.cs` — one **static** method is the whole
contract:

```csharp
// The single source of truth for what a layer looks like. Called by the
// render thread to produce the panel bitmap, and by the editor canvas to
// paint the design surface. One implementation, so WYSIWYG is structural
// rather than something to keep in sync by hand.
public static void DrawLayer(DrawingContext dc, OverlayLayer layer, SensorSnapshot values);
```

`Services/Overlay/OverlayService.cs` owns a **dedicated STA thread with its own
`Dispatcher`**, not the UI thread. Two reasons: the tray app must not hitch
while rendering, and the overlay has to keep updating while the main window is
hidden — which is the normal state of this app.

Loop, at `renderHz` (default 10, cap 30):

1. Take a sensor snapshot.
2. Compare against the last frame's *rendered* values. If nothing a visible
   layer depends on has changed, **skip** — no render, no `jl_overlay_update`,
   no recomposite. A static overlay over a still settles to zero work.
3. `DrawingVisual` → `RenderTargetBitmap(960, 480, 96, 96, Pbgra32)` →
   `CopyPixels` into a pooled `byte[]` → `jl_overlay_update`.

`Pbgra32` is already premultiplied, which is exactly what the native blend
wants — no conversion pass.

### Performance budget — **measured**, phase 0

720 frames of real 960×480 content per row, RTX 5070 machine, `/O2`:

| Stage | Rate | Measured |
|---|---|---|
| Sensor poll | 1 Hz | negligible; NVML and PDH are both sub-ms |
| Overlay render (C#) | ≤10 Hz | **2.8–3.4 ms**, on its own thread |
| WIC decode (native) | 30 Hz | **0.84–1.05 ms** |
| Blend (native) | 30 Hz | **0.02 / 0.18 / 0.30 ms** (minimal / typical / heavy) |
| WIC encode (native) | 30 Hz | **0.84–0.91 ms** |

**Total on the playback thread: 1.7–2.2 ms against a 33.3 ms budget — 5–7%.**
Three to four times better than the estimate this plan was written on; WIC is
much faster than assumed, and the blend is nearly free because it is confined
to the overlay's dirty box. The C# render at 2.8–3.4 ms is the single most
expensive step, which is exactly why it belongs off the playback thread and at
10 Hz rather than 30.

The deferred optimisation below is therefore **not needed** and should not be
built:

> ~~Composite frame *N+1* during the pacer's sleep rather than after it.~~
> At 2 ms of a 33 ms budget there is nothing to win, and it would complicate
> the seek and abort paths for no measurable gain.

---

## 8. Managed: the editor

`Views/OverlayEditorWindow.xaml` — a canvas, a layer list, a properties panel.

**Canvas.** 960×480 at 1×, with 0.5× / 1× / 1.5× zoom. Backdrop selectable:
the live panel preview (`jl_get_last_frame`, so you design against what is
actually playing), a still from the library, a solid colour, or a checkerboard.
Direct manipulation: drag to move, eight resize handles, rotate handle. Snapping
to an 8 px grid, to panel edges and centres, and to other layers' edges and
centres, with the alignment guides drawn while dragging. Arrow keys nudge 1 px,
Shift+arrow 10 px. Marquee select, multi-select move, align/distribute.

**Layer list.** Reorder by drag (list order *is* z-order), visibility toggle,
lock, duplicate, delete, rename. Right-click to add a layer of any type.

**Properties panel.** Switches on the selected layer's type. Every numeric
field is also draggable. Colours use a picker with alpha and a recents strip.

**Live values while designing.** The editor canvas paints with the *same*
`SensorSnapshot` the renderer uses, so bars move and clocks tick in the editor.

**Token picker.** A searchable list of every source with its live value beside
it; clicking inserts `{gpu.temp:0}` at the caret. This is what makes the text
layer usable without documentation.

**Profiles.** A dropdown at the top: switch, new, duplicate, rename, delete,
import, export. The active profile is also switchable from the tray menu, which
is where it will actually get used.

Entry points: an **Overlay** button beside **AI** and **Settings** in the header
of `MainWindow.xaml`, and an **Overlay** submenu on the tray icon with a
top-level on/off toggle plus the profile list.

---

## 9. Phases

Each phase ends in a state worth committing. `AGENTS.md` is right that there are
no tests — every phase is verified by building and by looking at the panel.

| # | Scope | Done when |
|---|---|---|
| **0** | **Spike.** ✅ **Done — passed.** WIC decode → blend → encode 4:2:0 at 960×480, over 720 real frames × several overlay weights and source qualities. See §9.1. | ✅ 1.7–2.2 ms/frame (5–7% of budget), zero `JpegIsBaseline420` failures, adaptive loop converges in one re-encode, **and the panel draws a WIC-encoded composite cleanly on real hardware**. |
| **1** | ✅ **Done.** `jl_overlay.cpp`, the `Compositor` hook through `PlayVideo`/`PlayPack`/`HoldStill`, the three new exports, `JlStatus` → 1168 + `VerifyLayout`. Verified by a throwaway C# harness driving the DLL directly. See §9.2. | ✅ Overlay draws on a streamed video, a pack and a held still; overlay off is byte-identical; 22/22 harness checks pass. |
| **2** | ✅ **Done.** `SensorRegistry` + `PdhProvider`, `SystemProvider`, `NvmlProvider`, cross-checked against `typeperf`. See §9.3. | ✅ 51 sensors registered, 50 reporting real values; network verified against the reference tool to 0.3%. |
| **3** | ✅ **Done.** Layer model, `DrawLayer`, `OverlayRenderer`, `OverlayService`, `Palette`, `TokenFormatter`, `overlays.json`, wired into `App.xaml.cs`. No editor yet — profiles are hand-edited JSON. See §9.4. | ✅ All five layer types render correctly on the panel over live video, driven by real sensors, at a sustained 30 fps. |
| **4** | ✅ **Done.** `OverlayCanvas`, `PropertyPanel`, `TokenPickerWindow`, `PromptWindow`, `OverlayEditorWindow`; header button and tray submenu. See §9.5. | ✅ Layers can be added, duplicated, removed, dragged, snapped and restyled entirely through the UI; the panel follows live. |
| **5** | Threshold colouring, `VisibleWhen`, smoothing, segmented bars, gauge ticks. Two or three shipped preset profiles. | The presets look good enough to be the screenshot in the README. |
| **6** | `LibreHardwareMonitorProvider`, `HwInfoProvider`, provider preference UI in Settings. See §10. | CPU temperature works with either helper, and degrades to `--` with neither. |
| **7** ✅ | Docs: README section, `AGENTS.md` entries for the overlay slot and the new `VerifyLayout` size, release notes. | Done. Both documents had the stale 1128 size; it is 1168. |

Phases 1–3 are the risky ones. 4 is the largest by volume but the
best-understood. 5–7 are additive and safely deferrable.

### 9.1 Phase 0 results

Spike lives outside the repo (throwaway). Two halves, matching the real design:
a WPF renderer emitting premultiplied BGRA, and a native WIC compositor.

**Timing** — see §7. Comfortably inside budget, by a wider margin than planned.

**Format** — zero `JpegIsBaseline420` failures across every run. Pinning
`JpegYCrCbSubsampling` to `WICJpegYCrCbSubsampling420` in the encoder's property
bag is what does it, and it is not optional: this is the same failure mode as
the GIF 4:4:4 bug, which the panel reports as a smeared picture rather than an
error.

**Size** — the number that changed the design. Overlay cost is modest, about
**+8–10 KB (≈15%)** for the heaviest dashboard tested. The real hazard was the
starting quality, not the overlay: WIC 0.85 re-encodes a `q:v 7` source from
43 KB up to 69 KB, and with a heavy overlay peaks at 81126 bytes — **794 bytes
under the cap.** Dropping the start to 0.75 fixes it (§4.3).

| Source | Overlay | Peak at q=0.75 | vs 80 KB cap |
|---|---|---|---|
| `q:v 7` | none | 57.1 KB | 70% |
| `q:v 7` | typical | 63.8 KB | 78% |
| `q:v 7` | heavy | 65.4 KB | 80% |
| `q:v 3` (high detail) | heavy | 69.6 KB | 85% |

**Adaptive loop** — started deliberately high at 0.90 on the hardest set: one
re-encode, settled at 0.76, then 720 frames with no further overshoot.

**Floor** — pure noise (unencodable at any quality, and already unplayable
today since calibration rejects it) drops every frame cleanly with no crash.
That run is what surfaced the one-attempt back-off now in §4.3.

**Panel (phase 0)** — the decisive check, because this hardware rejects a bad JPEG by
drawing a smeared picture rather than by failing. A 65 KB composited frame was
sent through `Jungle Leopard Display.exe --image`, which logged
`input already 960x480 JPEG … using as-is` — confirming ffmpeg was skipped and
the panel received WIC's bytes verbatim. It drew clean and sharp: correct
colours (no channel swap) and legible small text (no chroma bleed). **WIC's
encoder is confirmed panel-compatible on real hardware**, which is the fact the
whole plan rested on.

### 9.2 Phase 1 results

A throwaway C# harness (outside the repo) drove `JLDisplayNative.dll` directly —
no manager involved — over all three frame paths. 22 checks, all passing:

| What | Result |
|---|---|
| Still, overlay **off** | Frame published is **byte-identical to the source JPEG**, and the compositor reports having done no work at all |
| Still, overlay **on** | Recomposites in place: 63.6 KB, baseline 4:2:0, q=75 |
| Held still, static overlay | **Zero** rebuilds over 3 s — the version check works; without it this would be four encodes a second forever |
| Streamed video (`preprocess off`) | Draws correctly, no drops, compose 1.96 ms / encode 1.13 ms |
| Preprocessed pack (`preprocess memory`) | Draws correctly, compose 1.51 ms / encode 1.02 ms |
| Toggle **mid-playback** | Takes effect on the next frame without restarting the item |
| **Sustained frame rate, heavy overlay** | **30.33 fps off, 30.33 fps on — no measurable cost.** compose+encode = 8% of the frame budget |

Two design corrections came out of building it, both now in the code:

1. **The hook is installed unconditionally**, not when the overlay happens to be
   on as the item starts. Deciding it per item meant enabling the overlay during
   a two-hour video would do nothing until the next one. Costing nothing when
   off is `Overlay::Compose`'s job — two atomic loads — not a per-item decision.
2. **`HoldStill` reports its composited frame** through a `FrameFn`. Without it
   the preview would show a still *without* its overlay, quietly breaking the
   "the preview shows what is really on the glass" property claimed in §2. It
   deliberately does not go through the video path's `OnFrame`, which counts
   frames sent and computes an fps a still has never reported.

Also corrected: `PlayPack` no longer hard-codes `stats.dropped = 0`. That was
true when a pack could only contain frames the panel would accept; a compositor
can now re-encode one past the cap.

### 9.3 Phase 2 results

**51 sensors registered, 50 reporting.** The one that does not is
`battery.percent`, correctly unavailable on a desktop — it renders `--` rather
than a misleading zero, which is the whole point of `SensorReading.Available`.

Verified on this machine (RTX 5070, 24 logical cores, 128 GB):

| Provider | Status | Supplies |
|---|---|---|
| `NvmlProvider` | up | `gpu.temp` 34 °C, `gpu.power` 23 W, `gpu.vram.used` 2.18 / 11.94 GB, core and memory clocks, fan, utilisation |
| `PdhProvider` | up | `cpu.load` + all 24 `cpu.load.coreN`, `cpu.clock` 4.24 GHz, disk read/write/activity, `net.up`/`net.down` |
| `SystemProvider` | up | `mem.*` 20.6 / 127.9 GB, uptime, clock, date, battery |

**Provider precedence works.** Both NVML and PDH describe `gpu.load` and
`gpu.vram.used`; NVML is registered first and keeps them, with PDH's engine-sum
version suppressed. That is what makes the same profile work on an AMD machine,
where PDH takes over.

**Network validated against `typeperf`**, reading the same counter over the same
window: **8.829 MB/s measured against the reference tool's 8.799 — a ratio of
1.003.** Individual samples differ because the two tools tick on independent
clocks, so a burst lands in adjacent rows; the totals are what agree.

That check is worth recording for a reason. `net.down` read `0.00` through
several earlier attempts and looked like a clear bug, but every one of them was
a broken test rather than broken code:

- `Start-Job` downloads completed instantly without transferring anything, so
  there was no traffic to measure.
- Comparing the provider's *instantaneous* 1 Hz sample against a *windowed
  average* made correct readings look wrong.
- Summing `NetworkInterface.GetIPv4Statistics()` over every adapter reported
  47 MB/s on a 0.9 MB/s link, because it counts virtual and VPN adapters.

The lesson for later phases: on a machine this quiet, an idle counter and a
broken counter look identical, and a bad oracle is worse than no oracle.

**Design notes.** Smoothing is an EMA with a 400 ms time constant, computed from
the *actual* interval since the last tick rather than the nominal one, so a late
tick does not smooth as if it were on time. The first reading for a source lands
whole instead of easing up from zero. `sys.uptime`, `time.now` and `date.today`
are text sources, formatted at the provider so `{time.now}` needs no format
string to be useful.

### 9.4 Phase 3 results

The whole pipeline now runs as designed: sensors → layers → `DrawLayer` →
`RenderTargetBitmap` → `jl_overlay_update` → the compositor → the glass.

**On the panel**, the shipped default profile over a playing video, with live
values and no drops:

| | |
|---|---|
| Sustained frame rate | **30.0 fps** with the overlay on |
| Native cost | compose 1.97 ms, encode 1.09 ms, 0 drops, q=75 |
| Held still | recomposites once, then holds — 61.4 KB |
| Mid-playback toggle | off and back on without interrupting the item |

**Dirty detection earns its keep.** Over 10 s at 10 Hz the renderer did **11
renders and skipped 80** — roughly one render per second, matching the sensor
poll rate rather than the tick rate. The signature is built from *rendered text
and quantised fractions*, not raw sensor values, which is the reason it works:
smoothed values drift continuously and never settle, but `62°C` stays `62°C`,
and a bar whose fill moved a fifth of a pixel has not changed in any sense a
viewer can detect.

**Headless checks** on every layer type at once, through the same `DrawLayer`
the panel uses: text with outline, shadow and background pill; continuous,
gradient, segmented, vertical and reversed bars; gauges with ticks and round
caps; shapes; images; rotation and opacity; threshold recolouring; and a
`SensorAbove` layer correctly staying hidden at 34 °C against an 80 °C
threshold. The polymorphic JSON round-trip preserves all 15 layers and all five
types.

Robustness, verified rather than assumed: an unknown sensor renders `--` (not
zero, not the raw token, not an exception); a malformed format string falls back
to the default; `{{` escapes. A layer that throws is caught individually so the
rest of the profile still draws.

**One correction from seeing it run.** The first default profile showed
`CPU 47% --°C`, because it referenced `cpu.temp`, which nothing supplies until
phase 6. A shipped default that reads `--` out of the box looks broken rather
than looking like a feature waiting to be switched on, so it now shows
`{cpu.clock}` instead — a sensor the built-in tier always has.

### 9.5 Phase 4 results

The editor is a canvas, a layer list and a properties pane. The canvas paints
through `OverlayRenderer.DrawProfile` — the same call the panel's renderer makes
— so it is WYSIWYG structurally rather than by maintenance, and it repaints at
5 Hz against live sensors so clocks tick and bars move while you design.

Verified through the running app by driving the real UI: add a gauge (7→8
layers), duplicate it (→9), remove twice (→7), each change persisted to
`overlays.json`; the enable toggle survives a restart; and the manager's own
preview shows the composited overlay over a preprocessed video at **30.2 fps**.

**Four bugs found by running it, all fixed:**

1. **`NullReferenceException` on open.** `SnapBox`'s `IsChecked="True"` in XAML
   raises `Checked` *during* `InitializeComponent`, before the generated field
   assignments have run, so the handler touched a null `Canvas`. Every such
   handler now tolerates being called mid-construction.
2. **"Specified element is already the logical child of another element."** The
   `Row()` helper reparented controls into a Grid *before* detaching them from
   the panel, briefly giving each two logical parents. Detach first.
3. **Every ComboBox rendered blank.** String items render through a generated
   `TextBlock` that picks up the app's implicit `TextBlock` style, and that
   explicit setter beats an inherited `Foreground` — so setting `Foreground` on
   the ComboBox does nothing. `App.xaml` already carried the fix, a `ComboText`
   `DataTemplate`, with a comment describing this exact trap.
4. **Two buttons labelled "Delete"** — one for profiles, one for layers. The
   layer one is now "Remove".

**Two design faults the screenshots exposed:**

- **Dragging wrote `overlays.json` on every mouse-move.** `LayerChanged` fires
  per mouse-move event and was calling `Save()`. Split in two: `LayerChanged`
  pushes pixels to the panel and nothing else, and a new `EditCommitted` — mouse
  released, or a nudge key — is the only thing that writes the file.
- **The live backdrop double-drew the overlay.** `jl_get_last_frame` is taken
  *after* compositing, so with the overlay on the preview already contains the
  layers, and the canvas drew them again, compounding every translucent fill.
  The canvas now skips `DrawProfile` when the backdrop already includes it —
  and still draws normally over plain video when the overlay is off, which is
  how a profile gets designed before being switched on.

### 9.6 Post-phase-4 fixes

Two problems found by using it on a real, non-zero-rotation panel.

**The overlay ignored the mounting rotation.** See §11 — the plan's stated
reasoning was wrong, not just the code. Layers are now drawn in viewer space and
turned into the panel buffer alongside the video. Verified for all four
mountings: `DesignSize` returns 480×960 at 90 and 270, and a corner-marker
profile maps exactly onto the buffer with each corner where it belongs. On the
real 180° panel the preview now shows the overlay inverted *with* the video,
which is what upright on the glass looks like in buffer space.

**The editor was still upside down.** Rotating the *layers* was only half of it.
The canvas's live backdrop is a copy of the panel's buffer, which is
deliberately pre-turned, so on a rotated mounting the video behind the design
surface arrived inverted — the one place in the app where the picture is wrong,
and the worst possible place for it.

`UnrotateBackdrop` turns it back into design space before drawing. The image is
drawn at the buffer's own size and *then* turned, rather than stretched into the
design rect first: at 90 and 270 those differ in aspect, and stretching first
would squash the picture before rotating it. Only a real panel buffer is turned
— a still or a flat colour is already in design space, which is what
`BackdropIsPanelBuffer` distinguishes.

This also keeps the "backdrop already includes the overlay" path correct: with
the overlay on, un-rotating the buffer turns the baked-in layers upright along
with the video.

Verified on the real 180° panel — video, readouts, clock and selection handles
all upright — and at 90°, where the canvas correctly becomes portrait (480×960)
with upright text. Layers and hit-testing were already in design space, so only
the backdrop needed the turn.

**Layer shortcuts.** `Delete`/`Backspace` removes, `Ctrl+D` duplicates,
`Ctrl+↑`/`Ctrl+↓` reorders (plain arrows already nudge), `Ctrl+H` hides,
`Ctrl+L` locks, `Tab`/`Shift+Tab` cycles the stack — useful for a layer buried
under another — and `Esc` deselects. Handled at window level so they work
whether the canvas or the layer list has focus.

The guard is the important part and is tested: with a text box or combo focused,
the shortcuts stand down. Typing a template and losing the layer to a stray
Delete would be an unforgivable way to lose work. Confirmed — `Delete` in the
Name field edited the text and left the layer count untouched.

**One deliberate default.** The canvas opens at 75% rather than 1:1, so the
whole 960-wide panel fits the column at the default window size. Opening onto a
design surface that needs scrolling before it can be seen is a poor first
impression.

---

## 10. The optional sensor tier

### Why CPU temperature needs a helper

Measured on the machine this was built against — an AMD Ryzen 9 5900X:

| Source | Result |
|---|---|
| `MSAcpi_ThermalZoneTemperature` (WMI) | **"Not supported"** |
| `\Thermal Zone Information(*)\Temperature` (PDH) | **"No valid counters"** |

So Windows genuinely cannot answer the question. Ryzen die temperature (Tctl/
Tdie) lives behind the SMU and is reachable only from ring 0, which is why
LibreHardwareMonitor ships a signed kernel driver. Shipping one here would mean
admin rights, antivirus attention, and an end to "the release is three
binaries".

The honest alternative is to read what a monitor the user already runs has
already measured. Two providers, both raw — no package, nothing bundled:

- **`LibreHardwareMonitorProvider`** — HTTP to its web server on
  `localhost:8085/data.json`, walked as a tree. `System.Text.Json` only.
- **`HwInfoProvider`** — the `Global\HWiNFO_SENS_SM2` shared memory block, read
  through `MemoryMappedFile` against HWiNFO's published SDK layout. Cheaper than
  the HTTP path: no request, no parse, a mapped view read in place.

`HardwareNames` maps both onto the same ids, because they report the same
silicon under different names — LHM's "Core (Tctl/Tdie)" and HWiNFO's "CPU
(Tctl/Tdie)" are both `cpu.temp`, and a profile must not care which is running.

**The list is curated, not complete.** HWiNFO alone exposes several hundred
readings, and every one would land in the AI's system prompt, which is already
~1400 tokens. Eleven sensors people actually put on a panel — CPU temp and
power, hottest core, GPU hotspot, motherboard, three chassis fans, CPU fan, and
the pump and coolant readings an AIO owner wants — are worth more than all of
them.

### What real hardware taught us

Verifying against a live LibreHardwareMonitor caught three bugs that would all
have shipped silently, and each was wrong in a way that still *looked* right:

1. **`cpu.temp` reported the wrong figure.** A Ryzen exposes six labels
   containing "Tdie" — the package reading, two per-CCD, their max and their
   average — so a `contains("tdie")` test matched all six and whichever
   published last won. It read **38.6 °C** against a real Tctl/Tdie of **51 °C**.
   Only `Tctl` maps to `cpu.temp` now, and any other CCD label is dropped.
2. **`gpu.temp.hot` reported VRAM.** Matching `"junction"` caught "GPU Memory
   Junction", which runs about 10 °C above the core. Only `"hot"` matches now,
   and a card with no hotspot sensor correctly reports nothing.
3. **`CCDs Max` never matched at all** — the code looked for `"ccd max"` and
   LibreHardwareMonitor says `"ccds max"`.

One thing that looked like a fourth bug was not one. `cpu.temp.die` reads
*lower* than `cpu.temp` — 43 °C against 55 — because on Ryzen the package sensor
spikes above the individual dies. Both figures are right; the mistake was
calling it "CPU hottest core", which implies it should be the larger of the two.

### Degradation, verified

With no monitor installed: **63 sensors reporting, 11 unavailable**. Both
providers report themselves down with a reason, every unsupplied sensor renders
`--` rather than a fake zero, and nothing stalls the poll.

Settings says which source is answering, and says plainly when none is. A CPU
temperature layer reading `--` looks like a broken app rather than a missing
helper, and that screen is the only place that can explain the difference.

### Fans are published unlabelled

A board hands its fan headers out as "Fan #1" upward with no idea what is
plugged into them, so all seven are published rather than three. On the machine
this was built against the pump sits on `fan.7` at ~2700 rpm and nothing in the
data says so — `pump.rpm` stays unavailable unless a monitor actually labels a
header as a pump. Publishing them all lets someone bind the one that is theirs
instead of the mapping silently dropping it.

---

## 11. Risks and things deliberately left out

**JPEG size cap.** *Measured in phase 0 and smaller than feared* — the heaviest
overlay costs ≈15%, and the worst realistic case peaks at 85% of the cap. The
adaptive loop absorbs the rest. Still worth surfacing `overlayQuality` in the UI
so that a scene which does push quality down is visible rather than mysterious.
If it ever bites: prefer a background pill over a text outline, since a flat
translucent pill is markedly cheaper to encode than a stroked glyph.

**Double compression.** Accepted, with the reasoning in §4.3. Revisit only if
it is visible on the glass.

**The CLI gets no overlay.** Changing that means a native renderer — Direct2D +
DirectWrite driven from the profile JSON — which is roughly a second
implementation of §5 and §7 and would drift from the editor's rendering. If it
is ever wanted, the honest version is to move rendering entirely into native and
have the editor host a D2D surface, not to maintain two renderers.

**Non-NVIDIA GPUs.** `gpu.load` works everywhere via PDH; temperature, VRAM,
power and fans do not. AMD (`atiadlxx.dll`) and Intel (IGCL) providers are
deliberately out of scope — neither DLL exists on this machine, so both would
be written blind and shipped untested. The optional LHM/HWiNFO tier covers those
cards in the meantime, which is why it is worth having anyway.

**Per-item overlay overrides.** One profile is active globally. Per-`MediaItem`
overrides are a natural extension, but note that per-item rotate and stretch
overrides already exist in the model with no UI — that gap is worth closing in
the same pass as this, or not opening a second one.

**Rotation.** ~~The overlay composites in panel space, after rotation... that is
the intended behaviour.~~ **This was wrong, and it shipped wrong in phase 4.**

`RenderOpts.Rotate` makes ffmpeg pre-rotate the video so that the pump head's
*physical* turn on its magnet cancels it out. Compositing into the panel buffer
after that leaves the overlay square to the buffer — and therefore sideways on
the glass, at exactly the mountings the setting exists to support.

Fixed by turning the overlay with the video: layers are drawn in **viewer
space** — 960×480 normally, **480×960 at 90° and 270°** — and mapped into the
panel buffer by `OverlayRenderer.RotationTransform`. The editor's canvas uses
the same design size, so a rotated panel is designed portrait, as it is seen.
`LayerContext` carries the dimensions so the renderer, the anchoring and the
canvas's hit-testing cannot disagree.

The lesson: "upright" was ambiguous between the buffer and the viewer, and I
resolved it the wrong way on paper without a rotated panel to check against.

**Still open: layouts do not survive a change of aspect.** Layer coordinates are
stored in viewer space, so switching between a landscape mounting (0°/180°) and
a portrait one (90°/270°) reshapes the design surface from 960×480 to 480×960.
Anchors absorb the corners, but a profile laid out for 960 wide has its
left-hand and right-hand clusters overlapping in the middle at 480 — visible
and confirmed at 90°. A 0°↔180° or 90°↔270° change is unaffected.

Options, none of them free: remap coordinates proportionally on a rotation
change (lossy, and silently moves things the user placed); keep a separate
layout per orientation (honest, more state, and a profile becomes two);
or leave it and say so. Left alone for now — it only bites when the panel is
physically remounted a quarter turn, which is rare and already means
re-designing.
