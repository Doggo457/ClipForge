using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Fragment.Services;

/// <summary>
/// Ensures a usable ffmpeg.exe is available so Fragment works "launch and record"
/// out of the box. Resolution order:
///   1. Whatever <see cref="FfmpegLocator"/> can already find (configured path,
///      a copy next to the exe, or PATH).
///   2. A previously auto-downloaded copy in %AppData%\Fragment\ffmpeg.
///   3. A fresh download of a static win64 build (first run only).
/// </summary>
public static class FfmpegProvider
{
    // BtbN publishes static, self-contained Windows builds (single ffmpeg.exe, no extra DLLs).
    private const string DownloadUrl =
        "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip";

    /// <summary>Folder where an auto-downloaded ffmpeg.exe is stored.</summary>
    public static string TargetDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Fragment", "ffmpeg");

    /// <summary>Full path of the managed ffmpeg.exe copy.</summary>
    public static string TargetExe => Path.Combine(TargetDir, "ffmpeg.exe");

    /// <summary>
    /// Returns a valid path to ffmpeg.exe, downloading and extracting it on first run
    /// if necessary. Returns <c>null</c> if it could not be resolved or downloaded.
    /// </summary>
    public static async Task<string?> EnsureAsync(
        string? configuredPath,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        // 1. Already locatable somewhere?
        progress?.Report("Locating FFmpeg…");
        var existing = FfmpegLocator.Find(configuredPath);
        if (FfmpegLocator.IsValid(existing))
        {
            return existing;
        }

        // 2. Already auto-downloaded previously?
        if (File.Exists(TargetExe))
        {
            return TargetExe;
        }

        // 3. Download a fresh static build.
        try
        {
            Directory.CreateDirectory(TargetDir);
            var zipPath = Path.Combine(TargetDir, "ffmpeg-download.zip");

            progress?.Report("Downloading FFmpeg (one-time, ~80 MB)…");
            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) })
            using (var response = await http.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
            {
                response.EnsureSuccessStatusCode();
                long total = response.Content.Headers.ContentLength ?? -1L;

                using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var dest = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, true);

                var buffer = new byte[1 << 20];
                long readTotal = 0;
                int read;
                int lastPct = -1;
                while ((read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
                {
                    await dest.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    readTotal += read;
                    if (total > 0)
                    {
                        int pct = (int)(readTotal * 100 / total);
                        if (pct != lastPct)
                        {
                            lastPct = pct;
                            progress?.Report($"Downloading FFmpeg… {pct}%");
                        }
                    }
                }
            }

            progress?.Report("Extracting FFmpeg…");
            using (var archive = ZipFile.OpenRead(zipPath))
            {
                var entry =
                    archive.Entries.FirstOrDefault(e =>
                        e.FullName.EndsWith("/bin/ffmpeg.exe", StringComparison.OrdinalIgnoreCase))
                    ?? archive.Entries.FirstOrDefault(e =>
                        string.Equals(e.Name, "ffmpeg.exe", StringComparison.OrdinalIgnoreCase));

                if (entry is null)
                {
                    progress?.Report("FFmpeg archive did not contain ffmpeg.exe.");
                    return null;
                }

                entry.ExtractToFile(TargetExe, overwrite: true);
            }

            try { File.Delete(zipPath); } catch { /* non-fatal */ }

            return File.Exists(TargetExe) ? TargetExe : null;
        }
        catch (Exception ex)
        {
            progress?.Report($"FFmpeg download failed: {ex.Message}");
            return null;
        }
    }
}
