using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
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

    public GpuVideoRecorder(GpuRecordingDevice gpu, IntPtr hmon, string path, int fps, int bitrate,
        bool captureCursor, bool systemAudio = false, int audioBitrateBps = 160_000, Action<string>? diag = null)
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

        if (systemAudio)
        {
            try
            {
                _audio = new GpuAudioCapture(OnAudioPcm);
                _audioRate = _audio.SampleRate;
                _audioChannels = _audio.Channels;
            }
            catch (Exception ex) { _diag?.Invoke("audio disabled: " + ex.Message); _audio = null; }
        }

        _writer = new MfH264SinkWriter(gpu, path, _conv.Width, _conv.Height, _fps, bitrate,
            _audio != null ? _audioRate : 0, _audioChannels > 0 ? _audioChannels : 2, audioBitrateBps);

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
            var input = _inputPool[slot];
            var nv12 = _nv12Pool[slot];

            bool trace = frame < 3 || frame % 600 == 0;
            try
            {
                // Pull + convert + encode, all on this one thread. If capture stalled, CopyLatestInto
                // re-copies the previous frame, so we still emit on cadence (frame-repeat) → smooth motion.
                if (_cap.CopyLatestInto(input))
                {
                    _conv.Convert(input, nv12);
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
