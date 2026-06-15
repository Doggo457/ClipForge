using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using ClipForge.Models;

namespace ClipForge.Services;

/// <summary>
/// Detects which GPU hardware H.264 encoder actually works on this machine by attempting a tiny
/// throwaway encode with each candidate. Listing an encoder via "ffmpeg -encoders" is not enough
/// (NVENC/AMF/QSV are all listed regardless of hardware), so we genuinely test-encode a few frames.
/// Hardware encoding offloads work to the GPU, dropping CPU usage from tens-of-percent to near zero.
/// </summary>
public static class HardwareEncoderDetector
{
    private static readonly (VideoEncoder Encoder, string Codec)[] Candidates =
    {
        (VideoEncoder.NVENC_H264, "h264_nvenc"),  // NVIDIA
        (VideoEncoder.AMF_H264,   "h264_amf"),    // AMD
        (VideoEncoder.QSV_H264,   "h264_qsv"),    // Intel Quick Sync
    };

    /// <summary>
    /// Returns the best working GPU encoder, or <c>null</c> if none are usable (caller keeps software x264).
    /// </summary>
    public static async Task<VideoEncoder?> DetectBestAsync(string ffmpegPath)
    {
        if (string.IsNullOrWhiteSpace(ffmpegPath))
        {
            return null;
        }

        foreach (var (encoder, codec) in Candidates)
        {
            if (await TestEncoderAsync(ffmpegPath, codec).ConfigureAwait(false))
            {
                return encoder;
            }
        }

        return null;
    }

    private static async Task<bool> TestEncoderAsync(string ffmpegPath, string codec)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                // Mirror the real recording settings (nv12 + target bitrate at HD) so an encoder is
                // only adopted if it genuinely initialises with the parameters we use — AMF in
                // particular passes a trivial test but fails a real HD/nv12/bitrate encode.
                Arguments =
                    "-hide_banner -loglevel error -f lavfi -i color=c=black:s=1280x720:r=60:d=0.3 " +
                    $"-c:v {codec} -pix_fmt nv12 -b:v 8000k -f null -",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };

            using var process = Process.Start(psi);
            if (process is null)
            {
                return false;
            }

            // Drain pipes so the process can't block on a full buffer.
            _ = process.StandardError.ReadToEndAsync();
            _ = process.StandardOutput.ReadToEndAsync();

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);

            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
