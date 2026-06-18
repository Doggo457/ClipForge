using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Vortice.Direct3D11;

namespace Fragment.Services.Encoding;

/// <summary>
/// End-to-end GPU video recorder: WGC capture (frames kept on the GPU) → fixed-function BGRA→NV12
/// conversion → in-process hardware H.264 encode → MP4, with no CPU touching the pixels.
///
/// A dedicated even-cadence "FrameFeeder" thread drives the pipeline: each tick it snapshots the latest
/// captured frame (repeating the previous one if capture has stalled, which keeps motion smooth) and
/// emits exactly one CFR frame. Pacing is decoupled from the encoder so encode/disk hiccups don't make
/// the cadence stutter. A small ring of input/NV12 textures lets the async encoder drain without the
/// feeder overwriting a frame still in flight.
/// </summary>
public sealed class GpuVideoRecorder : IDisposable
{
    [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint ms);
    [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint ms);

    private const int PoolSize = 10; // ~166 ms of headroom at 60 fps for the async encoder to drain

    private readonly GpuRecordingDevice _gpu;
    private readonly GpuWgcCapture _cap;
    private readonly VideoProcessorConverter _conv;
    private readonly MfH264SinkWriter _writer;
    private readonly int _fps;
    private readonly ID3D11Texture2D[] _inputPool = new ID3D11Texture2D[PoolSize];
    private readonly ID3D11Texture2D[] _nv12Pool = new ID3D11Texture2D[PoolSize];

    private Thread? _thread;
    private volatile bool _running;
    private bool _timerRaised;
    private readonly Action<string>? _diag;

    public int Width => _conv.Width;
    public int Height => _conv.Height;
    public long FramesEmitted { get; private set; }
    public long CopyFalseCount { get; private set; }
    public string? LastError { get; private set; }
    public long ArrivedCount => _cap.ArrivedCount;

    public GpuVideoRecorder(GpuRecordingDevice gpu, IntPtr hmon, string path, int fps, int bitrate, bool captureCursor, Action<string>? diag = null)
    {
        _diag = diag;
        _gpu = gpu;
        _fps = fps > 0 ? fps : 60;
        _cap = new GpuWgcCapture(gpu, hmon, captureCursor);
        if (!_cap.WaitForFirstFrame(5000, out int w, out int h))
        {
            _cap.Dispose();
            throw new InvalidOperationException("GPU recorder: no frame captured within 5s.");
        }

        _conv = new VideoProcessorConverter(gpu, w, h, _fps);
        _writer = new MfH264SinkWriter(gpu, path, _conv.Width, _conv.Height, _fps, bitrate);
        for (int i = 0; i < PoolSize; i++)
        {
            _inputPool[i] = _conv.CreateInputTexture();
            _nv12Pool[i] = _conv.CreateNv12Texture();
        }
    }

    public void Start()
    {
        if (_running) return;
        _running = true;
        try { timeBeginPeriod(1); _timerRaised = true; } catch { }
        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal,
            Name = "GpuFrameFeeder",
        };
        _thread.Start();
    }

    private void Loop()
    {
        var sw = Stopwatch.StartNew();
        long freq = Stopwatch.Frequency;
        long frame = 0;

        while (_running)
        {
            // Even-cadence deadline for this frame index (no drift: derived from the absolute index).
            // Sleep (1 ms timer resolution) without spinning — the output is CFR (timestamps come from the
            // frame index), so ±1 ms sampling jitter is invisible, and not spinning keeps CPU near zero.
            long deadline = frame * freq / _fps;
            long now = sw.ElapsedTicks;
            if (deadline > now)
            {
                int ms = (int)((deadline - now) * 1000 / freq);
                if (ms > 0) Thread.Sleep(ms);
            }

            int slot = (int)(frame % PoolSize);
            var input = _inputPool[slot];
            var nv12 = _nv12Pool[slot];

            bool trace = frame < 3 || frame % 60 == 0;
            try
            {
                // Pull + convert + encode, all on this one thread. If capture stalled, CopyLatestInto
                // re-copies the previous frame, so we still emit on cadence (frame-repeat) → smooth motion.
                if (trace) _diag?.Invoke($"  f{frame}: copy+convert...");
                if (_cap.CopyLatestInto(input))
                {
                    _conv.Convert(input, nv12);
                    _gpu.Context.Flush(); // submit the Blt so the encoder sees finished NV12
                    long t0 = frame * 10_000_000L / _fps;
                    long t1 = (frame + 1) * 10_000_000L / _fps;
                    if (trace) _diag?.Invoke($"  f{frame}: write...");
                    _writer.WriteFrame(nv12, t0, t1 - t0);
                    if (trace) _diag?.Invoke($"  f{frame}: done (emitted={FramesEmitted + 1})");
                    FramesEmitted++;
                }
                else { CopyFalseCount++; }
            }
            catch (Exception ex)
            {
                LastError = $"frame {frame}: {ex.GetType().Name}: {ex.Message.Split('\n')[0]}";
                _diag?.Invoke("  LOOP FAIL: " + LastError);
                break;
            }

            frame++;
        }
    }

    public void Stop()
    {
        if (!_running && _thread is null) return;
        _running = false;
        _diag?.Invoke("Stop: joining feeder...");
        try { if (_thread?.Join(3000) == false) _diag?.Invoke("Stop: feeder JOIN TIMEOUT"); } catch { }
        _thread = null;
        if (_timerRaised) { try { timeEndPeriod(1); } catch { } _timerRaised = false; }
        _diag?.Invoke("Stop: finalizing writer...");
        _writer.Stop();
        _diag?.Invoke("Stop: done");
    }

    public void Dispose()
    {
        Stop();
        foreach (var t in _inputPool) { try { t?.Dispose(); } catch { } }
        foreach (var t in _nv12Pool) { try { t?.Dispose(); } catch { } }
        try { _writer.Dispose(); } catch { }
        try { _conv.Dispose(); } catch { }
        try { _cap.Dispose(); } catch { }
    }
}
