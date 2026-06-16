using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Fragment.Models;
using Fragment.Utils;

namespace Fragment.Services;

/// <summary>
/// Drives a single ffmpeg.exe process that captures the screen (via gdigrab) and,
/// optionally, audio (via dshow) and encodes to the configured output file.
/// </summary>
public sealed class ScreenRecorder
{
    private readonly string _ffmpegPath;
    private Process? _process;
    private LoopbackCapturePipe? _loopback;
    private readonly object _gate = new();

    public ScreenRecorder(string ffmpegPath)
    {
        if (string.IsNullOrWhiteSpace(ffmpegPath))
        {
            throw new ArgumentException("ffmpeg path must be supplied.", nameof(ffmpegPath));
        }

        _ffmpegPath = ffmpegPath;
    }

    /// <summary>True while an ffmpeg capture process is alive.</summary>
    public bool IsRecording
    {
        get
        {
            lock (_gate)
            {
                return _process is { HasExited: false };
            }
        }
    }

    /// <summary>The absolute path of the file currently being written, or null when idle.</summary>
    public string? CurrentOutputPath { get; private set; }

    /// <summary>Raised once the ffmpeg process has been launched.</summary>
    public event EventHandler? Started;

    /// <summary>Raised when recording finishes cleanly; payload is the output file path.</summary>
    public event EventHandler<string>? Stopped;

    /// <summary>Raised when ffmpeg fails to start or exits with a non-zero code; payload is a message.</summary>
    public event EventHandler<string>? Error;

    /// <summary>
    /// Builds the output path, constructs the ffmpeg command line and launches the capture process.
    /// </summary>
    public Task StartAsync(RecordingProfile profile)
    {
        if (profile is null)
        {
            throw new ArgumentNullException(nameof(profile));
        }

        lock (_gate)
        {
            if (_process is { HasExited: false })
            {
                throw new InvalidOperationException("A recording is already in progress.");
            }
        }

        string outputPath;
        string arguments;
        LoopbackInfo? loopbackInfo = null;
        try
        {
            outputPath = ResolveOutputPath(profile);
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

            // Real system-audio capture via WASAPI loopback — no "Stereo Mix" device needed.
            // GIF has no audio track, so don't start a capture that ffmpeg would never read.
            if (profile.Container != OutputContainer.Gif && profile.Audio is AudioMode.SystemOnly or AudioMode.SystemAndMic)
            {
                try
                {
                    _loopback = new LoopbackCapturePipe();
                    _loopback.Start();
                    loopbackInfo = new LoopbackInfo(
                        _loopback.FfmpegInputPath, _loopback.FfmpegFormat,
                        _loopback.SampleRate, _loopback.Channels);
                }
                catch
                {
                    _loopback?.Dispose();
                    _loopback = null; // fall back to a configured dshow system device, if any
                }
            }

            arguments = BuildArgumentsCore(profile, outputPath, loopbackInfo);
        }
        catch (Exception ex)
        {
            _loopback?.Dispose();
            _loopback = null;
            Error?.Invoke(this, $"Failed to prepare recording: {ex.Message}");
            return Task.CompletedTask;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = _ffmpegPath,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };

        // Each launch owns its own loopback pipe; the exit handler captures both so a stale exit
        // from a previous session can never tear down a freshly-started one.
        var sessionLoopback = _loopback;
        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.Exited += (_, _) => OnProcessExited(process, sessionLoopback);
        process.ErrorDataReceived += OnErrorDataReceived;

        // Publish state under the lock BEFORE Start() so an instant exit's handler sees this process.
        lock (_gate)
        {
            _process = process;
            CurrentOutputPath = outputPath;
        }

        try
        {
            if (!process.Start())
            {
                lock (_gate) { if (ReferenceEquals(_process, process)) { _process = null; CurrentOutputPath = null; } _loopback = null; }
                process.Dispose();
                sessionLoopback?.Dispose();
                Error?.Invoke(this, "ffmpeg process failed to start.");
                return Task.CompletedTask;
            }

            ChildProcessTracker.Track(process); // dies with Fragment no matter how it exits
            process.BeginErrorReadLine();
            process.BeginOutputReadLine();
        }
        catch (Exception ex)
        {
            lock (_gate) { if (ReferenceEquals(_process, process)) { _process = null; CurrentOutputPath = null; } _loopback = null; }
            try { process.Dispose(); } catch { }
            sessionLoopback?.Dispose();
            Error?.Invoke(this, $"Unable to launch ffmpeg: {ex.Message}");
            return Task.CompletedTask;
        }

        Started?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Requests a graceful stop by writing 'q' to ffmpeg's stdin and awaiting clean exit.
    /// </summary>
    public async Task StopAsync()
    {
        Process? process;
        lock (_gate)
        {
            process = _process;
        }

        if (process is null || process.HasExited)
        {
            return;
        }

        try
        {
            // 'q' tells ffmpeg to finalize the output (flush muxer trailer) and exit.
            await process.StandardInput.WriteLineAsync("q").ConfigureAwait(false);
            await process.StandardInput.FlushAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Stdin may already be closed; fall through to wait/kill below.
        }

        try
        {
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(10));
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // ffmpeg ignored the graceful request — terminate it so the file is at least closed.
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception) { }
        }
        catch (Exception)
        {
            // The Exited handler may have disposed the process concurrently; that's a clean stop.
        }
    }

    private void OnErrorDataReceived(object sender, DataReceivedEventArgs e)
    {
        // ffmpeg writes all logging to stderr; we simply drain it so the pipe never blocks.
        // Consumers wanting live progress can subscribe to a future event here.
    }

    // Bound to a specific (process, loopback) session via the Exited lambda, so a late exit from an
    // old recording disposes only its own resources and never clobbers a newer session's state/UI.
    private void OnProcessExited(Process process, LoopbackCapturePipe? loopback)
    {
        bool wasCurrent = false;
        string? output = null;
        lock (_gate)
        {
            if (ReferenceEquals(_process, process))
            {
                wasCurrent = true;
                output = CurrentOutputPath;
                _process = null;
                CurrentOutputPath = null;
            }
            if (ReferenceEquals(_loopback, loopback))
            {
                _loopback = null;
            }
        }

        int exitCode = -1;
        try { exitCode = process.ExitCode; } catch (Exception) { }
        try { process.Dispose(); } catch (Exception) { }
        loopback?.Dispose();

        if (!wasCurrent)
        {
            return; // stale exit from a superseded session — don't touch the current UI/state
        }

        var path = output ?? string.Empty;

        // ffmpeg returns 0 on a clean 'q' stop; 255 is the conventional interrupt result (file still finalized).
        if (exitCode == 0 || exitCode == 255)
        {
            Stopped?.Invoke(this, path);
        }
        else
        {
            Error?.Invoke(this, $"ffmpeg exited with code {exitCode}.");
        }
    }

    private static string ResolveOutputPath(RecordingProfile profile)
    {
        var folder = string.IsNullOrWhiteSpace(profile.OutputFolder)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Fragment")
            : profile.OutputFolder;

        var now = DateTime.Now;
        var template = string.IsNullOrWhiteSpace(profile.FileNameTemplate)
            ? "Fragment_{date}_{time}"
            : profile.FileNameTemplate;

        var fileName = template
            .Replace("{date}", now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
            .Replace("{time}", now.ToString("HH-mm-ss", CultureInfo.InvariantCulture))
            .Replace("{name}", profile.Name ?? "Default");

        fileName = SanitizeFileName(fileName);

        return Path.Combine(folder, fileName + ContainerExtension(profile.Container));
    }

    private static string SanitizeFileName(string name)
    {
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(invalid, '_');
        }

        return name;
    }

    /// <summary>
    /// Pure, side-effect-free construction of the full ffmpeg argument string for a capture session.
    /// Exposed for unit testing.
    /// </summary>
    public static string BuildArguments(RecordingProfile profile, string outputPath)
        => BuildArgumentsCore(profile, outputPath, null);

    internal static string BuildArgumentsCore(RecordingProfile profile, string outputPath, LoopbackInfo? loopback)
    {
        if (profile is null)
        {
            throw new ArgumentNullException(nameof(profile));
        }

        if (string.IsNullOrWhiteSpace(outputPath))
        {
            throw new ArgumentException("Output path is required.", nameof(outputPath));
        }

        var sb = new StringBuilder();

        // Global flags. -y overwrites, banner hidden, errors-only logging to keep stderr clean.
        sb.Append("-hide_banner -loglevel error -y");

        // ---- Video input (gdigrab) ----
        AppendVideoInput(sb, profile);

        // ---- Audio inputs (WASAPI loopback pipe and/or dshow), if any ----
        // GIF output carries no audio, so don't declare audio inputs that would go unmapped.
        var audioInputs = profile.Container == OutputContainer.Gif ? 0 : AppendAudioInputs(sb, profile, loopback);

        // ---- Encoding / mapping ----
        AppendEncoding(sb, profile, audioInputs);

        // ---- Output ----
        sb.Append(' ').Append(Quote(outputPath));

        return sb.ToString();
    }

    /// <summary>
    /// Appends the gdigrab video input. Handles full-screen, monitor offset, region and window-title capture.
    /// </summary>
    private static void AppendVideoInput(StringBuilder sb, RecordingProfile profile)
    {
        var fps = profile.Fps > 0 ? profile.Fps : 60;

        sb.Append(" -f gdigrab");
        sb.Append(" -framerate ").Append(fps.ToString(CultureInfo.InvariantCulture));

        // draw_mouse=1 captures the cursor; =0 hides it.
        sb.Append(" -draw_mouse ").Append(profile.CaptureCursor ? '1' : '0');

        switch (profile.Source)
        {
            case CaptureSource.Region:
                // gdigrab captures a rectangle starting at (offset_x, offset_y) of size video_size.
                var width = NormalizeEven(profile.RegionWidth > 0 ? profile.RegionWidth : 1920);
                var height = NormalizeEven(profile.RegionHeight > 0 ? profile.RegionHeight : 1080);
                sb.Append(" -offset_x ").Append(profile.RegionX.ToString(CultureInfo.InvariantCulture));
                sb.Append(" -offset_y ").Append(profile.RegionY.ToString(CultureInfo.InvariantCulture));
                sb.Append(" -video_size ")
                  .Append(width.ToString(CultureInfo.InvariantCulture))
                  .Append('x')
                  .Append(height.ToString(CultureInfo.InvariantCulture));
                sb.Append(" -i ").Append(Quote("desktop"));
                break;

            case CaptureSource.Monitor:
                AppendMonitorInput(sb, profile);
                break;

            case CaptureSource.Window:
                // gdigrab can grab a specific top-level window by title: title=<window title>.
                var title = string.IsNullOrWhiteSpace(profile.WindowTitle) ? "desktop" : profile.WindowTitle!;
                sb.Append(" -i ").Append(Quote($"title={title}"));
                break;

            case CaptureSource.FullScreen:
            default:
                AppendPrimaryFullScreen(sb);
                break;
        }
    }

    /// <summary>
    /// Captures a specific monitor by its enumerated index, using that monitor's exact bounds in
    /// virtual-desktop coordinates. Falls back to explicit region fields, then the primary screen.
    /// </summary>
    private static void AppendMonitorInput(StringBuilder sb, RecordingProfile profile)
    {
        var mon = MonitorEnumerator.GetByIndex(profile.MonitorIndex);
        if (mon is not null && mon.Width > 0 && mon.Height > 0)
        {
            sb.Append(" -offset_x ").Append(mon.X.ToString(CultureInfo.InvariantCulture));
            sb.Append(" -offset_y ").Append(mon.Y.ToString(CultureInfo.InvariantCulture));
            sb.Append(" -video_size ")
              .Append(NormalizeEven(mon.Width).ToString(CultureInfo.InvariantCulture))
              .Append('x')
              .Append(NormalizeEven(mon.Height).ToString(CultureInfo.InvariantCulture));
            sb.Append(" -i ").Append(Quote("desktop"));
        }
        else if (profile.RegionWidth > 0 && profile.RegionHeight > 0)
        {
            sb.Append(" -offset_x ").Append(profile.RegionX.ToString(CultureInfo.InvariantCulture));
            sb.Append(" -offset_y ").Append(profile.RegionY.ToString(CultureInfo.InvariantCulture));
            sb.Append(" -video_size ")
              .Append(NormalizeEven(profile.RegionWidth).ToString(CultureInfo.InvariantCulture))
              .Append('x')
              .Append(NormalizeEven(profile.RegionHeight).ToString(CultureInfo.InvariantCulture));
            sb.Append(" -i ").Append(Quote("desktop"));
        }
        else
        {
            AppendPrimaryFullScreen(sb);
        }
    }

    /// <summary>
    /// Full-screen capture bounded to the primary monitor. gdigrab "desktop" alone spans the whole
    /// multi-monitor virtual desktop, which can exceed a hardware encoder's max width and records
    /// every monitor; bounding to the primary screen fixes both.
    /// </summary>
    private static void AppendPrimaryFullScreen(StringBuilder sb)
    {
        var (w, h) = NativeMethods.GetPrimaryScreenSize();
        if (w > 0 && h > 0)
        {
            sb.Append(" -offset_x 0 -offset_y 0 -video_size ")
              .Append(NormalizeEven(w).ToString(CultureInfo.InvariantCulture))
              .Append('x')
              .Append(NormalizeEven(h).ToString(CultureInfo.InvariantCulture));
        }
        sb.Append(" -i ").Append(Quote("desktop"));
    }

    /// <summary>
    /// Appends one or two dshow audio inputs depending on <see cref="AudioMode"/>.
    /// Returns the number of audio inputs added (0, 1 or 2).
    /// </summary>
    private static int AppendAudioInputs(StringBuilder sb, RecordingProfile profile, LoopbackInfo? loopback)
    {
        bool wantSystem = profile.Audio is AudioMode.SystemOnly or AudioMode.SystemAndMic;
        bool wantMic = profile.Audio is AudioMode.MicOnly or AudioMode.SystemAndMic;
        int count = 0;

        if (wantSystem)
        {
            if (loopback is not null)
            {
                AppendPipeAudio(sb, loopback);   // real WASAPI loopback (system/desktop audio)
                count++;
            }
            else if (!string.IsNullOrWhiteSpace(profile.SystemAudioDevice))
            {
                AppendDshowAudio(sb, profile.SystemAudioDevice!); // fallback: configured dshow device
                count++;
            }
        }

        if (wantMic && !string.IsNullOrWhiteSpace(profile.MicDevice))
        {
            AppendDshowAudio(sb, profile.MicDevice!);
            count++;
        }

        return count;
    }

    private static void AppendPipeAudio(StringBuilder sb, LoopbackInfo loopback)
    {
        sb.Append(" -thread_queue_size 1024");
        sb.Append(" -f ").Append(loopback.Format);
        sb.Append(" -ar ").Append(loopback.SampleRate.ToString(CultureInfo.InvariantCulture));
        sb.Append(" -ac ").Append(loopback.Channels.ToString(CultureInfo.InvariantCulture));
        sb.Append(" -i ").Append(Quote(loopback.InputPath));
    }

    private static void AppendDshowAudio(StringBuilder sb, string deviceName)
    {
        // dshow device specifier: audio=<device name>. The whole token is quoted to survive spaces.
        sb.Append(" -thread_queue_size 1024 -f dshow -i ").Append(Quote($"audio={deviceName}"));
    }

    /// <summary>
    /// Appends codec selection, bitrate, container-specific flags and stream mapping.
    /// </summary>
    private static void AppendEncoding(StringBuilder sb, RecordingProfile profile, int audioInputs)
    {
        // GIF is a special-case pipeline: no audio, palette-friendly output.
        if (profile.Container == OutputContainer.Gif)
        {
            // High-quality GIF: build a palette on the fly and apply it.
            sb.Append(" -vf ")
              .Append(Quote($"fps={(profile.Fps > 0 ? profile.Fps : 15)},split[s0][s1];[s0]palettegen[p];[s1][p]paletteuse"));
            return;
        }

        // ----- Video codec -----
        var videoCodec = MapVideoEncoder(profile.Encoder, profile.Container);
        bool software = IsSoftwareEncoder(profile.Encoder);
        sb.Append(" -c:v ").Append(videoCodec);

        // Hardware encoders (NVENC/AMF/QSV) want NV12; software encoders use yuv420p.
        // gdigrab yields BGRA, which ffmpeg converts to the requested format.
        sb.Append(" -pix_fmt ").Append(software ? "yuv420p" : "nv12");

        // Only software x264/x265 honor the -preset speed names.
        if (software)
        {
            sb.Append(" -preset ").Append(profile.Preset.ToString());
        }

        // Bitrate target. Software gets a CBR-ish maxrate/bufsize; hardware encoders (esp. AMF)
        // reject those, so they just take -b:v.
        if (profile.VideoBitrateKbps > 0)
        {
            var kbps = profile.VideoBitrateKbps.ToString(CultureInfo.InvariantCulture);
            sb.Append(" -b:v ").Append(kbps).Append('k');
            if (software)
            {
                sb.Append(" -maxrate ").Append(kbps).Append('k');
                sb.Append(" -bufsize ").Append((profile.VideoBitrateKbps * 2).ToString(CultureInfo.InvariantCulture)).Append('k');
            }
        }

        // Keyframe roughly every 2 seconds for seekability.
        var fps = profile.Fps > 0 ? profile.Fps : 60;
        sb.Append(" -g ").Append((fps * 2).ToString(CultureInfo.InvariantCulture));

        // ----- Audio handling / mapping -----
        if (audioInputs == 0)
        {
            // No audio captured.
            sb.Append(" -an");
        }
        else
        {
            if (audioInputs == 2)
            {
                // Mix the two dshow inputs (system + mic) into a single stereo track.
                // Inputs: 0 = video, 1 = first audio, 2 = second audio.
                sb.Append(" -filter_complex ")
                  .Append(Quote("[1:a][2:a]amix=inputs=2:duration=longest:dropout_transition=2[aout]"));
                sb.Append(" -map 0:v -map ").Append(Quote("[aout]"));
            }
            else
            {
                // Single audio input at index 1.
                sb.Append(" -map 0:v -map 1:a");
            }

            sb.Append(" -c:a ").Append(MapAudioCodec(profile.Container));
            if (profile.AudioBitrateKbps > 0)
            {
                sb.Append(" -b:a ").Append(profile.AudioBitrateKbps.ToString(CultureInfo.InvariantCulture)).Append('k');
            }
        }

        // ----- Container-specific muxer flags -----
        if (profile.Container == OutputContainer.Mp4 || profile.Container == OutputContainer.Mov)
        {
            // +faststart relocates the moov atom for progressive playback.
            sb.Append(" -movflags +faststart");
        }
    }

    private static bool IsSoftwareEncoder(VideoEncoder encoder) =>
        encoder is VideoEncoder.x264 or VideoEncoder.x265 or VideoEncoder.VP9 or VideoEncoder.AV1;

    private static string MapVideoEncoder(VideoEncoder encoder, OutputContainer container) => encoder switch
    {
        VideoEncoder.x264 => "libx264",
        VideoEncoder.x265 => "libx265",
        VideoEncoder.NVENC_H264 => "h264_nvenc",
        VideoEncoder.NVENC_HEVC => "hevc_nvenc",
        VideoEncoder.AMF_H264 => "h264_amf",
        VideoEncoder.QSV_H264 => "h264_qsv",
        VideoEncoder.VP9 => "libvpx-vp9",
        VideoEncoder.AV1 => "libaom-av1",
        _ => "libx264",
    };

    private static string MapAudioCodec(OutputContainer container) => container switch
    {
        // WebM containers require Opus/Vorbis; everything else takes AAC happily.
        OutputContainer.WebM => "libopus",
        _ => "aac",
    };

    public static string ContainerExtension(OutputContainer container) => container switch
    {
        OutputContainer.Mp4 => ".mp4",
        OutputContainer.Mkv => ".mkv",
        OutputContainer.Mov => ".mov",
        OutputContainer.WebM => ".webm",
        OutputContainer.Gif => ".gif",
        _ => ".mp4",
    };

    private static int NormalizeEven(int value)
    {
        // Many encoders (yuv420p) require even dimensions.
        if (value <= 0)
        {
            return 2;
        }

        return value % 2 == 0 ? value : value - 1;
    }

    /// <summary>Wraps a token in double quotes, escaping any embedded quotes.</summary>
    private static string Quote(string value)
    {
        return "\"" + value.Replace("\"", "\\\"") + "\"";
    }
}
