using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace Fragment.Services;

/// <summary>
/// Trims a section out of an existing media file using FFmpeg.
/// Supports both fast stream-copy (keyframe-accurate) and full re-encode (frame-accurate).
/// </summary>
public sealed class ClipTrimmer
{
    private readonly string _ffmpegPath;

    public ClipTrimmer(string ffmpegPath)
    {
        if (string.IsNullOrWhiteSpace(ffmpegPath))
            throw new ArgumentException("FFmpeg path must be provided.", nameof(ffmpegPath));

        _ffmpegPath = ffmpegPath;
    }

    /// <summary>
    /// Trims <paramref name="inputPath"/> from <paramref name="start"/> to <paramref name="end"/>
    /// and writes the result to <paramref name="outputPath"/>.
    /// </summary>
    /// <param name="reEncode">
    /// When false, uses <c>-c copy</c> for a near-instant, lossless trim (cuts land on keyframes).
    /// When true, re-encodes for frame-accurate cut points.
    /// </param>
    /// <returns>The output path on success.</returns>
    public async Task<string> TrimAsync(
        string inputPath,
        TimeSpan start,
        TimeSpan end,
        string outputPath,
        bool reEncode)
    {
        if (string.IsNullOrWhiteSpace(inputPath))
            throw new ArgumentException("Input path must be provided.", nameof(inputPath));
        if (string.IsNullOrWhiteSpace(outputPath))
            throw new ArgumentException("Output path must be provided.", nameof(outputPath));
        if (!File.Exists(inputPath))
            throw new FileNotFoundException("Input media file was not found.", inputPath);
        if (end <= start)
            throw new ArgumentException("End time must be greater than start time.", nameof(end));

        var outputDir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(outputDir))
            Directory.CreateDirectory(outputDir);

        var duration = end - start;
        var arguments = BuildArguments(inputPath, start, duration, outputPath, reEncode);

        var psi = new ProcessStartInfo
        {
            FileName = _ffmpegPath,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

        var stderr = new StringBuilder();
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                stderr.AppendLine(e.Data);
        };
        process.OutputDataReceived += (_, _) => { /* ffmpeg writes progress to stderr */ };

        if (!process.Start())
            throw new InvalidOperationException("Failed to start the FFmpeg process.");

        process.BeginErrorReadLine();
        process.BeginOutputReadLine();

        await process.WaitForExitAsync().ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"FFmpeg trim failed (exit code {process.ExitCode}).{Environment.NewLine}{stderr}");
        }

        if (!File.Exists(outputPath))
        {
            throw new InvalidOperationException(
                $"FFmpeg reported success but the output file was not created: {outputPath}");
        }

        return outputPath;
    }

    /// <summary>
    /// Builds the FFmpeg argument string for a trim operation. Kept internal/static so it is
    /// testable in isolation from process launching.
    /// </summary>
    internal static string BuildArguments(
        string inputPath,
        TimeSpan start,
        TimeSpan duration,
        string outputPath,
        bool reEncode)
    {
        // Placing -ss before -i enables fast input seeking; -t after -i bounds the duration.
        var sb = new StringBuilder();
        sb.Append("-hide_banner -y ");
        sb.Append("-ss ").Append(FormatTime(start)).Append(' ');
        sb.Append("-i ").Append(Quote(inputPath)).Append(' ');
        sb.Append("-t ").Append(FormatTime(duration)).Append(' ');

        if (reEncode)
        {
            // Frame-accurate cuts require a real re-encode.
            sb.Append("-c:v libx264 -preset veryfast -crf 20 ");
            sb.Append("-c:a aac -b:a 160k ");
            sb.Append("-movflags +faststart ");
        }
        else
        {
            // Lossless, fast stream copy. Avoid negative timestamps for clean concat/copy.
            sb.Append("-c copy -avoid_negative_ts make_zero ");
        }

        sb.Append(Quote(outputPath));
        return sb.ToString();
    }

    private static string FormatTime(TimeSpan ts) =>
        ts.ToString(@"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture);

    private static string Quote(string value) => $"\"{value}\"";
}
