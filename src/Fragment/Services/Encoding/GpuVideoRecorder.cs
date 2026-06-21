using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Fragment.Models;
using Vortice.Direct3D11;

namespace Fragment.Services.Encoding;

/// <summary>
/// End-to-end GPU recorder: WGC capture (frames kept on the GPU) → fixed-function BGRA→NV12 conversion
/// → in-process hardware H.264 encode → MP4, with optional system audio (WASAPI loopback → AAC) muxed
/// in. No CPU touches the video pixels.
///
/// A dedicated even-cadence "FrameFeeder" thread drives the video pipeline: each tick it snapshots the
/// latest captured frame (repeating the previous one if capture stalled, which keeps motion smooth) and
/// emits one frame, timestamped on a shared monotonic clock. Audio buffers are timestamped on the SAME
/// clock so the muxer interleaves them in A/V sync. A small ring of input/NV12 textures lets the async
/// encoder drain without the feeder overwriting a frame still in flight.
/// </summary>
public sealed class GpuVideoRecorder : IDisposable
{
    [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint ms);
    [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint ms);

    private const int PoolSize = 10; // ~166 ms of headroom at 60 fps for the async encoder to drain

    private readonly GpuRecordingDevice _gpu;
    private readonly GpuWgcCapture _cap;
    private readonly CaptureTarget _target;          // monitor vs window (+ follow-active); drives the feeder path
    private readonly VideoProcessorConverter _conv;
    private readonly MfH264SinkWriter _writer;
    private readonly int _fps;
    private readonly ID3D11Texture2D[] _inputPool = new ID3D11Texture2D[PoolSize];
    private readonly ID3D11Texture2D[] _nv12Pool = new ID3D11Texture2D[PoolSize];

    private GpuAudioCapture? _audio;
    private readonly int _audioRate, _audioChannels;
    private long _audioSamples;       // per-channel samples written so far
    private long _audioAnchor100ns;   // real-clock time of the first audio sample
    private bool _audioAnchored;

    private Stopwatch? _clock;         // shared A/V timeline (started at Start)
    private Thread? _thread;
    private volatile bool _running;
    private bool _timerRaised;
    private readonly Action<string>? _diag;

    public int Width => _conv.Width;
    public int Height => _conv.Height;
    public long FramesEmitted { get; private set; }
    public long CopyFalseCount { get; private set; }
    public long AudioBuffers { get; private set; }
    public string? LastError { get; private set; }
    public long ArrivedCount => _cap.ArrivedCount;
    public bool HasAudio => _audio != null;

    public GpuVideoRecorder(GpuRecordingDevice gpu, CaptureTarget target, string path, int fps, int bitrate,
        AudioMode audio = AudioMode.None, int audioBitrateBps = 160_000,
        MicProcessing micProc = default, string? micDevice = null, Action<string>? diag = null)
    {
        _diag = diag;
        _gpu = gpu;
        _fps = fps > 0 ? fps : 60;
        _target = target;
        _cap = new GpuWgcCapture(gpu, target.Handle, target.CaptureCursor, target.IsWindow, target.FollowActive);
        if (!_cap.WaitForFirstFrame(5000, out int w, out int h))
        {
            _cap.Dispose();
            throw new InvalidOperationException("GPU recorder: no frame captured within 5s.");
        }

        // Window/active capture scales into a FIXED canvas (so the encoder size never changes); monitor capture
        // uses the source size directly. A fixed CanvasWidth (active-window mode) overrides the first-frame size.
        int canvasW = target.CanvasWidth > 0 ? target.CanvasWidth : w;
        int canvasH = target.CanvasHeight > 0 ? target.CanvasHeight : h;
        _conv = new VideoProcessorConverter(gpu, canvasW, canvasH, _fps);

        bool wantSystem = audio is AudioMode.SystemOnly or AudioMode.SystemAndMic;
        bool wantMic = audio is AudioMode.MicOnly or AudioMode.SystemAndMic;
        if (wantSystem || wantMic)
        {
            try
            {
                var a = new GpuAudioCapture(wantSystem, wantMic, OnAudioPcm, micProc, micDevice);
                if (a.Active) { _audio = a; _audioRate = a.SampleRate; _audioChannels = a.Channels; }
                else { a.Dispose(); }
            }
            catch (Exception ex) { _diag?.Invoke("audio disabled: " + ex.Message); _audio = null; }
        }

        _writer = new MfH264SinkWriter(gpu, path, _conv.Width, _conv.Height, _fps, bitrate,
            _audio != null ? _audioRate : 0, _audioChannels > 0 ? _audioChannels : 2, audioBitrateBps);

        for (int i = 0; i < PoolSize; i++)
        {
            if (!target.IsWindow) _inputPool[i] = _conv.CreateInputTexture(); // window mode scales straight from the capture
            _nv12Pool[i] = _conv.CreateNv12Texture();
        }
    }

    public void Start()
    {
        if (_running) return;
        _running = true;
        try { timeBeginPeriod(1); _timerRaised = true; } catch { }

        _clock = Stopwatch.StartNew();
        try { _audio?.Start(); } catch (Exception ex) { _diag?.Invoke("audio start failed: " + ex.Message); }

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
        long freq = Stopwatch.Frequency;
        long frame = 0;
        long frameDur = 10_000_000L / _fps;

        while (_running)
        {
            // Even-cadence deadline for this frame index. Sleep (1 ms timer) without spinning — keeps CPU
            // near zero; the actual emit time is read from the shared clock so A/V stay aligned.
            long deadline = frame * freq / _fps;
            long now = _clock!.ElapsedTicks;
            if (deadline > now)
            {
                int ms = (int)((deadline - now) * 1000 / freq);
                if (ms > 0) Thread.Sleep(ms);
            }

            int slot = (int)(frame % PoolSize);
            var nv12 = _nv12Pool[slot];

            bool trace = frame < 3 || frame % 600 == 0;
            try
            {
                // Pull + convert + encode, all on this one thread. If capture stalled, the previous frame is
                // re-used so we still emit on cadence (frame-repeat) → smooth motion.
                bool got;
                if (_target.IsWindow)
                {
                    // Window / active-window: scale the (any-size, possibly switching) source into the fixed canvas.
                    _cap.PullLatest();
                    var src = _cap.LatestTexture;
                    if (src is not null) { _conv.ConvertScaled(src, _cap.LatestWidth, _cap.LatestHeight, nv12); got = true; }
                    else got = false;
                }
                else
                {
                    var input = _inputPool[slot];
                    if (_cap.CopyLatestInto(input)) { _conv.Convert(input, nv12); got = true; }
                    else got = false;
                }

                if (got)
                {
                    _gpu.Context.Flush(); // submit the Blt so the encoder sees finished NV12
                    long ts = _clock!.Elapsed.Ticks; // 100-ns real time at emit (shared with audio)
                    _writer.WriteFrame(nv12, ts, frameDur);
                    FramesEmitted++;
                    if (trace) _diag?.Invoke($"  f{frame}: emitted={FramesEmitted} audioBufs={AudioBuffers}");
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

    // Called on NAudio's capture thread with interleaved 16-bit PCM. Timestamp on the shared clock,
    // anchored so the first sample maps to its real capture time, then advanced by exact sample count.
    private void OnAudioPcm(byte[] pcm, int count)
    {
        var clock = _clock;
        if (clock is null || count <= 0 || _audioChannels <= 0) return;

        int perChannel = count / (2 * _audioChannels);
        if (perChannel <= 0) return;

        if (!_audioAnchored)
        {
            long bufDur = perChannel * 10_000_000L / _audioRate;
            _audioAnchor100ns = Math.Max(0, clock.Elapsed.Ticks - bufDur);
            _audioSamples = 0;
            _audioAnchored = true;
        }

        long ts = _audioAnchor100ns + _audioSamples * 10_000_000L / _audioRate;
        long dur = perChannel * 10_000_000L / _audioRate;
        try { _writer.WriteAudio(pcm, count, ts, dur); AudioBuffers++; }
        catch (Exception ex) { LastError ??= "audio: " + ex.Message; }
        _audioSamples += perChannel;
    }

    public void Stop()
    {
        if (!_running && _thread is null) return;
        _running = false;

        _diag?.Invoke("Stop: joining feeder...");
        try { if (_thread?.Join(3000) == false) _diag?.Invoke("Stop: feeder JOIN TIMEOUT"); } catch { }
        _thread = null;
        if (_timerRaised) { try { timeEndPeriod(1); } catch { } _timerRaised = false; }

        try { _audio?.Dispose(); } catch { } // stop audio capture before finalizing the file
        _audio = null;

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
