# AGENTS.md

Instructions for working in this repo. Keep it true; delete stale or generic lines.

## What this is

Drive a **TZMRIT PF360 / "Jungle Leopard"** pump LCD (960×480 panel on an AIO
cooler head) from Windows. Windows 10/11, **x64 only**. Reverse-engineered
protocol (firmware 3.1). See `README.md` for full detail; this is the
hard-earned context an agent would otherwise guess wrong.

## Build & verify

No tests exist — verify by building. Requires **Visual Studio 2022 (v143 toolset)**
+ **.NET 9 SDK**.

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
`sizeof(JlStatus)==1128` at startup, turning drift into one clear exception.

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

## Manager specifics

- Entry point: `JLDisplayManager/App.xaml.cs`. `ShutdownMode = OnExplicitShutdown`
  — closing the window hides it (panel keeps running); **Exit** on the tray stops
  the process.
- Playlist **and** AI pipeline drive the one panel; starting either stops the
  other (arbitrated in `App.xaml.cs`).
- AI pipeline is **off by default**; SwarmUI at `http://localhost:7801`. API keys
  DPAPI-encrypted, base64 in `%LOCALAPPDATA%\JungleLeopardDisplay\ai.json`.
- `--hwaccel` affects **decoding only** (no GPU MJPEG encoder on NVENC/AMF).

## Gotchas

- **`pwsh.exe is not recognized`** at build = vcpkg's `applocal.ps1` falling back
  to `powershell.exe`. Harmless; install pwsh to quiet it.
- Per-item rotation/stretch overrides exist in the model, honoured at playback,
  but have **no UI** yet.
- The Anthropic client is raw HTTP (not the SDK) to keep the portable drop free
  of third-party assemblies.
- `.gitattributes` forces **CRLF** on `*.sln`, `*.vcxproj`, `*.csproj`, `*.props`,
  `*.targets`, `*.manifest`; `*.png/.ico/.jpg/.dll/.lib/.exe/.pdb` stay binary.
