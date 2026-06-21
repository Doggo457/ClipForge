using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Fragment.Models;

namespace Fragment.Services;

/// <summary>
/// FFmpeg-backed editing operations for the editor window: generating filmstrip thumbnails and
/// exporting an ordered list of <see cref="EditorSegment"/> (cut + merge) with optional resize,
/// target-size compression, audio control and GIF output.
/// </summary>
public sealed class VideoEditorService
{
    private readonly string _ffmpegPath;

    public VideoEditorService(string ffmpegPath)
    {
        if (string.IsNullOrWhiteSpace(ffmpegPath))
            throw new ArgumentException("FFmpeg path must be provided.", nameof(ffmpegPath));
        _ffmpegPath = ffmpegPath;
    }

    /// <summary>
    /// Probes a video's DISPLAY dimensions via ffmpeg, accounting for rotation metadata. Phone videos are
    /// often coded landscape (e.g. 1280x720) with a 90° flag and auto-rotated to portrait on decode/export;
    /// the WPF probe reports the CODED size (rotation ignored), so the editor canvas came out landscape and
    /// the auto-rotated portrait frame got huge pillarbox bars. Returns the size the exported frame will
    /// actually have so the canvas matches and there are no bars. Null if it can't be read.
    /// </summary>
    public (int Width, int Height)? ProbeDisplaySize(string file)
    {
        if (string.IsNullOrWhiteSpace(file) || !File.Exists(file)) return null;
        try
        {
            var psi = new ProcessStartInfo(_ffmpegPath, "-hide_banner -i " + Quote(file))
            {
                RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return null;
            string err = p.StandardError.ReadToEnd();
            p.WaitForExit(5000);

            string? vline = null;
            foreach (var line in err.Split('\n'))
                if (line.Contains("Video:")) { vline = line; break; }
            if (vline is null) return null;

            var dm = Regex.Match(vline, @"\b(\d{2,5})x(\d{2,5})\b"); // the WxH on the video stream line
            if (!dm.Success) return null;
            int w = int.Parse(dm.Groups[1].Value), h = int.Parse(dm.Groups[2].Value);

            // Display Matrix "rotation of ±90/270 degrees" → ffmpeg auto-rotates, so the displayed frame is
            // the coded size with W/H swapped. (180° keeps the dimensions.)
            var rm = Regex.Match(err, @"rotation of (-?\d+(?:\.\d+)?) degrees");
            if (rm.Success)
            {
                int rot = ((int)Math.Round(Math.Abs(double.Parse(rm.Groups[1].Value, CultureInfo.InvariantCulture)))) % 180;
                if (rot == 90) (w, h) = (h, w);
            }
            return (w, h);
        }
        catch { return null; }
    }

    // ------------------------------------------------------------------ thumbnails

    /// <summary>
    /// Extracts <paramref name="count"/> evenly-spaced thumbnails spanning the
    /// [<paramref name="startSec"/>, startSec+<paramref name="lengthSec"/>] range of
    /// <paramref name="file"/>, scaled to <paramref name="height"/>px tall, into a fresh folder under
    /// %TEMP%. Returns the generated image paths in order (best-effort; may return fewer on failure).
    /// </summary>
    public async Task<List<string>> GenerateFilmstripAsync(
        string file, double startSec, double lengthSec, int count, int height, string baseDir, CancellationToken ct = default)
    {
        var result = new List<string>();
        if (!File.Exists(file) || lengthSec <= 0 || count <= 0) return result;
        if (string.IsNullOrWhiteSpace(baseDir))
            baseDir = Path.Combine(Path.GetTempPath(), "Fragment", "thumbs");

        var dir = Path.Combine(baseDir, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var pattern = Path.Combine(dir, "t%04d.jpg");

        // fps = count/length → one frame every (length/count) seconds across the segment range.
        double fps = count / lengthSec;
        var args = new StringBuilder();
        args.Append("-hide_banner -y ");
        args.Append("-ss ").Append(Time(startSec)).Append(' ');
        args.Append("-i ").Append(Quote(file)).Append(' ');
        args.Append("-t ").Append(Time(lengthSec)).Append(' ');
        args.Append("-vf ").Append(Quote(
            $"fps={fps.ToString("0.######", CultureInfo.InvariantCulture)},scale=-1:{height}")).Append(' ');
        args.Append("-frames:v ").Append(count.ToString(CultureInfo.InvariantCulture)).Append(' ');
        args.Append("-q:v 5 ");
        args.Append(Quote(pattern));

        int exit;
        try
        {
            (exit, _) = await RunAsync(args.ToString(), ct).ConfigureAwait(false);
        }
        catch
        {
            TryDeleteDir(dir);
            throw;
        }
        if (exit != 0) { TryDeleteDir(dir); return result; }

        for (int i = 1; i <= count; i++)
        {
            var p = Path.Combine(dir, $"t{i:0000}.jpg");
            if (File.Exists(p)) result.Add(p);
        }
        if (result.Count == 0) TryDeleteDir(dir); // nothing usable produced
        return result;
    }

    // ------------------------------------------------------------------ export

    /// <summary>
    /// Renders <paramref name="segments"/> (in order) to <paramref name="outputPath"/> applying the
    /// given <paramref name="options"/>. Throws on failure with the ffmpeg stderr tail.
    /// </summary>
    public async Task ExportAsync(
        IReadOnlyList<EditorSegment> segments,
        ExportOptions options,
        string outputPath,
        IProgress<string>? progress = null,
        IProgress<double>? pct = null,
        CancellationToken ct = default)
    {
        if (segments is null || segments.Count == 0)
            throw new ArgumentException("There are no segments to export.", nameof(segments));
        double totalSec = 0;
        foreach (var s in segments)
        {
            if (string.IsNullOrWhiteSpace(s.SourceFile) || !File.Exists(s.SourceFile))
                throw new FileNotFoundException("A segment's source file is missing.", s.SourceFile);
            if (s.Duration <= 0.01)
                throw new InvalidOperationException("A segment has zero length.");
            totalSec += s.Duration;
        }

        var outDir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(outDir)) Directory.CreateDirectory(outDir);

        bool fastEligible = options.Format == EditorOutputFormat.Mp4
            && options.FastCopy
            && options.AudioMode == EditorAudioMode.Keep
            && options.TargetSizeMb is null;

        if (fastEligible)
        {
            progress?.Report("Exporting (fast copy)…");
            try
            {
                await FastCopyExportAsync(segments, outputPath, ct).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                // Fast copy needs identical codecs/streams across segments; on any mismatch (different
                // sources, a video-only clip, etc.) fall through to the full re-encode which normalizes.
                progress?.Report("Fast copy not possible — re-encoding…");
            }
        }

        // Detect which segments' sources actually carry an audio stream, so the graph keeps real audio
        // where present and fills silence where absent — rather than failing or dropping all audio when
        // a video-only clip is in the mix.
        bool wantAudio = options.Format != EditorOutputFormat.Gif && options.AudioMode != EditorAudioMode.Mute;
        var hasAudio = new bool[segments.Count];
        if (wantAudio)
        {
            progress?.Report("Analysing clips…");
            var cache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < segments.Count; i++)
            {
                var f = segments[i].SourceFile;
                if (!cache.TryGetValue(f, out bool h)) { h = await HasAudioAsync(f, ct).ConfigureAwait(false); cache[f] = h; }
                hasAudio[i] = h;
            }
        }

        progress?.Report(options.Format == EditorOutputFormat.Gif ? "Rendering GIF…" : "Rendering…");
        var args = BuildEncodeArguments(segments, options, hasAudio, outputPath);
        var (exit, err) = await RunAsync(args, totalSec, pct, ct).ConfigureAwait(false);

        if (exit != 0)
        {
            // Safety net: if a source we believed had audio actually didn't, retry once fully muted.
            if (wantAudio && LooksLikeMissingAudio(err))
            {
                progress?.Report("A clip's audio couldn't be read — exporting without audio…");
                var (e2, err2) = await RunAsync(BuildEncodeArguments(segments, CloneMuted(options), hasAudio, outputPath), totalSec, pct, ct).ConfigureAwait(false);
                if (e2 != 0)
                {
                    TryDeleteFile(outputPath);
                    throw new InvalidOperationException($"Export failed (ffmpeg exit {e2}).{Environment.NewLine}{Tail(err2)}");
                }
            }
            else
            {
                TryDeleteFile(outputPath);
                throw new InvalidOperationException($"Export failed (ffmpeg exit {exit}).{Environment.NewLine}{Tail(err)}");
            }
        }

        if (!File.Exists(outputPath))
            throw new InvalidOperationException("ffmpeg reported success but no output file was produced.");
    }

    /// <summary>Lossless path: stream-copy each segment to a temp .ts, then concat-copy. MP4 only.</summary>
    private async Task FastCopyExportAsync(IReadOnlyList<EditorSegment> segments, string outputPath, CancellationToken ct)
    {
        var work = Path.Combine(Path.GetTempPath(), "Fragment", "edit", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var parts = new List<string>();
            for (int i = 0; i < segments.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var seg = segments[i];
                var part = Path.Combine(work, $"part{i:000}.ts");
                var a = new StringBuilder();
                a.Append("-hide_banner -y ");
                a.Append("-ss ").Append(Time(seg.InSec)).Append(' ');
                a.Append("-i ").Append(Quote(seg.SourceFile)).Append(' ');
                a.Append("-t ").Append(Time(seg.Duration)).Append(' ');
                // MPEG-TS stream copy concatenates cleanly across parts.
                a.Append("-c copy -avoid_negative_ts make_zero -f mpegts ").Append(Quote(part));
                var (exit, err) = await RunAsync(a.ToString(), ct).ConfigureAwait(false);
                if (exit != 0)
                    throw new InvalidOperationException($"Fast cut failed on segment {i + 1}.{Environment.NewLine}{Tail(err)}");
                parts.Add(part);
            }

            var concat = new StringBuilder("concat:");
            for (int i = 0; i < parts.Count; i++)
            {
                if (i > 0) concat.Append('|');
                concat.Append(parts[i]);
            }

            var args = new StringBuilder();
            args.Append("-hide_banner -y -i ").Append(Quote(concat.ToString())).Append(' ');
            args.Append("-c copy -movflags +faststart ").Append(Quote(outputPath));
            var (cExit, cErr) = await RunAsync(args.ToString(), ct).ConfigureAwait(false);
            if (cExit != 0)
                throw new InvalidOperationException($"Fast join failed.{Environment.NewLine}{Tail(cErr)}");
        }
        finally
        {
            TryDeleteDir(work);
        }
    }

    /// <summary>
    /// Builds a single-pass concat-filter command: each segment is trimmed, scaled+letterboxed to the
    /// output canvas, normalized to a common fps/sar/audio, concatenated, then encoded (MP4 or GIF).
    /// </summary>
    internal static string BuildEncodeArguments(IReadOnlyList<EditorSegment> segments, ExportOptions o, bool[] segHasAudio, string outputPath)
    {
        int w = EnsureEven(Math.Max(2, o.OutWidth));
        int h = EnsureEven(Math.Max(2, o.OutHeight));
        bool gif = o.Format == EditorOutputFormat.Gif;
        bool wantAudio = !gif && o.AudioMode != EditorAudioMode.Mute;

        var sb = new StringBuilder();
        sb.Append("-hide_banner -y ");
        foreach (var s in segments)
            sb.Append("-i ").Append(Quote(s.SourceFile)).Append(' ');

        var filter = new StringBuilder();
        var inv = CultureInfo.InvariantCulture;

        for (int i = 0; i < segments.Count; i++)
        {
            var s = segments[i];
            string inS = s.InSec.ToString("0.###", inv);
            string outS = s.OutSec.ToString("0.###", inv);

            // Video: trim → reset PTS → fit the canvas (preserve aspect, pad with black) → square pixels → common fps.
            filter.Append('[').Append(i).Append(":v]")
                  .Append("trim=start=").Append(inS).Append(":end=").Append(outS).Append(',')
                  .Append("setpts=PTS-STARTPTS,")
                  .Append("scale=").Append(w).Append(':').Append(h).Append(":force_original_aspect_ratio=decrease,")
                  .Append("pad=").Append(w).Append(':').Append(h).Append(":(ow-iw)/2:(oh-ih)/2,")
                  .Append("setsar=1,fps=").Append(o.OutFps.ToString(inv))
                  .Append("[v").Append(i).Append("];");

            if (wantAudio)
            {
                bool segAudio = segHasAudio != null && i < segHasAudio.Length && segHasAudio[i];
                if (segAudio)
                {
                    filter.Append('[').Append(i).Append(":a]")
                          .Append("atrim=start=").Append(inS).Append(":end=").Append(outS).Append(',')
                          .Append("asetpts=PTS-STARTPTS,aresample=48000,aformat=sample_fmts=fltp:channel_layouts=stereo")
                          .Append("[a").Append(i).Append("];");
                }
                else
                {
                    // Source has no audio: synthesize matching silence for the segment's length so the
                    // streams concatenate (and clips that DO have audio keep theirs).
                    filter.Append("anullsrc=channel_layout=stereo:sample_rate=48000,")
                          .Append("atrim=duration=").Append(s.Duration.ToString("0.###", inv)).Append(',')
                          .Append("asetpts=PTS-STARTPTS,aformat=sample_fmts=fltp:channel_layouts=stereo")
                          .Append("[a").Append(i).Append("];");
                }
            }
        }

        // Concat all segment streams.
        for (int i = 0; i < segments.Count; i++)
        {
            filter.Append("[v").Append(i).Append(']');
            if (wantAudio) filter.Append("[a").Append(i).Append(']');
        }
        filter.Append("concat=n=").Append(segments.Count).Append(":v=1:a=").Append(wantAudio ? 1 : 0)
              .Append(wantAudio ? "[vc][ac]" : "[vc]");

        string videoLabel = "[vc]";

        if (gif)
        {
            // Palette pipeline for clean GIF colours; cap height for a sane file size.
            int gifH = Math.Min(h, 360);
            filter.Append(";[vc]fps=").Append(o.GifFps.ToString(inv))
                  .Append(",scale=-2:").Append(gifH).Append(":flags=lanczos,split[gs0][gs1];")
                  .Append("[gs0]palettegen=stats_mode=diff[pal];")
                  .Append("[gs1][pal]paletteuse=dither=bayer:bayer_scale=3[vout]");
            videoLabel = "[vout]";
        }
        else if (wantAudio && o.AudioMode == EditorAudioMode.Volume && Math.Abs(o.Volume - 1.0) > 0.001)
        {
            filter.Append(";[ac]volume=").Append(o.Volume.ToString("0.##", inv)).Append("[aout]");
        }

        sb.Append("-filter_complex ").Append(Quote(filter.ToString())).Append(' ');

        // Mapping.
        sb.Append("-map ").Append(Quote(videoLabel)).Append(' ');
        if (gif)
        {
            sb.Append(Quote(outputPath));
            return sb.ToString();
        }

        if (wantAudio)
        {
            string aLabel = (o.AudioMode == EditorAudioMode.Volume && Math.Abs(o.Volume - 1.0) > 0.001) ? "[aout]" : "[ac]";
            sb.Append("-map ").Append(Quote(aLabel)).Append(' ');
        }

        // Video codec / rate control.
        sb.Append("-c:v libx264 -preset veryfast -pix_fmt yuv420p ");
        if (o.TargetSizeMb is { } mb && mb > 0)
        {
            double totalSec = 0;
            foreach (var s in segments) totalSec += s.Duration;
            int audioKbps = wantAudio ? 160 : 0;
            // bitrate(kbps) = size(bits) / duration(s) / 1000, minus the audio budget. The 0.95 factor
            // leaves headroom for MP4 container overhead so the file lands at/under the target.
            int vKbps = (int)Math.Max(200, (mb * 0.95 * 8.0 * 1024.0) / Math.Max(0.5, totalSec) - audioKbps);
            string k = vKbps.ToString(inv);
            sb.Append("-b:v ").Append(k).Append("k -maxrate ").Append(k).Append("k -bufsize ")
              .Append((vKbps * 2).ToString(inv)).Append("k ");
        }
        else
        {
            sb.Append("-crf 20 ");
        }

        if (wantAudio) sb.Append("-c:a aac -b:a 160k ");
        else sb.Append("-an ");

        sb.Append("-movflags +faststart ").Append(Quote(outputPath));
        return sb.ToString();
    }

    // ------------------------------------------------------------------ process plumbing

    // Overload for callers that don't need progress (probes, fast-copy, two-pass analysis).
    private Task<(int exit, string stderr)> RunAsync(string arguments, CancellationToken ct)
        => RunAsync(arguments, 0, null, ct);

    private async Task<(int exit, string stderr)> RunAsync(string arguments, double totalSec, IProgress<double>? pct, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _ffmpegPath,
            // "-progress pipe:1" emits machine-readable key=value progress to stdout (out_time_us=…), which we
            // turn into a 0..1 fraction against the known total output duration to drive the UI progress bar.
            Arguments = "-progress pipe:1 " + arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var stderr = new StringBuilder();
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };
        process.OutputDataReceived += (_, e) =>
        {
            var line = e.Data;
            if (line is null || pct is null || totalSec <= 0) return;
            if (line.StartsWith("out_time_us=", StringComparison.Ordinal))
            {
                if (long.TryParse(line.AsSpan("out_time_us=".Length), out long us) && us >= 0)
                    pct.Report(Math.Clamp(us / 1_000_000.0 / totalSec, 0.0, 1.0));
            }
            else if (line.StartsWith("progress=end", StringComparison.Ordinal))
            {
                pct.Report(1.0);
            }
        };

        if (!process.Start())
            throw new InvalidOperationException("Failed to start the FFmpeg process.");

        process.BeginErrorReadLine();
        process.BeginOutputReadLine();

        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            throw;
        }

        return (process.ExitCode, stderr.ToString());
    }

    private static bool LooksLikeMissingAudio(string err)
    {
        if (string.IsNullOrEmpty(err)) return false;
        return err.Contains("matches no streams", StringComparison.OrdinalIgnoreCase)
            || err.Contains("do not match any streams", StringComparison.OrdinalIgnoreCase)
            || err.Contains("does not contain any stream", StringComparison.OrdinalIgnoreCase);
    }

    private static ExportOptions CloneMuted(ExportOptions o) => new()
    {
        Format = o.Format,
        OutWidth = o.OutWidth,
        OutHeight = o.OutHeight,
        OutFps = o.OutFps,
        TargetSizeMb = o.TargetSizeMb,
        AudioMode = EditorAudioMode.Mute,
        Volume = 1.0,        // muted: volume is meaningless, don't carry a stale value
        GifFps = o.GifFps,
        FastCopy = false,    // the retry always re-encodes; FastCopy with Mute is a contradiction
    };

    /// <summary>True if <paramref name="file"/> contains an audio stream (probed with ffmpeg itself,
    /// since no ffprobe is bundled). Assumes audio on probe failure so the export still attempts it.</summary>
    private async Task<bool> HasAudioAsync(string file, CancellationToken ct)
    {
        try
        {
            // `ffmpeg -i file` with no output prints stream info to stderr then exits non-zero.
            var (_, err) = await RunAsync($"-hide_banner -i {Quote(file)}", ct).ConfigureAwait(false);
            return err.Contains("Audio:", StringComparison.OrdinalIgnoreCase);
        }
        catch (OperationCanceledException) { throw; }
        catch { return true; }
    }

    private static string Time(double seconds)
        => TimeSpan.FromSeconds(seconds < 0 ? 0 : seconds).ToString(@"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture);

    private static int EnsureEven(int v) => v % 2 == 0 ? v : v + 1;

    private static string Tail(string s, int max = 1200)
        => string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s.Substring(s.Length - max));

    private static string Quote(string value) => $"\"{value}\"";

    private static void TryDeleteDir(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
    }

    private static void TryDeleteFile(string file)
    {
        try { if (File.Exists(file)) File.Delete(file); } catch { }
    }
}
