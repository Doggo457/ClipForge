using System;
using System.IO;

namespace Fragment.Services;

/// <summary>
/// Locates and validates the bundled (or system) ffmpeg.exe binary.
/// Resolution order:
///   1. An explicitly configured path (from settings).
///   2. A bundled copy at ./ffmpeg/ffmpeg.exe next to the running executable.
///   3. The first "ffmpeg.exe" found on the PATH environment variable.
/// </summary>
public static class FfmpegLocator
{
    private const string ExecutableName = "ffmpeg.exe";

    /// <summary>
    /// Attempts to resolve a usable path to ffmpeg.exe. Returns <c>null</c> if none can be found.
    /// </summary>
    public static string? Find(string? configuredPath)
    {
        // 1. Explicitly configured path wins if it is valid.
        if (IsValid(configuredPath))
        {
            return Path.GetFullPath(configuredPath!);
        }

        // 2. Bundled copy next to the executable: ./ffmpeg/ffmpeg.exe
        var baseDir = AppContext.BaseDirectory;
        if (!string.IsNullOrEmpty(baseDir))
        {
            var bundled = Path.Combine(baseDir, "ffmpeg", ExecutableName);
            if (IsValid(bundled))
            {
                return Path.GetFullPath(bundled);
            }

            // Also accept ffmpeg.exe directly alongside the executable.
            var sideBySide = Path.Combine(baseDir, ExecutableName);
            if (IsValid(sideBySide))
            {
                return Path.GetFullPath(sideBySide);
            }
        }

        // 3. Search every directory on PATH.
        var pathVar = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(pathVar))
        {
            foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                string candidate;
                try
                {
                    candidate = Path.Combine(dir.Trim(), ExecutableName);
                }
                catch (ArgumentException)
                {
                    // Skip malformed PATH entries (invalid characters).
                    continue;
                }

                if (IsValid(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Returns true when <paramref name="path"/> points at an existing ffmpeg executable file.
    /// </summary>
    public static bool IsValid(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            return File.Exists(path);
        }
        catch (Exception)
        {
            // Defensive: a malformed path string should never throw out of a validity check.
            return false;
        }
    }
}
