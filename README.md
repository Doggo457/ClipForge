# Fragment

A fast, no-fuss **Windows screen recorder + instant clipper**, built with C# / .NET 8 / WPF.
All capture and encoding is handled by a bundled **FFmpeg** binary, so Fragment stays a small,
clean GUI on top of a proven engine.

## What it is

Fragment records your screen and lets you grab short, shareable clips — either by trimming a
recording after the fact, or by keeping an always-on **replay buffer** so you can save *the last
N seconds* the moment something happens (Shadowplay-style). MVVM-lite, no third-party MVVM
framework, JSON settings.

## Features

- One-click recording: full screen, a specific monitor, a region, or a single window.
- Always-on **replay buffer** — save the last N seconds with a global hotkey, retroactively.
- **Record-then-trim** with fast (lossless) or accurate (re-encode) cuts.
- Global hotkeys for record / save clip / toggle replay (work while minimized).
- Hardware encoders: NVIDIA **NVENC**, AMD **AMF**, Intel **QSV**, plus x264/x265 software.
- System audio + microphone capture (independent or mixed).
- Multiple named recording profiles, persisted to JSON.
- Containers: MP4, MKV, MOV, WebM, GIF. Configurable FPS, bitrate, and rate preset.
- Dark UI with a single accent colour.

## Build & run

Requirements: .NET 8 SDK, Windows 10/11, and an `ffmpeg.exe` (see below).

```powershell
dotnet restore
dotnet build -c Release
dotnet run --project src/Fragment/Fragment.csproj
```

## FFmpeg

Fragment shells out to `ffmpeg.exe`. It is located in this order:

1. The path configured in **Settings** (`FfmpegPath`).
2. `./ffmpeg/ffmpeg.exe` next to the application executable (the intended bundled layout).
3. `ffmpeg` on your system `PATH`.

Drop a static FFmpeg build into `src/Fragment/ffmpeg/ffmpeg.exe` (gitignored) before running,
or set the path in Settings. If FFmpeg is not found, recording and replay are disabled and the
status bar will say so.

## Screenshot

> _Screenshot placeholder — add `docs/screenshot.png` once the UI is wired up._

## License

MIT © 2026 Callum Challinor. See [LICENSE](LICENSE).
