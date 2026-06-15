# Fragment — Technical Plan

> A Windows-native screen recorder and instant clipper built on .NET 8 + WPF, with all
> capture and encoding delegated to a bundled FFmpeg binary.

---

## 1. Overview

**Fragment** is a desktop GUI application for Windows that records the screen and produces
short, shareable clips on demand. It is aimed at gamers, streamers, bug-reporters, and
tutorial authors who want OBS-grade capture without the configuration burden, plus a
"Shadowplay-style" always-on replay buffer so the last *N* seconds can be saved
*after* something interesting has already happened.

The application itself is a thin, well-structured WPF shell. The heavy lifting — screen
grab, audio capture, hardware-accelerated encoding, muxing, trimming, and concatenation —
is performed by **FFmpeg**, which the app drives by shelling out to `ffmpeg.exe`. This keeps
the managed code small, dependency-light, and easy to reason about, while inheriting
FFmpeg's enormous matrix of codecs, containers, and capture backends.

The codebase follows a hand-rolled, **MVVM-lite** pattern: `INotifyPropertyChanged`
view-models, an `ICommand` implementation (`RelayCommand`), and plain services — but **no**
third-party MVVM framework (no Prism, no CommunityToolkit.Mvvm, no ReactiveUI). Settings are
persisted as JSON via `System.Text.Json`.

---

## 2. Goals & Non-Goals

### Goals
- One-click full-screen, per-monitor, region, or window recording.
- Always-on **replay buffer** ("Instant Replay") — keep the last *N* seconds in a rolling
  buffer and dump a clip with a single hotkey, even retroactively.
- **Record-then-trim** workflow — record a long session, then carve out clips with a simple
  in/out trimmer UI.
- Global hotkeys that work while the app is minimized / another app is focused.
- Hardware encoder support (NVIDIA NVENC, AMD AMF, Intel QSV) with software x264/x265
  fallback.
- System audio + microphone capture, independently or mixed.
- A clean dark UI with a single accent colour (`#00b3ff`).
- Self-contained: bundle `ffmpeg.exe`; degrade gracefully and guide the user if it is absent.
- Human-readable JSON settings with multiple named **recording profiles**.

### Non-Goals (for v1)
- No live streaming / RTMP push (recording only).
- No scene compositor, overlays, webcam PiP, or source mixing (FFmpeg `filter_complex`
  could add this later, but it is out of scope for MVP).
- No built-in uploader / cloud sharing.
- No cross-platform support — Windows only (`net8.0-windows`, gdigrab/ddagrab/dshow).
- No frame-accurate NLE editing — the trimmer does cut-style trims only.
- We do **not** re-implement any capture/encode logic in managed code; FFmpeg owns it.

---

## 3. Tech Stack + Rationale

| Concern              | Choice                              | Why |
|----------------------|-------------------------------------|-----|
| Language / runtime   | C# 12 / .NET 8 (`net8.0-windows`)   | LTS, fast, first-class Windows desktop story. |
| UI                   | WPF (`UseWPF=true`)                 | Mature, retained-mode, great data binding, styling/theming, runs everywhere Win10+. |
| Architecture         | MVVM-lite, hand-rolled              | Avoids a framework dependency; the app is small enough that a `RelayCommand` + `INotifyPropertyChanged` is all we need. |
| Capture + encode     | Bundled **FFmpeg** (`ffmpeg.exe`)   | Battle-tested, supports gdigrab/ddagrab/dshow, NVENC/AMF/QSV, every container we want. Shelling out isolates native crashes from our process. |
| Settings persistence | `System.Text.Json`                  | In-box, fast, source-generation-friendly, no extra NuGet. |
| Global hotkeys       | Win32 `RegisterHotKey` via P/Invoke | The only reliable way to get system-wide hotkeys without a low-level keyboard hook. |
| Device enumeration   | FFmpeg `-list_devices` (dshow)      | Single source of truth — the same names FFmpeg will accept on the command line. |

**Why shell out to FFmpeg instead of binding a library (FFmpeg.AutoGen / FFMediaToolkit)?**
- Process isolation: a decoder/encoder fault kills `ffmpeg.exe`, not Fragment.
- The CLI surface is stable and trivially testable — `BuildArguments` is a pure function.
- Updating FFmpeg is "drop in a new exe", no ABI/marshalling concerns.
- The replay buffer maps perfectly onto the `segment` muxer + `concat` demuxer, which are
  CLI-first features.

---

## 4. Architecture

Fragment is layered: **Views** (XAML) → **ViewModels** (state + commands) → **Services**
(FFmpeg orchestration, hotkeys, settings, device discovery) → **Models** (POCO settings &
enums) and **Utils** (`RelayCommand`, P/Invoke).

```
                         +--------------------------------------------------+
                         |                     Views (XAML)                 |
                         |  MainWindow   SettingsWindow   TrimmerWindow      |
                         +-------------------------+------------------------+
                                                   | DataContext / bindings
                                                   v
                         +--------------------------------------------------+
                         |                   ViewModels                     |
                         |  MainViewModel    SettingsViewModel              |
                         |  (INotifyPropertyChanged, RelayCommand)          |
                         +----+--------+--------+--------+--------+----------+
                              |        |        |        |        |
              +---------------+        |        |        |        +----------------+
              v                        v        v        v                         v
   +--------------------+  +----------------+  +--------------+  +-----------+  +----------------+
   |  ScreenRecorder    |  | ReplayBuffer   |  | ClipTrimmer  |  | Hotkey    |  | DeviceEnumer.  |
   |  (record session)  |  | Service        |  | (cut/trim)   |  | Service   |  | (dshow list)   |
   |                    |  | (ring buffer)  |  |              |  | (Win32)   |  |                |
   +---------+----------+  +-------+--------+  +------+-------+  +-----+-----+  +-------+--------+
             |                     |                  |               |                |
             +----------+----------+---------+--------+               |                |
                        v                    v                        |                |
                +----------------+   +----------------+               |                |
                | FfmpegLocator  |   |   ffmpeg.exe   | <-------------(spawned by services)
                | (find binary)  |   | (child proc)   |               |
                +----------------+   +----------------+               |
                                                                      v
                        +-----------------+                  +-----------------+
                        | SettingsService |                  |  NativeMethods  |
                        | (JSON load/save)|                  | (user32 P/Invoke)|
                        +--------+--------+                  +-----------------+
                                 |
                                 v
                        +-----------------+
                        |   Models        |
                        | AppSettings,    |
                        | RecordingProfile|
                        | Enums           |
                        +-----------------+
```

**Key principles**
- ViewModels never touch FFmpeg directly; they call services.
- Services never touch WPF; they raise events / return tasks.
- `BuildArguments` and `ParseHotkey` are static & pure so they can be unit-tested headless.
- The only Win32 surface lives in `Utils/NativeMethods.cs`, wrapped by `HotkeyService`.

### Project / namespace layout
```
src/Fragment/
  Fragment.csproj
  App.xaml / App.xaml.cs                     -> namespace Fragment
  MainWindow.xaml / .cs                       -> namespace Fragment
  Models/
    Enums.cs                                  -> Fragment.Models
    RecordingProfile.cs                       -> Fragment.Models
    AppSettings.cs                            -> Fragment.Models
  Services/
    FfmpegLocator.cs                          -> Fragment.Services
    ScreenRecorder.cs
    ReplayBufferService.cs
    ClipTrimmer.cs
    HotkeyService.cs
    SettingsService.cs
    DeviceEnumerator.cs
  ViewModels/
    MainViewModel.cs                          -> Fragment.ViewModels
    SettingsViewModel.cs
  Views/
    SettingsWindow.xaml / .cs                 -> Fragment.Views
    TrimmerWindow.xaml / .cs
  Utils/
    RelayCommand.cs                           -> Fragment.Utils
    NativeMethods.cs
  ffmpeg/                                      -> bundled ffmpeg.exe (gitignored)
```

---

## 5. Feature List

### MVP (Phase 1–3)
- [x] Locate / validate bundled FFmpeg, prompt if missing.
- [x] Full-screen recording with start/stop button **and** global hotkey.
- [x] Live recording timer + status text.
- [x] Choose container (MP4 default) and FPS.
- [x] System + mic audio (dshow), or none.
- [x] Software x264 encode with sensible bitrate/preset.
- [x] Replay buffer (always-on ring) + "Save Clip" hotkey.
- [x] Record-then-trim window (input + in/out + trim).
- [x] Named recording profiles persisted to JSON.
- [x] Open output folder button.

### "Ton of options" (Phase 4+)
- Capture sources: full screen, specific monitor (by index), arbitrary region (x/y/w/h),
  specific window (by title — `gdigrab -i title=...`).
- Encoders: x264, x265, **NVENC** H.264/HEVC, **AMF** H.264, **QSV** H.264, VP9, AV1.
- Containers: MP4, MKV, MOV, WebM, GIF.
- Rate control presets (`ultrafast` … `slow`), target video bitrate, audio bitrate.
- Capture cursor toggle (gdigrab `-draw_mouse`).
- Independent system-audio and mic device selection (from enumerated dshow list).
- Configurable replay-buffer length and default clip length.
- Fully rebindable hotkeys (record / save clip / toggle replay).
- Output folder + filename template (`{date}`, `{time}` tokens).
- Minimize-to-tray, "play sound on clip", theme name (Dark for v1).
- Multiple profiles, switchable from the main window combo.

---

## 6. Clipping Design (the heart of the app)

Fragment offers **two independent clipping strategies**. They are complementary: the ring
buffer is for *retroactive* "save what just happened", the record-then-trim path is for
*deliberate* "record a session, then cut highlights".

### 6.1 Always-on replay ring buffer (segment muxer + concat)

**Goal:** at any moment, be able to save the *last N seconds* with near-zero CPU cost and no
"start recording first" friction — exactly like NVIDIA Instant Replay.

**Mechanism (`ReplayBufferService`):**
1. When the buffer is enabled, we launch a *single long-lived* `ffmpeg.exe` that captures the
   screen/audio (same input args as a normal recording) and writes to the **segment muxer**:
   ```
   ffmpeg <inputs> <encode> -f segment -segment_time 2 -segment_wrap K \
          -reset_timestamps 1 -segment_format mkv \
          "<tempdir>\seg_%05d.mkv"
   ```
   - `-segment_time 2` → each output file is ~2 seconds long.
   - `-segment_wrap K` → only **K** files are kept on disk; the muxer overwrites the oldest,
     giving us a fixed-size **ring buffer** on disk. `K = ceil(bufferSeconds / segment_time) + 2`
     (a couple of spare segments to cover the in-progress one and rounding).
   - `-reset_timestamps 1` → each segment starts at PTS 0 so it can be concatenated cleanly.
   - We use **MKV** (or MPEG-TS) for segments because they tolerate being cut mid-GOP and
     concatenated far better than fragmented MP4.
   - Encoding (not raw) is used so disk/IO stays bounded; with a hardware encoder this is
     cheap enough to leave running.

2. **Saving a clip** (`SaveClipAsync(seconds, outputPath)`):
   - List the segment files, sort by last-write time, and take the most recent ones whose
     **combined duration ≥ `seconds`** (count = `ceil(seconds / segment_time) + 1`).
   - Write a temporary FFmpeg **concat list** file:
     ```
     file 'seg_00018.mkv'
     file 'seg_00019.mkv'
     file 'seg_00020.mkv'
     ```
   - Concatenate with the **concat demuxer**, stream-copying (no re-encode → instant):
     ```
     ffmpeg -f concat -safe 0 -i list.txt -c copy "<outputPath>"
     ```
   - Optionally, a second pass could trim the head so the clip is *exactly* `seconds` long
     (segments overshoot because we round up). For v1 we accept a slight overshoot (cheap &
     lossless); precise trimming is a `ClipTrimmer` follow-up.
   - The in-progress segment (currently being written) is skipped to avoid a torn file.

**Why this design:** the ring buffer bounds disk usage to *exactly* `K` segments regardless of
how long the app runs, the save operation is a lossless stream-copy (milliseconds), and the
whole thing is just two FFmpeg invocations — no managed frame buffers, no memory pressure.

**Trade-offs:** clip boundaries land on segment edges (≤ `segment_time` granularity), and
audio/video sync relies on FFmpeg's segmenting (good with `-reset_timestamps`). Shrinking
`segment_time` improves precision at the cost of more files / muxer overhead.

### 6.2 Record-then-trim

**Goal:** capture a full session to a single file, then extract precise clips afterward.

**Mechanism:**
1. `ScreenRecorder.StartAsync` records the whole session to one output file (MP4/MKV/...).
2. `ScreenRecorder.StopAsync` ends it gracefully (sends `q` to FFmpeg stdin so the moov atom
   / index is written correctly — *killing* the process would corrupt an MP4).
3. The user opens **TrimmerWindow**, picks the recording, sets **start** and **end**, and the
   `ClipTrimmer` cuts it:
   - **Fast / lossless (`reEncode = false`)** — stream copy with input seeking:
     ```
     ffmpeg -ss <start> -to <end> -i input -c copy output
     ```
     Instant, but cuts land on the nearest keyframe (GOP boundary).
   - **Accurate (`reEncode = true`)** — output seeking + re-encode:
     ```
     ffmpeg -i input -ss <start> -to <end> -c:v libx264 -c:a aac output
     ```
     Frame-accurate in/out points, slower because it re-encodes.

These two paths give users the classic speed-vs-precision choice that every editor exposes.

---

## 7. Recording Pipeline

### 7.1 Inputs (capture backends)
- **Screen video:**
  - `gdigrab` — universal GDI-based grab. Supports `-framerate`, `-offset_x/-offset_y`,
    `-video_size WxH` (region), `-draw_mouse 0|1` (cursor), and `-i title=<window>` /
    `-i desktop`. Reliable everywhere; CPU-bound.
  - `ddagrab` — Desktop Duplication API (DXGI), GPU-side, lower overhead, per-monitor. Used
    where available for higher FPS / lower cost; falls back to `gdigrab`.
- **Audio:** `dshow` — `-f dshow -i audio="<device name>"`. Two inputs can be added (system
  loopback device + microphone) and mixed with `-filter_complex amix` when `AudioMode` is
  `SystemAndMic`.

### 7.2 Argument shape (built by `ScreenRecorder.BuildArguments`)
```
ffmpeg -y -hide_banner
       -f gdigrab -framerate <fps> -draw_mouse <0|1> [-offset_x X -offset_y Y -video_size WxH]
       -i <desktop | title=Window>
       [-f dshow -i audio="<system device>"]
       [-f dshow -i audio="<mic device>"]
       [-filter_complex "[1:a][2:a]amix=inputs=2[a]" -map 0:v -map "[a]"]
       -c:v <encoder> -preset/-rc <preset> -b:v <kbps>k -pix_fmt yuv420p -r <fps>
       -c:a aac -b:a <kbps>k
       "<outputPath>"
```

### 7.3 Encoder mapping
| `VideoEncoder` | FFmpeg `-c:v` | Notes |
|----------------|---------------|-------|
| `x264`         | `libx264`     | `-preset <RatePreset>`; CPU; universal. |
| `x265`         | `libx265`     | `-preset`; smaller files, slower. |
| `NVENC_H264`   | `h264_nvenc`  | NVIDIA GPU; `-preset p1..p7`/quality. |
| `NVENC_HEVC`   | `hevc_nvenc`  | NVIDIA GPU, HEVC. |
| `AMF_H264`     | `h264_amf`    | AMD GPU. |
| `QSV_H264`     | `h264_qsv`    | Intel iGPU. |
| `VP9`          | `libvpx-vp9`  | WebM. |
| `AV1`          | `libaom-av1` / `av1_nvenc` | next-gen; slow on CPU. |

Software encoders take the `RatePreset` directly (`-preset veryfast`). Hardware encoders map
the preset onto their own quality ladder; for v1 we pass a reasonable preset and the target
bitrate (`-b:v`), keeping the mapping in one place inside `BuildArguments`.

### 7.4 Stopping cleanly
FFmpeg is told to quit by writing `q` to its **stdin** (`StopAsync`). This lets it flush
buffers and finalize the container (critical for MP4's trailing index). A hard `Kill()` is
only the last-resort fallback after a grace period.

---

## 8. Settings Schema (`%AppData%\Fragment\settings.json`)

`AppSettings`:
```jsonc
{
  "Profiles": [ /* RecordingProfile[] */ ],
  "ActiveProfileName": "Default",
  "ReplayBufferEnabled": true,
  "ReplayBufferSeconds": 120,
  "ClipLengthSeconds": 30,
  "RecordHotkey": "Ctrl+Alt+R",
  "ClipHotkey": "Ctrl+Alt+C",
  "ReplayToggleHotkey": "Ctrl+Alt+B",
  "FfmpegPath": null,            // null => auto-locate
  "MinimizeToTray": true,
  "PlaySoundOnClip": true,
  "Theme": "Dark"
}
```

`RecordingProfile`:
```jsonc
{
  "Name": "Default",
  "Source": "FullScreen",        // FullScreen|Monitor|Region|Window
  "MonitorIndex": 0,
  "RegionX": 0, "RegionY": 0, "RegionWidth": 0, "RegionHeight": 0,
  "WindowTitle": null,
  "Fps": 60,
  "Encoder": "x264",             // x264|x265|NVENC_H264|...
  "VideoBitrateKbps": 12000,
  "Preset": "veryfast",          // ultrafast..slow
  "CaptureCursor": true,
  "Container": "Mp4",            // Mp4|Mkv|Mov|WebM|Gif
  "Audio": "SystemAndMic",       // None|SystemOnly|MicOnly|SystemAndMic
  "SystemAudioDevice": null,
  "MicDevice": null,
  "AudioBitrateKbps": 160,
  "OutputFolder": "%USERPROFILE%\\Videos\\Fragment",
  "FileNameTemplate": "Fragment_{date}_{time}"
}
```

- Enums are serialized as **strings** (`JsonStringEnumConverter`) for human-editable files.
- `SettingsService.Load()` tolerates a missing/corrupt file by returning sane defaults and
  re-saving.
- `AppSettings.ActiveProfile()` returns the profile whose `Name == ActiveProfileName`, else
  the first profile (and never null — defaults guarantee at least one).

---

## 9. Global Hotkeys

- Implemented with Win32 `RegisterHotKey` / `UnregisterHotKey` (P/Invoke in `NativeMethods`).
- `HotkeyService.Initialize(hwnd)` is given the main window handle (from
  `WindowInteropHelper`); the window adds an `HwndSource` hook that forwards `WM_HOTKEY`
  (`0x0312`) messages into `HotkeyService.ProcessMessage`, which raises `HotkeyPressed(id)`.
- `Register(modifiers, vk)` returns an incrementing id; the VM maps ids → actions
  (record / save clip / toggle replay).
- `ParseHotkey("Ctrl+Alt+R")` → `(MOD_CONTROL|MOD_ALT, VK_R)`. Supports `Ctrl/Control`,
  `Alt`, `Shift`, `Win`, function keys `F1`–`F12`, digits, and letters.
- Modifier constants: `MOD_ALT=0x1`, `MOD_CONTROL=0x2`, `MOD_SHIFT=0x4`, `MOD_WIN=0x8`.

---

## 10. UI Layout

**MainWindow** (compact control panel, dark, accent `#00b3ff`):
```
+--------------------------------------------------+
|  Fragment                              [ _ ][x] |
+--------------------------------------------------+
|  Profile: [ Default            v ]               |
|                                                  |
|     +---------------------------------------+    |
|     |          ●  START RECORDING           |    |   <- big accent toggle button
|     +---------------------------------------+    |
|                                                  |
|  Status: Idle                  00:00:00          |   <- status text + live timer
|                                                  |
|  [ Replay Buffer: OFF ]   [  Save Clip  ]        |
|                                                  |
|  [ Settings ]  [ Trim ]  [ Open Folder ]         |
+--------------------------------------------------+
```

**SettingsWindow** — a `TabControl` with sections: **Capture** (source, monitor, region,
window, cursor), **Video** (encoder, fps, bitrate, preset, container), **Audio** (mode,
system device, mic device, audio bitrate — devices from `DeviceEnumerator`), **Clipping**
(replay on/off, buffer seconds, clip length), **Hotkeys** (three rebindable fields),
**Output** (folder picker, filename template, tray/sound toggles). A **Save** button writes
via `SettingsService`.

**TrimmerWindow** — input file picker (browse), **Start** and **End** time fields
(`hh:mm:ss`), a **re-encode (accurate)** checkbox, and a **Trim** button that calls
`ClipTrimmer.TrimAsync`.

---

## 11. Milestone Roadmap (phases)

- **Phase 0 — Scaffold (this commit):** project, models, services, view-models, XAML shells,
  docs, license, gitignore. Compiles and runs as a shell.
- **Phase 1 — Record MVP:** wire `ScreenRecorder` end to end (full screen, x264, MP4),
  start/stop button, live timer, status, open-folder.
- **Phase 2 — Settings & profiles:** full `SettingsWindow`, JSON persistence, profile combo,
  device enumeration populating audio dropdowns.
- **Phase 3 — Replay buffer:** `ReplayBufferService` ring + `SaveClipAsync`, toggle + save
  hotkeys, "play sound on clip".
- **Phase 4 — Trimmer & options:** `TrimmerWindow`, lossless/accurate trim, expose all
  encoders/containers/region/window capture.
- **Phase 5 — Polish:** minimize-to-tray + notify icon, hardware-encoder auto-detection,
  toast notifications, settings validation, first-run FFmpeg download helper.

---

## 12. Build & Run

**Prerequisites:** .NET 8 SDK, Windows 10/11, an `ffmpeg.exe` (see §13).

```powershell
# from repo root
dotnet restore
dotnet build -c Release
dotnet run --project src/Fragment/Fragment.csproj
```

Publish a self-contained single file:
```powershell
dotnet publish src/Fragment/Fragment.csproj -c Release -r win-x64 `
  --self-contained true -p:PublishSingleFile=true -o publish
# then copy ffmpeg.exe into publish\ffmpeg\ffmpeg.exe
```

---

## 13. FFmpeg Bundling / Dependency Strategy

Resolution order (`FfmpegLocator.Find`):
1. **Configured path** — `AppSettings.FfmpegPath` if the user set one explicitly.
2. **Bundled** — `./ffmpeg/ffmpeg.exe` next to the executable (the intended distribution
   layout; the csproj copies the `ffmpeg/` folder to output).
3. **System PATH** — fall back to whatever `ffmpeg` resolves to on `PATH`.

`IsValid(path)` confirms the file exists and is an `.exe`. If nothing is found, the UI surfaces
a clear "FFmpeg not found" status and the record/replay actions are disabled.

**Distribution options:**
- *Recommended:* ship a known-good static FFmpeg build in `ffmpeg/` (gitignored so the binary
  is not committed; CI/release packaging downloads it).
- *Lightweight installer:* a first-run helper could download a pinned FFmpeg release and place
  it in `%AppData%\Fragment\ffmpeg`.
- We do not vendor FFmpeg source or link it; we only invoke the CLI, which keeps licensing
  clean (FFmpeg LGPL/GPL stays in its own binary).

---

## 14. Future Ideas
- Webcam picture-in-picture and overlays via `filter_complex`.
- Live streaming (RTMP/SRT) to Twitch/YouTube.
- Per-clip metadata, a clip library/gallery with thumbnails.
- Auto-upload / share links.
- Multi-track audio export (separate game/mic tracks).
- GPU auto-detection to default the best available hardware encoder.
- Scheduled / motion-triggered recording.
- Cross-platform port (Avalonia) reusing the service layer unchanged.
