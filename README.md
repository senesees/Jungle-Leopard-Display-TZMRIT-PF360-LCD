# Jungle Leopard Display

Drive a **TZMRIT PF360 / "Jungle Leopard" pump LCD** — the 960×480 panel on the
head of an AIO cooler — from Windows, without the vendor's Electron app.

Ships two programs over one shared core:

- **Display Manager** — a tray app that holds the panel permanently, with a
  media library, a playlist, an optional AI image pipeline, a live preview of
  what's on the glass, and an optional start-at-logon task.
- **`Jungle Leopard Display.exe`** — the original command-line tool, for
  scripting and one-shot use.

![The display manager, mid-playlist](docs/screenshot.png)

The protocol was recovered from the vendor app's `resources/app.asar`
(`main/_baseClass/device.js`) and verified against firmware **3.1**, model
`TXW818-ST7701S-5.5inch-hor`. See [Protocol notes](#protocol-notes).

---

## Requirements

| | |
|---|---|
| **OS** | Windows 10/11, **x64 only** |
| **Runtime** | [.NET 9 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/9.0) — for the manager only; the CLI is native |
| **ffmpeg** | `winget install "FFmpeg (Essentials Build)"`, or drop `ffmpeg.exe` beside the binaries |
| **Build** | Visual Studio 2022 (v143 toolset) + .NET 9 SDK |

ffmpeg does all transcoding. Without it, the only thing that can be displayed is
an image that is *already* a 960×480 JPEG under 80 KB. `ffprobe` is optional —
without it, calibration reports elapsed time instead of progress.

> **Only one process can hold the panel at a time.** Close the vendor app before
> using either program, and don't run the CLI and the manager together — the
> manager has a **Release device** button for exactly this.

---

## Download

A portable x64 build is attached to each [release][releases]: unzip anywhere and
run, no installer and no registry keys. It carries the tray app, the CLI and the
native core — about 220 KB — and still wants the two prerequisites above, since
neither the .NET runtime nor ffmpeg is bundled.

[releases]: https://github.com/senesees/Jungle-Leopard-Display-TZMRIT-PF360-LCD/releases

---

## Building

```sh
msbuild "Jungle Leopard Display.sln" -p:Configuration=Release -p:Platform=x64 -restore
```

Release builds emit **no PDBs** — `DebugInformationFormat` is `None` and
`GenerateDebugInformation` is `false` for every Release configuration, and the
manager sets `DebugType=none`. Debug configurations keep their symbols, though
the solution maps `Debug|x64` onto `Release|x64` for every project, so a plain
solution build never produces any.

Everything lands in `x64\Release\`, native and managed together, so
`DllImport` resolves with no probing paths.

---

## Using the manager

Run `x64\Release\JungleLeopardDisplayManager.exe`. It finds the panel by
hardware ID, connects, and sits in the notification area.

- **Add files…**, or drag images and videos onto the window.
- **Download…** takes a YouTube URL and fetches the video into the library.
  `yt-dlp.exe` ships beside the manager, so nothing needs installing first.
- Select an item and **Show now**, or double-click it.
- **Add to playlist** to build a rotation, then **Play playlist**. Stills hold
  for their dwell in seconds; videos play through and hand over.
- Closing the window hides it — the panel keeps running. **Exit** on the tray
  menu is the only thing that stops it.

New videos are calibrated in the background as they're added, so pressing Show
later is instant.

### Settings

![Settings](docs/settings.png)

**Rotation** compensates for however the pump head ended up mounted — it rotates
270° on its magnet and there is no device-side rotation command, so this is done
host-side (as the vendor app does).

**Start when I sign in** registers a logon-triggered scheduled task rather than a
`Run` key, which buys restart-on-failure and no "stop on battery". The checkbox
reads the real registered state, so removing the task outside the app is
reflected honestly.

**Preprocessing** decides whether ffmpeg runs for as long as something is on the
panel, or only once per item. See below.

---

## Preprocessing

The panel is not a display — it is an MJPEG sink. Everything it accepts is one
fixed shape: 960×480, baseline 4:2:0, under 80 KB, arriving at a steady rate. So
what ffmpeg produces for a given source is a pure function of that source's bytes
and the render options, which is the same assumption the calibration cache has
always made. Those frames can therefore be computed once and replayed.

| Mode | What happens | Cost |
|---|---|---|
| **Off** | ffmpeg streams MJPEG for as long as the item plays | a running ffmpeg, always |
| **Memory** | frames built into RAM once, then ffmpeg exits | ~1.2 MB/s of RAM, rebuilt each launch |
| **Disk** | frames built into a pack file once, reused forever | ~70 MB per minute of video |

**Memory** is the default. It writes nothing, and a source too long to hold
falls back to streaming on its own — so its worst case is exactly the old
behaviour.

Both limits are settable in **Settings → Preprocessing**, which shows whichever
one the selected mode actually uses (Off has none). Defaults are 512 MB in
memory and 8 GB on disk, and each preset is labelled with roughly how much video
it holds. The two limits behave differently on purpose: reaching the memory
limit makes that one item stream instead, while reaching the disk limit evicts
the least recently used packs. So raising the memory limit widens what benefits
— it never decides what will play.

A pack is deliberately dull: a header, the frame blobs, then an index. Identical
frames share one blob and the index points at it more than once, which collapses
GIFs and static footage to a fraction of their nominal size. Disk packs are
memory-**mapped** rather than read, so a long video costs address space rather
than working set, and the cache evicts least-recently-used once it passes its
limit.

Two things worth knowing:

- **The frame rate is part of what a frame is.** It is baked into the pack's key
  along with rotation, stretch and the calibrated quality, so changing any of
  them rebuilds every pack. That is correct rather than wasteful — the old frames
  really are the wrong frames — but it is why the frame-rate slider feels
  expensive in Disk mode.
- **Stills benefit too, in every mode but Off.** A still in a playlist used to be
  re-transcoded on every rotation; now it is prepared once and remembered.

---

## AI image pipeline

The manager can generate its own wallpaper: you keep a list of short ideas, an
LLM expands each into a full prompt, [SwarmUI][swarmui] renders it, and the
result goes on the panel. **AI** on the status bar opens it.

Everything is optional and off until configured — the app is exactly as it was
if you never open that window.

[swarmui]: https://github.com/mcmonkeyprojects/SwarmUI

```
prompts ──▶ LLM enhance ──▶ SwarmUI ──▶ generated\  ──▶ library
                                            │
                        buffer of ready images
                                            │
                            dwell timer ────┴──▶ panel
```

### What you need

**SwarmUI**, running and reachable — the default install listens on
`http://localhost:7801`. Press **Test** and the window fills its model, sampler
and scheduler lists from that server, so you pick from what is actually
installed rather than from a guess.

**An LLM**, optionally. Two providers:

| Provider | For |
|---|---|
| **OpenAI-compatible** | Ollama, LM Studio, llama.cpp, OpenRouter, OpenAI — anything at `/v1/chat/completions`. Works offline. |
| **Anthropic** | Claude, via the Messages API. Needs a key. |

Set the provider to **None** to send your prompts to the image model as written.
Each provider remembers its own address, model and key, so switching between a
local model and a hosted one to compare results doesn't make you retype
anything.

### Two clocks

Generation on a home GPU takes anywhere from ten seconds to several minutes, so
generating and displaying run independently:

- **Keep ready ahead** — how many finished images to hold in reserve. A worker
  tops the buffer up in the background.
- **Show each for** — how long an image holds the panel.
- **Generate no more than** — a floor on time between generations, so a fast
  backend doesn't fill the disk in a minute. `0` generates as fast as the buffer
  empties.

The panel is never waiting on the backend: it rotates through what is already
finished. If the buffer does run dry, the current image simply stays up.

### Failure is not fatal

The pipeline is meant to be left running for days, so nothing in it stops the
slideshow permanently:

- A failed enhancement **falls back to the prompt as written** and logs it — a
  dead LLM costs picture quality, not the whole rotation. Tick *Skip generating
  if enhancement fails* to prefer stopping instead.
- A failed generation backs off, doubling from 15 s to a 5-minute cap, so an
  unreachable server is a quiet line in the log rather than a request storm.
- An expired SwarmUI session — which every restart of SwarmUI causes — is
  renewed once and the request retried.
- A disconnected panel picks its image back up when the device returns.

Each leg has its own **Test** button, so a failure isolates to one hop rather
than needing to be guessed at.

### Where the images go

Generated images land in `%LOCALAPPDATA%\JungleLeopardDisplay\generated\` and
appear under **GENERATED**, the second tab above the media grid, with the prompt
as the tooltip. They arrive on their own schedule and in bulk, so they get their
own tab rather than burying the hand-picked library — but they are ordinary
items otherwise, and can be shown, added to a playlist and pinned like anything
else.

**Keep at most** bounds how many are kept; past that the oldest go, file and
all. **Pin** exempts one — pinned images are never pruned, and neither is
whatever is currently on the panel or waiting in the buffer. **Remove** on the
GENERATED tab deletes the file as well, since nothing else would ever list it
again.

### A note on keys

API keys are encrypted with **DPAPI**, scoped to your Windows account, and
stored as base64 in `ai.json`. A copied `ai.json` is useless on another machine
or under another account. That is the whole guarantee: it stops a key
travelling, not someone already running as you. A key is never displayed once
saved — leave the box blank to keep it, type in it to replace it.

---

## Using the CLI

```
Jungle Leopard Display.exe [--port COMn] [--rotate 0|90|180|270] [--stretch]
    --info
    --light 0-100
    --image FILE [--once]
    --video FILE [--loop] [--fps N] [--quality 2-31]
                 [--recalibrate] [--hwaccel auto|d3d11va|cuda|qsv]
                 [--preprocess none|memory|disk] [--limit MB]
    --clear-cache
```

```sh
# what's attached?
"Jungle Leopard Display.exe" --info

# a still, held until Ctrl-C (the panel drops out of live mode without a keepalive)
"Jungle Leopard Display.exe" --image photo.jpg

# send one frame and exit, screen mounted upside down
"Jungle Leopard Display.exe" --image photo.jpg --rotate 180 --once

# loop a clip
"Jungle Leopard Display.exe" --video clip.mp4 --loop

# transcode it once, then loop it forever with no ffmpeg running
"Jungle Leopard Display.exe" --video clip.gif --loop --preprocess disk

# same, but cap the pack cache at 2 GB (--limit applies to whichever mode is set)
"Jungle Leopard Display.exe" --video clip.gif --loop --preprocess disk --limit 2048
```

Without `--once`, `--image` holds live mode until interrupted. `--stretch` fills
the panel and ignores aspect ratio; the default pads with black.

`--hwaccel` affects **decoding only** — there is no GPU MJPEG encoder on NVENC or
AMF (Intel's `mjpeg_qsv` is the lone exception), so the encode stays on the CPU
either way. It pays off on high-resolution sources, where decoding dominates; on
small inputs the extra GPU↔system copies can make it slower than plain software.

---

## How it works

```
JLDisplayCore  (static lib)   protocol · framing · ffmpeg · calibration · packs
      │
      ├── Jungle Leopard Display.exe    thin CLI over the core
      │
      └── JLDisplayNative.dll           flat C API, async workers
                │
                └── JungleLeopardDisplayManager.exe   WPF tray app (P/Invoke)
```

The core is a **static** library so the CLI stays a single self-contained exe;
`JLDisplayNative.dll` exists only to give C# something to P/Invoke.

Two constraints shape everything:

**One port owner.** Only one process on the machine can hold the COM port, so
the tray app is the sole owner and the GUI lives inside it. There is no IPC and
no second binary to keep in step.

**Nothing blocks the UI.** The device is a blocking serial handle fed by a child
ffmpeg process, and calibrating a long video takes tens of seconds. So every
content call starts a native worker and returns immediately; C# polls a status
struct every 250 ms. Calibration is separately cancellable — without that,
picking a large video would wedge the UI until it finished.

Some consequences worth knowing if you touch this code:

- The core's **log sink is per-thread**. Background calibration runs alongside
  playback, and a global sink would interleave their progress into one garbled
  line.
- `jl::Device` is **internally locked at whole-message granularity**. That's why
  a brightness change from the UI thread lands cleanly between two video frames
  instead of halfway through a JPEG.
- The DLL mirrors the connection flag rather than calling `Device::IsOpen()`
  under the status lock — the core takes the status lock (via the log sink)
  while holding the device lock, so querying the other way round deadlocks.
- The native side plays **one item**; the playlist lives in C#. Timing-critical
  work stays in C++, scheduling stays where it's easy to write and persist.

### Native API

```c
int32_t jl_open(const wchar_t* port);          // NULL = autodetect
void    jl_close(void);
int32_t jl_show_image(const wchar_t*, const JlRenderOpts*);   // async
int32_t jl_play_video(const wchar_t*, const JlRenderOpts*);   // async
int32_t jl_calibrate (const wchar_t*, const JlRenderOpts*);   // blocking, device-free
void    jl_stop(void);
void    jl_get_status(JlStatus*);              // poll this
int32_t jl_get_last_frame(uint8_t*, int32_t);  // the JPEG actually on the glass
```

Full contract in [`JLDisplayNative/jl_api.h`](JLDisplayNative/jl_api.h). The C#
side checks `sizeof(JlRenderOpts) == 88` and `sizeof(JlStatus) == 1128` at
startup, so struct drift fails loudly instead of corrupting memory silently.

---

## Where things are stored

| Path | What |
|---|---|
| `%LOCALAPPDATA%\JungleLeopardDisplay\settings.json` | manager settings |
| `%LOCALAPPDATA%\JungleLeopardDisplay\library.json` | library and playlist |
| `%LOCALAPPDATA%\JungleLeopardDisplay\thumbnails\` | extracted video frames |
| `%LOCALAPPDATA%\JungleLeopardDisplay\downloads\` | videos fetched from YouTube |
| `%LOCALAPPDATA%\JungleLeopardDisplay\manager.log` | connection events, errors |
| `%LOCALAPPDATA%\jl_display\calibration.txt` | **shared** with the CLI |
| `%LOCALAPPDATA%\jl_display\packs\*.jlp` | preprocessed video frames, **shared** |
| `%LOCALAPPDATA%\jl_display\packs\*.jlf` | preprocessed stills, **shared** |

The calibration cache is keyed on each file's absolute path, size, timestamp and
filter chain, so re-encoding or replacing a video invalidates its entry
automatically — and a video calibrated in one program is instant in the other.

Packs are keyed on the same material plus the frame rate and the calibrated
quality, and the key is the filename, so a stale pack can never be mistaken for a
current one — it simply stops being looked up and ages out. The CLI and the
manager share them the same way they share calibration.

---

## Protocol notes

**Serial.** CDC-ACM at 115200/8-N-1 (the rate is advisory), DTR and RTS asserted,
no flow control. Found by walking the ports class for hardware ID
`VID_33C3&PID_7788` and reading `PortName` from its device registry key.

**Control frames.**

```
55 AA | length (u16 LE) | cmd | payload… | checksum (u16 LE)
```

`length` counts the whole frame (`payload + 7`). The checksum is a plain 16-bit
sum of every preceding byte.

| Cmd | Meaning | Reaches this panel |
|---|---|---|
| `0x01` | Restart | yes |
| `0x03` | Set backlight (one byte, 0–100) | yes |
| `0x06` | Get device info (replies with JSON) | yes |
| `0x0C` | Begin OTA firmware flash | yes |
| `0x11` | Enter / hold live mode | yes |
| `0x14` | Set motion-before-off | yes |
| `0x15` | Set motion timeout | firmware >= 2.8 |
| `0x20` | Set region (UTF-8 string) | yes |
| `0x21` | Close | firmware >= 3.1 |
| `0x23` | Set serial number, then reboot | yes |
| `0x25` | Set motor — open (`1`) / close (`2`) | only when region is `ycc28_v1` |
| `0x26` | Set real-time timeout | firmware >= 4.1, so never here |

The right-hand column is the condition the vendor app checks before it will send
that command, taken from `main/_baseClass/device.js` and evaluated against this
panel (firmware 3.1). Only `0x03`, `0x06` and `0x11` are sent from this project.

**`0x25` is not a pump-speed control.** The payload is one byte: `1` opens, `2`
closes. A 60-second CPU-temperature poll drives it — above the configured
threshold (default 50 °C) it opens, below it closes — and the device stays busy
for `actionTimeout` seconds afterwards, 35 by default, rejecting further
commands. It only runs on hardware whose region has been set to `ycc28_v1` via
`0x20`, which is not this one.

**`0x0C` and `0x23` are documented but not implemented, on purpose.** `0x23`
takes a serial number, writes it to the panel and reboots into it. `0x0C` takes
`F2 FF` followed by the firmware size as a 32-bit little-endian value, after
which the `.bin` streams through the image envelope below. Neither is gated away
from this hardware, and both are one bad byte from a panel that no longer
enumerates.

**Image frames use a different envelope** — no `55 AA`, no command byte:

```
length (u32 LE) | JPEG bytes… | checksum (u16 LE)
```

The checksum covers the length bytes too.

**Live mode lapses** unless `0x11` is re-sent about every **1500 ms**. This is
why showing a still keeps a thread alive rather than firing and forgetting.

**A still must be re-sent, not sent once.** The panel commits a frame only when
the bytes behind it arrive, and it needs a moment after `0x11` before it takes
pixels at all. Video hides both — ffmpeg's startup covers the mode change and
the next frame is 33 ms away — but a picture written once and then left alone
stays half-drawn on the glass. `HoldStill` re-sends the same frame every
**250 ms**, a fifth of the bandwidth playback already sustains.

**Stills must be 4:2:0, like every video frame.** ffmpeg matches the JPEG
encoder to its *input*, so video (`yuv420p`) lands on `yuvj420p` while an RGB
still (PNG, BMP, a screenshot) lands on a 4:4:4 layout this panel cannot
decode. The image path pins `-pix_fmt yuvj420p`, and the "already conforms,
send as-is" shortcut checks the SOF marker for baseline 4:2:0 rather than
trusting the dimensions alone.

**Before talking to the device**, the vendor app sends `FF D9 FF D9` (a JPEG
end-of-image marker, flushing any partial frame), pauses, then four zero bytes.
This client does the same.

**Size limits.** Frames must be ≤ **80 KB**. Video calibration targets **64 KB**
to leave headroom, by encoding every *keyframe* in the file (`-skip_frame nokey`,
so inter-frames are never reconstructed) and walking `-q:v` up through
`3,4,5,7,9,12,16,20,25,31` until the worst keyframe fits. Keyframes are
intra-coded and therefore the most detailed frames in the stream, which makes
them a conservative proxy. Surveying the *whole* file matters — calibrating on
the first few seconds badly underestimates a video that gets busier later.

**SPI-class panels.** If the image path doesn't work on your unit, check
`checkIsSPI()` in the vendor app's `main/util/common.js` against your model
string. Those take raw RGB565 big-endian with **no** length header and no
checksum — `((r>>3)<<11) | ((g>>2)<<5) | (b>>3)` as u16 BE per pixel. Control
commands are identical either way; only the pixel path differs.

---

## Troubleshooting

**"COM7 is in use by another program"** — something else holds the port: the
vendor app, a running CLI, or the manager itself. In the manager, use
**Settings → Release device** to hand it over, then **Reconnect** to take it
back.

**"ffmpeg.exe not found"** — install it, or drop `ffmpeg.exe` next to the
binaries. The manager checks once at startup and says so via a tray balloon,
rather than failing identically on every item.

**Nothing appears, no error** — the panel drops out of live mode after ~1.5 s
without a keepalive. If you're driving it yourself, keep sending `0x11`.

**Video is choppy** — check the dropped-frame count in the status line. Frames
over 80 KB are skipped; force a lower quality with `--quality 12` or
`--recalibrate`.

**"cannot reach SwarmUI"** — SwarmUI isn't running, or is on another port.
Check the address in the AI window and press **Test**; a working server reports
its version and model count.

**AI images generate but never appear** — the playlist and the AI slideshow both
drive the one panel, and starting either stops the other. **Start** in the AI
window is what hands the panel over.

**Enhanced prompts look like chat** — the enhancer strips code fences, quotes and
lead-in lines, but a model that ignores the system prompt entirely will still
produce prose. Lower the temperature, or reword the system prompt under
*Instructions and limits*.

**Build says `pwsh.exe is not recognized`** — that's vcpkg's `applocal.ps1`
integration trying PowerShell 7 and falling back to `powershell.exe`. Harmless;
install pwsh to quiet it.

---

## Repository layout

```
JLDisplayCore/        protocol, device I/O, ffmpeg, calibration cache, frame packs
JLDisplayNative/      flat C API over the core, for P/Invoke
JLDisplayManager/     WPF tray app (net9.0-windows, x64)
  Services/Ai/        SwarmUI client, LLM clients, prompt enhancer, pipeline
Jungle Leopard Display/   the CLI
docs/                 screenshots
```

### Known rough edges

- The solution maps **`Debug|x64` to `Release|x64`** for every project. That's
  inherited from the original `.sln`; the new projects mirror it so CRT settings
  match and linking works. It does mean "Debug" builds optimised code.
- Per-item rotation and stretch overrides exist in the model and are honoured at
  playback, but have no UI yet — only dwell time is editable per item.
- x64 only. The `Win32` solution configurations build the native projects but
  not the manager.
- The Anthropic client is raw HTTP rather than the official SDK, to keep the
  portable drop free of third-party assemblies. It covers one POST to
  `/v1/messages`; anything more — tools, streaming — would be worth the
  dependency instead.
- The AI pipeline generates one image at a time. SwarmUI will happily batch, and
  a backend with the VRAM for it is left idle between requests.

---

## Credit

Protocol reverse-engineered from the vendor Electron application. This project
ships no vendor code.

## Donations

BTC
```
bc1q3evq9z0scme5repkz5gmsza3tyfeaqwfqx8xa7trnjmh7a7yk8ts55xgfv
```
