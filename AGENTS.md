# AGENTS.md

Instructions for working in this repo. Keep it true; delete stale or generic lines.

## What this is

Drive a **TZMRIT PF360 / "Jungle Leopard"** pump LCD (960×480 panel on an AIO
cooler head) from Windows. Windows 10/11, **x64 only**. Reverse-engineered
protocol (firmware 3.1). See `README.md` for full detail; this is the
hard-earned context an agent would otherwise guess wrong.

## Build & verify

No test project ships in the solution — verify by building, and for anything
touching the overlay, **by rendering it and looking at it**. That is not a
figure of speech: the overlay bugs that assertions missed and a rendered image
caught include an auto-scaled bar chart implying load on an idle GPU, a graph
fill drawing grey under a green line, and a glow that read as a halo at 44 px
and a blob at 22 px. Geometry assertions passed for all three.

Requires **Visual Studio 2022 (v143 toolset)** + **.NET 9 SDK**.

```sh
msbuild "Jungle Leopard Display.sln" -p:Configuration=Release -p:Platform=x64 -restore
```

- Output lands in `x64\Release\` (native + managed together) so `DllImport`
  resolves with no probing paths.
- **Solution quirk:** `Debug|x64` maps onto `Release|x64` for the native projects
  (Core/Native/CLI) — inherited from the original `.sln`. So a plain "Debug"
  solution build produces *optimised* native code. The manager builds Debug
  properly. `Win32` builds the native projects but **not** the manager.
- **Release builds emit no PDBs** (`DebugInformationFormat=None`,
  `GenerateDebugInformation=false`).
- **Native DLL build-order gotcha:** `JLDisplayManager.csproj` pins
  `<SetPlatform>Platform=x64</SetPlatform>` on the `JLDisplayNative` vcxproj
  reference. Letting the platform float → MSBuild builds the vcxproj a second
  time under a different platform → `LNK1257` (front/back-end not compatible)
  in the shared intermediate dir.

## Native ↔ managed interop (touch both sides)

`JLDisplayManager/Interop/NativeMethods.cs` **blits** structs into
`JLDisplayNative.dll` — it is not field-by-field marshalled, so a layout drift
corrupts memory silently. `VerifyLayout()` checks `sizeof(JlRenderOpts)==88` and
`sizeof(JlStatus)==1168` at startup, turning drift into one clear exception.
(1128 originally; 1144 with the playback-position doubles; 1168 now it carries
the four overlay diagnostics.)

If you change a struct in `JLDisplayNative/jl_api.h`, update the matching C#
struct **and** the expected sizes in `VerifyLayout()`.

## Runtime prerequisites

- **ffmpeg** — does all transcoding. `winget install "FFmpeg (Essentials Build)"`
  or drop `ffmpeg.exe` beside the binaries. Without it, only a pre-made ≤80 KB
  960×480 JPEG can show. `ffprobe` optional (else calibration reports elapsed
  time, not progress).
- **.NET 9 Desktop Runtime** — for the manager only; the CLI is native.

## One port owner

Only one process can hold the COM port at a time. **Don't run the CLI and the
manager together.** The manager's **Settings → Release device** hands the port
over. The tray app uses a process mutex (`Local\JungleLeopardDisplayManager.Instance`)
so a second launch hands over to the running instance instead of failing.

## Data paths

| Path | What |
|---|---|
| `%LOCALAPPDATA%\JungleLeopardDisplay\settings.json` | manager settings |
| `%LOCALAPPDATA%\JungleLeopardDisplay\library.json` | library + playlist |
| `%LOCALAPPDATA%\JungleLeopardDisplay\thumbnails\` | extracted video frames |
| `%LOCALAPPDATA%\JungleLeopardDisplay\overlays.json` | overlay profiles |
| `%LOCALAPPDATA%\JungleLeopardDisplay\overlay-assets\` | images used by overlay layers |
| `%LOCALAPPDATA%\JungleLeopardDisplay\manager.log` | connection events, errors |
| `%LOCALAPPDATA%\jl_display\calibration.txt` | **shared** with CLI |
| `%LOCALAPPDATA%\jl_display\packs\*.jlp` / `*.jlf` | preprocessed frames, **shared** |

Calibration/packs are keyed on file path, size, timestamp, filter chain (+ frame
rate, quality) so re-encoding or replacing invalidates automatically, and a
video calibrated in one program is instant in the other.

## Architecture

```
JLDisplayCore   (static lib)  protocol · device I/O · ffmpeg · calibration · packs
  │
  ├── Jungle Leopard Display.exe    thin CLI (single self-contained exe)
  │
  └── JLDisplayNative.dll         flat C API, async workers
            │
            └── JungleLeopardDisplayManager.exe   WPF tray app (net9.0-windows, P/Invoke)
```

- Core is **static** so the CLI stays a single exe; the DLL exists only to give
  C# something to P/Invoke.
- **Nothing blocks the UI:** every content call starts a native worker and
  returns; C# polls a status struct every 250 ms. Calibration is separately
  cancellable.
- Core log sink is **per-thread**; `jl::Device` locks at whole-message
  granularity. DLL mirrors the connection flag rather than calling
  `Device::IsOpen()` under the status lock (status lock is taken via the log
  sink while holding the device lock — query the other way and it deadlocks).
- Native plays **one item**; the playlist lives in C#.
- **Overlays composite natively** (`JLDisplayCore/jl_overlay.cpp`, WIC): the
  panel has no overlay plane and no alpha, so every frame is decoded, blended
  and re-encoded in the frame path. C# renders to a premultiplied BGRA bitmap on
  its own STA thread and hands it over double-buffered with a version counter;
  C++ blends and re-encodes. Costs ~3 ms.

## Manager specifics

- Entry point: `JLDisplayManager/App.xaml.cs`. `ShutdownMode = OnExplicitShutdown`
  — closing the window hides it (panel keeps running); **Exit** on the tray stops
  the process.
- Playlist **and** AI pipeline drive the one panel; starting either stops the
  other (arbitrated in `App.xaml.cs`).
- AI pipeline is **off by default**; SwarmUI at `http://localhost:7801`. API keys
  DPAPI-encrypted, base64 in `%LOCALAPPDATA%\JungleLeopardDisplay\ai.json`.
- `--hwaccel` affects **decoding only** (no GPU MJPEG encoder on NVENC/AMF).

## Overlays

The largest subsystem added since the original release. `docs/overlay-plan.md`,
`overlay-ai-plan.md` and `overlay-drawing-plan.md` carry the full record,
including the decisions that turned out wrong — read those before redesigning
anything here.

**Layers are plain objects with no WPF types.** The renderer runs on its own STA
thread and WPF objects have thread affinity, so a `Brush` stored on a layer could
not be drawn by both the render thread and the editor. Colours are strings.

**Colours are role names, not hex.** `good`, `warm`, `track`, `panel`, `dim`,
`line` resolve through the profile's theme **at draw time**, which is what makes
switching theme restyle everything coherently. A literal `#RRGGBB` passes
through untouched. If you add a colour, add a role.

**The render thread must never touch the live layer list.** `OverlayService`
keeps a shallow copy (`OverlayProfile.ShallowCopy`) — a `Collection was modified`
crash came from exactly this, and `ShallowCopy` forgetting `Theme` was a second
bug on top.

**The skip signature is how frames are avoided.** ~80% of ticks draw nothing
because nothing visible changed. **A new layer type that shows live data must be
added to `OverlayService.Signature`** or it will never redraw. Keyed on the
formatted value, or on `SensorSnapshot.Version` for a graph.

**Sensor ids are permanent.** They end up saved in user profiles, so renaming
one breaks people's layouts.

**Undo is snapshot-based** (`Views/Overlay/EditHistory.cs`), so it covers any
property without knowing about it — which is the point, given how often layers
gain fields. Two rules when touching the editor: an edit must go through
`Commit()`, not `Save()` (`Save` is for things that are not edits — switching
profile, the master enable — and must not enter the history); and anything that
auto-repeats must pass a coalesce key, or a held arrow key costs thirty presses
of Ctrl+Z. Snapshots cover the profile list and the active id, never the enable
switch or the render rate.

### The AI path

`Services/Overlay/Ai/`. A prompt becomes a compact `LayerSpec` list, expanded by
`LayerFactory`, styled by `StyleApplier`, positioned by `LayoutEngine`.

**The model never writes pixel coordinates or colours** — only kinds, sensors,
anchors, sizes and named roles. That is the whole reason a small local model is
reliable enough to be useful. Widening the schema is the standing temptation and
the standing risk; every field added is one more thing to get wrong.

`OverlaySystemPrompt` is composed per call and is ~2300 tokens. It carries the
live sensor list, so a prompt can only ask for sensors this machine has.

Everything from the model is treated as hostile input: unknown values are
dropped with a note, numbers are clamped, an invented sensor drops the layer
rather than producing a dead one.

### Sensors

`Services/Sensors/`. PDH and NVML need nothing installed; **CPU temperature does
not exist as a Windows counter** and needs LibreHardwareMonitor (web server,
:8085) or HWiNFO shared memory, both opt-in.

`HardwareNames.Match()` maps vendor labels to ids and **its ordering is
load-bearing**: `tctl` must be checked before anything else, because a Ryzen
reports six labels containing "Tdie" and matching those gave a reading 12°C low.
GPU hotspot matches `hot` only — `junction` also appears in a VRAM label.

The registry polls on a **`System.Threading.Timer`**, i.e. the thread pool. Code
that floods the pool thins out the sensor history; this was measured, not
theorised.

## Gotchas

- **`pwsh.exe is not recognized`** at build = vcpkg's `applocal.ps1` falling back
  to `powershell.exe`. Harmless; install pwsh to quiet it.
- Per-item rotation/stretch overrides exist in the model, honoured at playback,
  but have **no UI** yet.
- **ComboBox items render through a generated `TextBlock`**, which picks up the
  app's implicit `TextBlock` style — an explicit setter that beats anything
  inherited, so combos render as invisible text. The fix is the `ComboText`
  `DataTemplate` in `App.xaml`, **not** a `Foreground` on the ComboBox. That was
  tried first and does nothing.
- **Do not load `App.xaml` as a `ResourceDictionary`** in a test harness: it is
  the `ApplicationDefinition`, so parsing it constructs a second `App` and WPF
  throws "cannot create more than one Application instance". Instantiate `App`
  and call `InitializeComponent()`.
- Overlay text effects cost roughly the same to encode (outline, glow, pill are
  all within ~1 KB against an 80 KB cap). Earlier comments claiming outlines were
  expensive were **wrong** and are corrected; do not reintroduce that advice.
- The Anthropic client is raw HTTP (not the SDK) to keep the portable drop free
  of third-party assemblies.
- `.gitattributes` forces **CRLF** on `*.sln`, `*.vcxproj`, `*.csproj`, `*.props`,
  `*.targets`, `*.manifest`; `*.png/.ico/.jpg/.dll/.lib/.exe/.pdb` stay binary.
