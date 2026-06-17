using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Fragment.Services;

/// <summary>Details of a newer release found on GitHub. Size is the asset's byte length (integrity check).</summary>
public sealed record UpdateInfo(Version Version, string Tag, string DownloadUrl, long Size);

/// <summary>
/// Checks the GitHub Releases API for a newer Fragment build, downloads the self-contained exe, and
/// (when the user confirms) swaps it in via a small detached script that waits for this process to
/// exit, overwrites the running exe, and relaunches.
/// </summary>
public sealed class UpdateService
{
    private const string LatestApi = "https://api.github.com/repos/Doggo457/Fragment/releases/latest";
    private const string AssetName = "Fragment-win-x64.exe";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Fragment-Updater");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    /// <summary>This build's version, normalized to major.minor.build.</summary>
    public static Version CurrentVersion => Normalize(Assembly.GetExecutingAssembly().GetName().Version);

    /// <summary>Returns details of a newer release, or null if up to date / offline / on error.</summary>
    public async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await Http.GetAsync(LatestApi, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;

            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            var root = doc.RootElement;

            if (!root.TryGetProperty("tag_name", out var tagEl)) return null;
            string tag = tagEl.GetString() ?? "";
            var latest = ParseTag(tag);
            if (latest is null || latest <= CurrentVersion) return null;

            string? url = null;
            long size = 0;
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in assets.EnumerateArray())
                {
                    if (a.TryGetProperty("name", out var n)
                        && string.Equals(n.GetString(), AssetName, StringComparison.Ordinal)
                        && a.TryGetProperty("browser_download_url", out var u))
                    {
                        url = u.GetString();
                        if (a.TryGetProperty("size", out var sz) && sz.TryGetInt64(out var s)) size = s;
                        break;
                    }
                }
            }

            // Only accept an https github.com asset URL (the asset then redirects to GitHub's own CDN).
            if (string.IsNullOrEmpty(url) || !IsTrustedUrl(url!)) return null;
            return new UpdateInfo(latest, tag, url!, size);
        }
        catch
        {
            return null; // offline / rate-limited / parse error — silently skip
        }
    }

    /// <summary>Downloads the release asset to %TEMP% and returns its path.</summary>
    public async Task<string> DownloadAsync(UpdateInfo info, CancellationToken ct = default)
    {
        if (!IsTrustedUrl(info.DownloadUrl))
            throw new InvalidOperationException("Update URL is not a trusted GitHub URL.");

        var dir = Path.Combine(Path.GetTempPath(), "Fragment", "update");
        Directory.CreateDirectory(dir);
        var dest = Path.Combine(dir, $"Fragment-{SanitizeTag(info.Tag)}.exe");

        // Reuse a prior COMPLETE download (verified by exact size), so we don't refetch ~75MB each launch.
        if (File.Exists(dest) && info.Size > 0 && new FileInfo(dest).Length == info.Size) return dest;

        var tmp = dest + ".tmp";
        try
        {
            using (var resp = await Http.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                resp.EnsureSuccessStatusCode();
                await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var fs = File.Create(tmp);
                await src.CopyToAsync(fs, ct).ConfigureAwait(false);
            }

            // Integrity: the finished file must match the size GitHub reported for the asset (catches a
            // truncated/corrupt download before we ever run it).
            if (info.Size > 0 && new FileInfo(tmp).Length != info.Size)
                throw new InvalidOperationException("Downloaded update size did not match the release asset — aborting.");

            File.Move(tmp, dest, overwrite: true);
            return dest;
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            throw;
        }
    }

    /// <summary>
    /// Launches a detached script that waits for this process to exit, overwrites the running exe with
    /// the downloaded one, relaunches it, then cleans up. The caller should shut the app down right after.
    /// </summary>
    public bool ApplyAndRestart(string newExePath)
    {
        try
        {
            string? current = Environment.ProcessPath;
            if (string.IsNullOrEmpty(current) || !File.Exists(newExePath)) return false;

            int pid = Environment.ProcessId;
            var dir = Path.Combine(Path.GetTempPath(), "Fragment", "update");
            Directory.CreateDirectory(dir);
            var bat = Path.Combine(dir, $"apply_{Guid.NewGuid():N}.cmd");

            // Wait for THIS pid to exit, copy the new exe over the running one (with retries to ride out
            // the brief post-exit file lock / AV scan), relaunch, then self-delete. If the copy keeps
            // failing the original exe is untouched, so we relaunch it (the app still works, just not
            // updated) and keep the downloaded exe.
            string script =
                "@echo off\r\n" +
                ":wait\r\n" +
                $"tasklist /fi \"PID eq {pid}\" /fo csv /nh | find \"{pid}\" >nul && (ping -n 2 127.0.0.1 >nul & goto wait)\r\n" +
                "set /a tries=0\r\n" +
                ":copy\r\n" +
                $"copy /y \"{newExePath}\" \"{current}\" >nul\r\n" +
                "if not errorlevel 1 goto done\r\n" +
                "set /a tries+=1\r\n" +
                "if %tries% lss 6 (ping -n 2 127.0.0.1 >nul & goto copy)\r\n" +
                $"start \"\" \"{current}\"\r\n" +
                "del \"%~f0\" >nul 2>&1\r\n" +
                "exit /b 1\r\n" +
                ":done\r\n" +
                $"start \"\" \"{current}\"\r\n" +
                $"del \"{newExePath}\" >nul 2>&1\r\n" +
                "del \"%~f0\" >nul 2>&1\r\n";
            File.WriteAllText(bat, script);

            var p = Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"{bat}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            return p != null;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsTrustedUrl(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var u)
           && u.Scheme == Uri.UriSchemeHttps
           && (u.Host == "github.com" || u.Host.EndsWith(".github.com", StringComparison.OrdinalIgnoreCase));

    // Keep only filename-safe characters from the tag so it can't break/inject into the apply script.
    private static string SanitizeTag(string tag)
    {
        var clean = new string((tag ?? "").Where(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_').ToArray());
        return string.IsNullOrEmpty(clean) ? "latest" : clean;
    }

    private static Version? ParseTag(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        tag = tag.TrimStart('v', 'V').Trim();
        // Tolerate a pre-release suffix like "1.4.0-beta" by parsing only the numeric part.
        int dash = tag.IndexOf('-');
        if (dash > 0) tag = tag.Substring(0, dash);
        return Version.TryParse(tag, out var v) ? Normalize(v) : null;
    }

    // Compare on major.minor.build only (ignore the 4th/revision field, which is 0 vs -1 inconsistently).
    private static Version Normalize(Version? v)
        => v is null ? new Version(0, 0, 0) : new Version(v.Major, Math.Max(0, v.Minor), Math.Max(0, v.Build));
}
