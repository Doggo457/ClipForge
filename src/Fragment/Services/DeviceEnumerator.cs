using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Fragment.Services;

/// <summary>
/// Enumerates DirectShow capture devices (video + audio) by parsing the output of
/// <c>ffmpeg -hide_banner -list_devices true -f dshow -i dummy</c>.
/// </summary>
public sealed class DeviceEnumerator
{
    private readonly string _ffmpegPath;

    // FFmpeg prints device lines like:
    //   [dshow @ 0000...] "Microphone (Realtek Audio)" (audio)
    //   [dshow @ 0000...] "Integrated Camera" (video)
    // and (on newer builds) an alternative-name line we want to ignore.
    private static readonly Regex DeviceLineRegex = new(
        "\"(?<name>[^\"]+)\"\\s*\\((?<type>audio|video)\\)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex AltNameRegex = new(
        "Alternative name",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public DeviceEnumerator(string ffmpegPath)
    {
        if (string.IsNullOrWhiteSpace(ffmpegPath))
            throw new ArgumentException("FFmpeg path must be provided.", nameof(ffmpegPath));

        _ffmpegPath = ffmpegPath;
    }

    /// <summary>
    /// Runs FFmpeg's dshow device listing and returns the discovered video and audio device names.
    /// </summary>
    /// <remarks>
    /// FFmpeg intentionally exits with a non-zero code for this command (the "dummy" input is never
    /// opened), so the exit code is ignored and only stderr is parsed.
    /// </remarks>
    public async Task<(List<string> Video, List<string> Audio)> ListDshowDevicesAsync()
    {
        var video = new List<string>();
        var audio = new List<string>();

        var psi = new ProcessStartInfo
        {
            FileName = _ffmpegPath,
            Arguments = "-hide_banner -list_devices true -f dshow -i dummy",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };

        var stderr = new StringBuilder();

        using (var process = new Process { StartInfo = psi, EnableRaisingEvents = true })
        {
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                    stderr.AppendLine(e.Data);
            };
            process.OutputDataReceived += (_, _) => { /* device list goes to stderr */ };

            if (!process.Start())
                throw new InvalidOperationException("Failed to start the FFmpeg process for device enumeration.");

            process.BeginErrorReadLine();
            process.BeginOutputReadLine();

            await process.WaitForExitAsync().ConfigureAwait(false);
        }

        ParseDeviceOutput(stderr.ToString(), video, audio);
        return (video, audio);
    }

    /// <summary>
    /// Parses FFmpeg dshow listing output into the supplied device lists.
    /// Kept internal/static for unit testing without launching a process.
    /// </summary>
    internal static void ParseDeviceOutput(string output, List<string> video, List<string> audio)
    {
        if (string.IsNullOrEmpty(output))
            return;

        var lines = output.Split('\n');
        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r');

            // Skip the "Alternative name" lines that follow each device on newer FFmpeg builds.
            if (AltNameRegex.IsMatch(line))
                continue;

            var match = DeviceLineRegex.Match(line);
            if (!match.Success)
                continue;

            var name = match.Groups["name"].Value.Trim();
            var type = match.Groups["type"].Value.ToLowerInvariant();

            if (name.Length == 0)
                continue;

            if (type == "video")
            {
                if (!video.Contains(name))
                    video.Add(name);
            }
            else if (type == "audio")
            {
                if (!audio.Contains(name))
                    audio.Add(name);
            }
        }
    }
}
