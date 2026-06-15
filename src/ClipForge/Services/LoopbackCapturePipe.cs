using System;
using System.IO.Pipes;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace ClipForge.Services;

/// <summary>
/// Describes a raw-PCM audio input that ffmpeg should read from a named pipe.
/// </summary>
public sealed record LoopbackInfo(string InputPath, string Format, int SampleRate, int Channels);

/// <summary>
/// Captures real desktop/system audio using WASAPI loopback (the same technique OBS uses — no
/// "Stereo Mix" device required) and streams the raw PCM to a Windows named pipe that ffmpeg
/// reads as an input. A silent keep-alive render stream keeps the endpoint active so loopback
/// keeps delivering frames even when nothing is playing (avoiding audio drift).
/// </summary>
public sealed class LoopbackCapturePipe : IDisposable
{
    private readonly string _pipeName;
    private NamedPipeServerStream? _server;
    private WasapiLoopbackCapture? _capture;
    private IWavePlayer? _silence;
    private volatile bool _connected;
    private volatile bool _disposed;

    public LoopbackCapturePipe()
    {
        _pipeName = "clipforge_sys_" + Guid.NewGuid().ToString("N");
    }

    /// <summary>The path ffmpeg uses to read the pipe, e.g. <c>\\.\pipe\clipforge_sys_xxxx</c>.</summary>
    public string FfmpegInputPath => $@"\\.\pipe\{_pipeName}";

    /// <summary>Sample rate of the captured audio (device native, typically 48000).</summary>
    public int SampleRate { get; private set; } = 48000;

    /// <summary>Channel count of the captured audio (typically 2).</summary>
    public int Channels { get; private set; } = 2;

    /// <summary>The ffmpeg raw-format token for the PCM stream (f32le or s16le).</summary>
    public string FfmpegFormat { get; private set; } = "f32le";

    /// <summary>
    /// Begins WASAPI loopback capture and opens the named pipe for ffmpeg to connect to.
    /// Throws if WASAPI is unavailable; callers should fall back gracefully.
    /// </summary>
    public void Start()
    {
        _capture = new WasapiLoopbackCapture();
        var fmt = _capture.WaveFormat;
        SampleRate = fmt.SampleRate;
        Channels = fmt.Channels;
        FfmpegFormat = fmt.Encoding == WaveFormatEncoding.IeeeFloat
            ? "f32le"
            : (fmt.BitsPerSample == 16 ? "s16le" : "f32le");

        _server = new NamedPipeServerStream(
            _pipeName, PipeDirection.Out, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous,
            1 << 20, 1 << 20);

        _server.BeginWaitForConnection(OnPipeConnected, null);

        // Keep the default render endpoint active with inaudible silence so loopback always
        // delivers PCM frames (WASAPI loopback otherwise stalls when the system is silent).
        try
        {
            var silentOut = new WasapiOut(AudioClientShareMode.Shared, 100);
            silentOut.Init(new SilenceProvider(_capture.WaveFormat));
            silentOut.Play();
            _silence = silentOut;
        }
        catch
        {
            // Keep-alive is best-effort; capture still works while audio is playing.
        }

        _capture.DataAvailable += OnDataAvailable;
        _capture.StartRecording();
    }

    private void OnPipeConnected(IAsyncResult ar)
    {
        try
        {
            _server?.EndWaitForConnection(ar);
            _connected = true;
        }
        catch
        {
            // Server was disposed before ffmpeg connected; ignore.
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (_disposed || !_connected || _server is null || e.BytesRecorded <= 0)
        {
            return;
        }

        try
        {
            _server.Write(e.Buffer, 0, e.BytesRecorded);
        }
        catch
        {
            // ffmpeg closed the pipe (recording stopped) — stop feeding it.
            _connected = false;
        }
    }

    public void Dispose()
    {
        _disposed = true;

        try { _capture?.StopRecording(); } catch { }
        try { _capture?.Dispose(); } catch { }
        try { _silence?.Stop(); } catch { }
        try { _silence?.Dispose(); } catch { }
        try { _server?.Dispose(); } catch { }

        _capture = null;
        _silence = null;
        _server = null;
    }
}
