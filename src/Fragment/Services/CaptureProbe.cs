using System;
using System.Diagnostics;
using Fragment.Utils;

namespace Fragment.Services;

/// <summary>
/// Runtime check for whether the GPU Desktop Duplication backend (ddagrab) can actually capture
/// right now. It can't see exclusive-fullscreen games or DRM-protected content, so when it can't,
/// callers transparently fall back to the gdigrab (GDI) path instead of recording a black screen.
/// </summary>
internal static class CaptureProbe
{
    /// <summary>
    /// Runs a tiny two-frame ddagrab capture and reports whether it succeeded. Exit code 0 means
    /// Desktop Duplication is available for the primary output at this instant; any non-zero exit
    /// (or a hang) means it isn't (game in exclusive fullscreen, no DDA support, protected output).
    /// Typically ~0.2s (a real block exits fast); bounded to ~2.4s worst case if the GPU is wedged
    /// and both attempts have to hit the timeout.
    /// </summary>
    public static bool IsDesktopDuplicationWorking(string ffmpegPath)
    {
        if (string.IsNullOrWhiteSpace(ffmpegPath))
        {
            return false;
        }

        // Retry once: a real exclusive-fullscreen block fails every time, but a momentary
        // access-lost (mode switch, UAC secure desktop, a brief flip) can fail one instant and
        // succeed the next — we don't want a transient blip to drop the whole session to 60fps.
        return ProbeOnce(ffmpegPath) || ProbeOnce(ffmpegPath);
    }

    private static bool ProbeOnce(string ffmpegPath)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = "-hide_banner -loglevel error -f lavfi -i ddagrab=output_idx=0:framerate=30 -frames:v 2 -f null -",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };

            using var p = Process.Start(psi);
            if (p is null)
            {
                return false;
            }

            ChildProcessTracker.Track(p); // never outlive the app, even if it hangs
            p.BeginErrorReadLine();        // drain pipes so the child can't block
            p.BeginOutputReadLine();

            // 1200ms per attempt keeps the worst case (~2.4s over two attempts) tolerable if ddagrab
            // ever wedges; a normal block exits in well under this.
            if (!p.WaitForExit(1200))
            {
                try
                {
                    p.Kill(entireProcessTree: true);
                    p.WaitForExit(1000); // Kill is async — reap the child before disposing
                }
                catch { }
                return false;
            }

            p.WaitForExit(); // flush the async stdout/stderr reads before the using disposes p
            return p.ExitCode == 0;
        }
        catch
        {
            return false; // any failure: use the safe gdigrab path
        }
    }
}
