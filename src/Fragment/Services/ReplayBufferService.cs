using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Fragment.Models;
using Fragment.Utils;

namespace Fragment.Services;

/// <summary>
/// Maintains a rolling "instant replay" buffer by continuously recording the screen into a
/// ring of short MPEG-TS segments via ffmpeg's segment muxer. <see cref="SaveClipAsync"/> then
/// stitches the most recent segments together with the concat demuxer to produce a clip covering
/// roughly the last N seconds.
/// </summary>
public sealed class ReplayBufferService
{
    /// <summary>Length of each rolling segment, in seconds. Smaller = finer clip granularity.</summary>
    private const int SegmentSeconds = 2;

    /// <summary>Segment files use the .ts container so they can be concatenated by stream copy.</summary>
    private const string SegmentExtension = ".ts";

    private readonly string _ffmpegPath;
    private readonly object _gate = new();

    private Process? _process;
    private string? _bufferDir;
    private RecordingProfile? _profile;
    private LoopbackCapturePipe? _loopback;

    public ReplayBufferService(string ffmpegPath)
    {
        if (string.IsNullOrWhiteSpace(ffmpegPath))
        {
            throw new ArgumentException("ffmpeg path must be supplied.", nameof(ffmpegPath));
        }

        _ffmpegPath = ffmpegPath;
    }

    /// <summary>True while the buffering ffmpeg process is alive.</summary>
    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _process is { HasExited: false };
            }
        }
    }

    /// <summary>
    /// Starts continuous segmented recording into a fresh temp directory. The number of retained
    /// segments is sized to cover at least <paramref name="bufferSeconds"/> seconds.
    /// </summary>
    public void Start(RecordingProfile profile, int bufferSeconds)
    {
        if (profile is null)
        {
            throw new ArgumentNullException(nameof(profile));
        }

        if (bufferSeconds <= 0)
        {
            bufferSeconds = 120;
        }

        lock (_gate)
        {
            if (_process is { HasExited: false })
            {
                throw new InvalidOperationException("Replay buffer is already running.");
            }
        }

        var bufferDir = CreateBufferDirectory();
        var wrap = ComputeSegmentWrap(bufferSeconds);

        // Real system-audio capture via WASAPI loopback for the rolling buffer too.
        LoopbackInfo? loopbackInfo = null;
        if (profile.Audio is AudioMode.SystemOnly or AudioMode.SystemAndMic)
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
                _loopback = null;
            }
        }

        var arguments = BuildBufferArguments(profile, bufferDir, wrap, loopbackInfo);

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

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var sessionLoopback = _loopback;
        process.Exited += (_, _) => OnBufferExited(process, sessionLoopback, bufferDir);

        // Publish under the lock before Start() so an instant exit's handler sees this session.
        lock (_gate)
        {
            _process = process;
            _bufferDir = bufferDir;
            _profile = profile;
        }

        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("ffmpeg process did not start.");
            }
            ChildProcessTracker.Track(process); // dies with Fragment no matter how it exits
            process.BeginErrorReadLine();
            process.BeginOutputReadLine();
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                if (ReferenceEquals(_process, process)) { _process = null; _bufferDir = null; }
                _loopback = null;
            }
            try { process.Dispose(); } catch { }
            sessionLoopback?.Dispose();
            TryDeleteDirectory(bufferDir);
            throw new InvalidOperationException("Failed to start the replay buffer ffmpeg process.", ex);
        }
    }

    /// <summary>
    /// Fires when the buffer ffmpeg exits on its own (crash / encoder failure) without Stop() being
    /// called, so the view model can reflect that the buffer is no longer running.
    /// </summary>
    public event EventHandler? Stopped;

    // Bound to a specific session; disposes only that session's own resources.
    private void OnBufferExited(Process process, LoopbackCapturePipe? loopback, string? dir)
    {
        bool wasCurrent;
        lock (_gate)
        {
            wasCurrent = ReferenceEquals(_process, process);
            if (wasCurrent)
            {
                _process = null;
                if (ReferenceEquals(_loopback, loopback)) { _loopback = null; }
                _bufferDir = null;
            }
        }

        try { process.Dispose(); } catch { }
        loopback?.Dispose();

        if (wasCurrent)
        {
            // Self-exit (not a user Stop): clean up this session's temp dir and notify.
            TryDeleteDirectory(dir);
            Stopped?.Invoke(this, EventArgs.Empty);
        }
        // If not current, Stop() owns this session's dir deletion.
    }

    /// <summary>Stops the buffering process and deletes its rolling-segment temp directory.</summary>
    public void Stop()
    {
        Process? process;
        LoopbackCapturePipe? loopback;
        string? bufferDir;
        lock (_gate)
        {
            process = _process;
            _process = null;
            loopback = _loopback;
            _loopback = null;
            bufferDir = _bufferDir;
            _bufferDir = null;
        }

        if (process is null)
        {
            loopback?.Dispose();
            TryDeleteDirectory(bufferDir);
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                try
                {
                    process.StandardInput.WriteLine("q");
                    process.StandardInput.Flush();
                }
                catch (Exception)
                {
                    // stdin may be closed.
                }

                if (!process.WaitForExit(4000) && !process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    try { process.WaitForExit(1500); } catch { }
                }
            }
        }
        catch (Exception)
        {
            // Best-effort shutdown.
        }
        finally
        {
            try { process.Dispose(); } catch { }
            loopback?.Dispose();
            TryDeleteDirectory(bufferDir); // reclaim the rolling .ts segments
        }
    }

    /// <summary>Deletes leftover %TEMP%\Fragment\replay_* dirs from previous (possibly crashed) runs.</summary>
    public static void CleanupStaleBuffers()
    {
        try
        {
            var root = Path.Combine(Path.GetTempPath(), "Fragment");
            if (!Directory.Exists(root)) return;
            foreach (var dir in Directory.GetDirectories(root, "replay_*"))
            {
                TryDeleteDirectory(dir);
            }
        }
        catch (Exception)
        {
            // Best-effort.
        }
    }

    private static void TryDeleteDirectory(string? dir)
    {
        if (string.IsNullOrEmpty(dir)) return;
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch (Exception)
        {
            // A segment may still be locked by a dying ffmpeg; the startup sweep will get it next time.
        }
    }

    /// <summary>
    /// Concatenates the most recent segments that together cover at least <paramref name="seconds"/>
    /// seconds into <paramref name="outputPath"/> using the concat demuxer. Returns the output path
    /// on success, or null if no buffered footage is available.
    /// </summary>
    public async Task<string?> SaveClipAsync(int seconds, string outputPath)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            throw new ArgumentException("Output path is required.", nameof(outputPath));
        }

        if (seconds <= 0)
        {
            seconds = 30;
        }

        string? bufferDir;
        lock (_gate)
        {
            bufferDir = _bufferDir;
        }

        if (string.IsNullOrEmpty(bufferDir) || !Directory.Exists(bufferDir))
        {
            return null;
        }

        // Order segments oldest -> newest by last-write time. The currently-open segment is the
        // newest; ffmpeg is still writing it, so we exclude it to avoid concatenating a partial file.
        List<FileInfo> segments;
        try
        {
            segments = new DirectoryInfo(bufferDir)
                .GetFiles("*" + SegmentExtension)
                .OrderBy(f => f.LastWriteTimeUtc)
                .ToList();
        }
        catch (Exception)
        {
            return null;
        }

        if (segments.Count == 0)
        {
            return null;
        }

        // Drop the most-recently-modified file (likely still being written).
        if (segments.Count > 1)
        {
            segments.RemoveAt(segments.Count - 1);
        }

        if (segments.Count == 0)
        {
            return null;
        }

        // How many tail segments do we need to cover the requested duration?
        var neededSegments = (int)Math.Ceiling(seconds / (double)SegmentSeconds);
        neededSegments = Math.Min(neededSegments, segments.Count);

        var tail = segments.Skip(segments.Count - neededSegments).ToList();
        if (tail.Count == 0)
        {
            return null;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);

        // Write a concat-demuxer list file referencing the tail segments in order.
        var listPath = Path.Combine(bufferDir, $"concat_{Guid.NewGuid():N}.txt");
        try
        {
            await File.WriteAllTextAsync(listPath, BuildConcatList(tail)).ConfigureAwait(false);

            var arguments = BuildConcatArguments(listPath, outputPath);
            var (exitCode, stderr) = await RunFfmpegAsync(arguments).ConfigureAwait(false);

            if (exitCode != 0)
            {
                throw new InvalidOperationException(
                    $"ffmpeg concat failed (exit {exitCode}): {stderr}");
            }

            return outputPath;
        }
        finally
        {
            TryDelete(listPath);
        }
    }

    // ----------------------------------------------------------------------------------------
    // Argument construction
    // ----------------------------------------------------------------------------------------

    /// <summary>
    /// Builds the segment-muxer command: capture the screen (and audio) and write a rolling ring of
    /// fixed-length .ts segments. <c>-segment_wrap</c> caps the number of files on disk.
    /// </summary>
    private static string BuildBufferArguments(RecordingProfile profile, string bufferDir, int wrap, LoopbackInfo? loopback)
    {
        var sb = new StringBuilder();
        sb.Append("-hide_banner -loglevel error -y");

        // Reuse the recorder's input + encoding logic by building the capture portion here.
        AppendCapture(sb, profile, loopback);

        // Segment muxer: rolling, wrapping, fixed-duration TS files. reset_timestamps keeps each
        // segment independently playable / concatenatable.
        var pattern = Path.Combine(bufferDir, "seg_%05d" + SegmentExtension);
        sb.Append(" -f segment");
        sb.Append(" -segment_time ").Append(SegmentSeconds.ToString(CultureInfo.InvariantCulture));
        sb.Append(" -segment_wrap ").Append(wrap.ToString(CultureInfo.InvariantCulture));
        sb.Append(" -segment_format mpegts");
        sb.Append(" -reset_timestamps 1");
        sb.Append(' ').Append(Quote(pattern));

        return sb.ToString();
    }

    /// <summary>
    /// Builds the concat-demuxer command that stitches the listed segments together by stream copy
    /// (no re-encode) into the output file.
    /// </summary>
    private static string BuildConcatArguments(string listPath, string outputPath)
    {
        var sb = new StringBuilder();
        sb.Append("-hide_banner -loglevel error -y");
        // -safe 0 allows absolute paths in the list file.
        sb.Append(" -f concat -safe 0 -i ").Append(Quote(listPath));
        // Stream copy: fast and lossless. faststart helps mp4 progressive playback.
        sb.Append(" -c copy");

        if (outputPath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) ||
            outputPath.EndsWith(".mov", StringComparison.OrdinalIgnoreCase))
        {
            sb.Append(" -movflags +faststart");
        }

        sb.Append(" -fflags +genpts");
        sb.Append(' ').Append(Quote(outputPath));
        return sb.ToString();
    }

    /// <summary>
    /// Appends the gdigrab video input + dshow audio + encoder settings for buffer capture.
    /// Audio (when present) is mixed/mapped; output is forced to a TS-friendly codec set
    /// (H.264 + AAC) so segments concatenate cleanly regardless of profile container.
    /// </summary>
    private static void AppendCapture(StringBuilder sb, RecordingProfile profile, LoopbackInfo? loopback)
    {
        var fps = profile.Fps > 0 ? profile.Fps : 60;

        // ---- Video input ----
        sb.Append(" -f gdigrab");
        sb.Append(" -framerate ").Append(fps.ToString(CultureInfo.InvariantCulture));
        sb.Append(" -draw_mouse ").Append(profile.CaptureCursor ? '1' : '0');

        switch (profile.Source)
        {
            case CaptureSource.Region:
                sb.Append(" -offset_x ").Append(profile.RegionX.ToString(CultureInfo.InvariantCulture));
                sb.Append(" -offset_y ").Append(profile.RegionY.ToString(CultureInfo.InvariantCulture));
                sb.Append(" -video_size ")
                  .Append(NormalizeEven(profile.RegionWidth > 0 ? profile.RegionWidth : 1920).ToString(CultureInfo.InvariantCulture))
                  .Append('x')
                  .Append(NormalizeEven(profile.RegionHeight > 0 ? profile.RegionHeight : 1080).ToString(CultureInfo.InvariantCulture));
                sb.Append(" -i ").Append(Quote("desktop"));
                break;

            case CaptureSource.Window:
                var title = string.IsNullOrWhiteSpace(profile.WindowTitle) ? "desktop" : profile.WindowTitle!;
                sb.Append(" -i ").Append(Quote($"title={title}"));
                break;

            case CaptureSource.Monitor:
                var mon = MonitorEnumerator.GetByIndex(profile.MonitorIndex);
                if (mon is not null && mon.Width > 0 && mon.Height > 0)
                {
                    sb.Append(" -offset_x ").Append(mon.X.ToString(CultureInfo.InvariantCulture));
                    sb.Append(" -offset_y ").Append(mon.Y.ToString(CultureInfo.InvariantCulture));
                    sb.Append(" -video_size ")
                      .Append(NormalizeEven(mon.Width).ToString(CultureInfo.InvariantCulture))
                      .Append('x')
                      .Append(NormalizeEven(mon.Height).ToString(CultureInfo.InvariantCulture));
                }
                else if (profile.RegionWidth > 0 && profile.RegionHeight > 0)
                {
                    sb.Append(" -offset_x ").Append(profile.RegionX.ToString(CultureInfo.InvariantCulture));
                    sb.Append(" -offset_y ").Append(profile.RegionY.ToString(CultureInfo.InvariantCulture));
                    sb.Append(" -video_size ")
                      .Append(NormalizeEven(profile.RegionWidth).ToString(CultureInfo.InvariantCulture))
                      .Append('x')
                      .Append(NormalizeEven(profile.RegionHeight).ToString(CultureInfo.InvariantCulture));
                }
                else
                {
                    var (ppw, pph) = NativeMethods.GetPrimaryScreenSize();
                    if (ppw > 0 && pph > 0)
                    {
                        sb.Append(" -offset_x 0 -offset_y 0 -video_size ")
                          .Append(NormalizeEven(ppw).ToString(CultureInfo.InvariantCulture))
                          .Append('x')
                          .Append(NormalizeEven(pph).ToString(CultureInfo.InvariantCulture));
                    }
                }
                sb.Append(" -i ").Append(Quote("desktop"));
                break;

            case CaptureSource.FullScreen:
            default:
                var (pw, ph) = NativeMethods.GetPrimaryScreenSize();
                if (pw > 0 && ph > 0)
                {
                    sb.Append(" -offset_x 0 -offset_y 0 -video_size ")
                      .Append(NormalizeEven(pw).ToString(CultureInfo.InvariantCulture))
                      .Append('x')
                      .Append(NormalizeEven(ph).ToString(CultureInfo.InvariantCulture));
                }
                sb.Append(" -i ").Append(Quote("desktop"));
                break;
        }

        // ---- Audio inputs ----
        var audioInputs = AppendAudioInputs(sb, profile, loopback);

        // ---- Encoding (TS-friendly: H.264 + AAC, keyframes aligned to segment length) ----
        var videoCodec = MapTsVideoEncoder(profile.Encoder);
        bool software = videoCodec.StartsWith("lib", StringComparison.Ordinal);
        sb.Append(" -c:v ").Append(videoCodec);
        sb.Append(" -pix_fmt ").Append(software ? "yuv420p" : "nv12");

        if (software)
        {
            sb.Append(" -preset ").Append(profile.Preset.ToString());
        }

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

        // Keyframe every segment so each .ts starts cleanly and concat works. Software encoders
        // also get an explicit forced-keyframe expression; hardware encoders rely on -g.
        sb.Append(" -g ").Append((fps * SegmentSeconds).ToString(CultureInfo.InvariantCulture));
        if (software)
        {
            sb.Append(" -force_key_frames ").Append(Quote($"expr:gte(t,n_forced*{SegmentSeconds})"));
        }

        if (audioInputs == 0)
        {
            sb.Append(" -an");
        }
        else
        {
            if (audioInputs == 2)
            {
                sb.Append(" -filter_complex ")
                  .Append(Quote("[1:a][2:a]amix=inputs=2:duration=longest:dropout_transition=2[aout]"));
                sb.Append(" -map 0:v -map ").Append(Quote("[aout]"));
            }
            else
            {
                sb.Append(" -map 0:v -map 1:a");
            }

            sb.Append(" -c:a aac");
            if (profile.AudioBitrateKbps > 0)
            {
                sb.Append(" -b:a ").Append(profile.AudioBitrateKbps.ToString(CultureInfo.InvariantCulture)).Append('k');
            }
        }
    }

    private static int AppendAudioInputs(StringBuilder sb, RecordingProfile profile, LoopbackInfo? loopback)
    {
        bool wantSystem = profile.Audio is AudioMode.SystemOnly or AudioMode.SystemAndMic;
        bool wantMic = profile.Audio is AudioMode.MicOnly or AudioMode.SystemAndMic;
        int count = 0;

        if (wantSystem)
        {
            if (loopback is not null)
            {
                AppendPipeAudio(sb, loopback);
                count++;
            }
            else if (!string.IsNullOrWhiteSpace(profile.SystemAudioDevice))
            {
                AppendDshowAudio(sb, profile.SystemAudioDevice!);
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
        sb.Append(" -thread_queue_size 1024 -f dshow -i ").Append(Quote($"audio={deviceName}"));
    }

    /// <summary>
    /// Maps the profile encoder to a TS-compatible codec. Non-H.264-family encoders (VP9/AV1) cannot
    /// live in an MPEG-TS ring, so they fall back to libx264 for the buffer.
    /// </summary>
    private static string MapTsVideoEncoder(VideoEncoder encoder) => encoder switch
    {
        VideoEncoder.x264 => "libx264",
        VideoEncoder.x265 => "libx265",
        VideoEncoder.NVENC_H264 => "h264_nvenc",
        VideoEncoder.NVENC_HEVC => "hevc_nvenc",
        VideoEncoder.AMF_H264 => "h264_amf",
        VideoEncoder.QSV_H264 => "h264_qsv",
        // VP9 / AV1 are not valid in MPEG-TS; use H.264 for the rolling buffer.
        _ => "libx264",
    };

    // ----------------------------------------------------------------------------------------
    // Helpers
    // ----------------------------------------------------------------------------------------

    private static string BuildConcatList(IEnumerable<FileInfo> segments)
    {
        var sb = new StringBuilder();
        foreach (var seg in segments)
        {
            // concat demuxer syntax: file '<path>' — single quotes escaped by doubling.
            var safePath = seg.FullName.Replace("'", "'\\''");
            sb.Append("file '").Append(safePath).Append('\'').Append('\n');
        }

        return sb.ToString();
    }

    private async Task<(int ExitCode, string StdErr)> RunFfmpegAsync(string arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _ffmpegPath,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        var stderrTask = process.StandardError.ReadToEndAsync();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();

        await process.WaitForExitAsync().ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        _ = await stdoutTask.ConfigureAwait(false);

        return (process.ExitCode, stderr);
    }

    private static string CreateBufferDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "Fragment", "replay_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// Number of segment files to retain so the ring spans at least <paramref name="bufferSeconds"/>.
    /// A couple of extra segments are added as headroom (one is always mid-write).
    /// </summary>
    private static int ComputeSegmentWrap(int bufferSeconds)
    {
        var segments = (int)Math.Ceiling(bufferSeconds / (double)SegmentSeconds);
        return Math.Max(2, segments + 2);
    }

    private static int NormalizeEven(int value)
    {
        if (value <= 0)
        {
            return 2;
        }

        return value % 2 == 0 ? value : value - 1;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception)
        {
            // Ignore cleanup failures.
        }
    }

    private static string Quote(string value)
    {
        return "\"" + value.Replace("\"", "\\\"") + "\"";
    }
}
