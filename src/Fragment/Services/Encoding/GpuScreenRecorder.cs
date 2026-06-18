using System;
using System.IO;
using System.Threading.Tasks;
using Fragment.Models;

namespace Fragment.Services.Encoding;

/// <summary>
/// Direct-recording engine that uses the in-process all-GPU pipeline (<see cref="GpuVideoRecorder"/>):
/// WGC capture → GPU BGRA→NV12 → hardware H.264 + AAC → MP4, at near-zero CPU. Exposes the same surface
/// as <see cref="ScreenRecorder"/> so the view-model can pick either engine. Only MP4 full-screen/monitor
/// captures are supported (see <see cref="CanHandle"/>); the view-model falls back to ffmpeg otherwise.
/// </summary>
public sealed class GpuScreenRecorder : IScreenRecorder
{
    private readonly object _gate = new();
    private GpuRecordingDevice? _device;
    private GpuVideoRecorder? _recorder;
    private bool _recording;

    public bool IsRecording { get { lock (_gate) return _recording; } }
    public string? CurrentOutputPath { get; private set; }

    /// <summary>Reason the last start attempt failed (the view-model falls back to ffmpeg instead of erroring).</summary>
    public string? LastStartError { get; private set; }

    public event EventHandler? Started;
    public event EventHandler<string>? Stopped;
#pragma warning disable CS0067 // required by IScreenRecorder; start failures fall back silently, reserved for mid-recording faults
    public event EventHandler<string>? Error;
#pragma warning restore CS0067

    /// <summary>The GPU engine handles MP4 full-screen / single-monitor capture (Media Foundation muxes MP4).</summary>
    public static bool CanHandle(RecordingProfile p) =>
        p.Container == OutputContainer.Mp4 &&
        p.Source is CaptureSource.FullScreen or CaptureSource.Monitor;

    public async Task StartAsync(RecordingProfile profile)
    {
        if (profile is null) throw new ArgumentNullException(nameof(profile));
        if (IsRecording) throw new InvalidOperationException("A recording is already in progress.");
        LastStartError = null;
        if (!CanHandle(profile)) { LastStartError = "unsupported profile"; return; }

        string outputPath = ScreenRecorder.ResolveOutputPath(profile);
        int fps = profile.Fps > 0 ? profile.Fps : 60;
        int videoBps = Math.Max(1_000_000, profile.VideoBitrateKbps * 1000);
        int audioBps = Math.Max(64_000, profile.AudioBitrateKbps * 1000);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            IntPtr hmon = ResolveMonitor(profile);

            await Task.Run(() =>
            {
                var dev = new GpuRecordingDevice();
                GpuVideoRecorder rec;
                try
                {
                    rec = new GpuVideoRecorder(dev, hmon, outputPath, fps, videoBps,
                        profile.CaptureCursor, profile.Audio, audioBps);
                }
                catch { dev.Dispose(); throw; }

                rec.Start();
                lock (_gate) { _device = dev; _recorder = rec; CurrentOutputPath = outputPath; _recording = true; }
            }).ConfigureAwait(false);

            Started?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            lock (_gate) { _recording = false; _recorder = null; _device = null; }
            // Don't surface an error: the caller checks IsRecording and falls back to the ffmpeg engine.
            LastStartError = ex.Message;
        }
    }

    public async Task StopAsync()
    {
        GpuVideoRecorder? rec; GpuRecordingDevice? dev; string? path;
        lock (_gate)
        {
            if (!_recording) return;
            rec = _recorder; dev = _device; path = CurrentOutputPath;
            _recorder = null; _device = null; _recording = false;
        }

        await Task.Run(() =>
        {
            try { rec?.Dispose(); } catch { }   // finalizes the MP4 (writes moov)
            try { dev?.Dispose(); } catch { }
        }).ConfigureAwait(false);

        CurrentOutputPath = null;
        Stopped?.Invoke(this, path ?? string.Empty);
    }

    private static IntPtr ResolveMonitor(RecordingProfile p)
    {
        if (p.Source == CaptureSource.Monitor)
        {
            var mon = MonitorEnumerator.GetByIndex(p.MonitorIndex);
            return mon is not null
                ? WgcCapture.MonitorFromPoint(mon.X + 1, mon.Y + 1)
                : WgcCapture.MonitorFromPoint(0, 0);
        }
        return WgcCapture.MonitorFromPoint(0, 0); // primary
    }
}
