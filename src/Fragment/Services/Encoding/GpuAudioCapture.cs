using System;
using System.Diagnostics;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Fragment.Services.Encoding;

/// <summary>
/// Captures system (WASAPI loopback) and/or microphone audio, mixes them to a fixed 48 kHz stereo
/// stream, and delivers interleaved 16-bit PCM (the format the MF AAC encoder wants) to a callback.
///
/// Each source feeds a thread-safe buffer (resampled / up-mixed to 48 kHz stereo). A steady pull thread,
/// clocked by a Stopwatch, reads exactly <c>elapsed × 48000</c> frames so the produced audio tracks real
/// time (no drift vs the video clock). Underruns read as silence; overruns are discarded — so a glitch
/// in one source never desyncs the stream. A silent keep-alive render stream keeps loopback delivering
/// during system silence.
/// </summary>
public sealed class GpuAudioCapture : IDisposable
{
    private const int Rate = 48000;
    private const int Ch = 2;

    private readonly Action<byte[], int> _onPcm16;
    private readonly bool _wantSystem, _wantMic;

    private WasapiLoopbackCapture? _loopback;
    private WasapiCapture? _mic;
    private IWavePlayer? _silence;
    private BufferedWaveProvider? _sysBuf, _micBuf;
    private MixingSampleProvider? _mixer;

    private Thread? _pump;
    private volatile bool _running;
    private float[] _mixF = Array.Empty<float>();
    private byte[] _pcm = Array.Empty<byte>();

    public int SampleRate => Rate;
    public int Channels => Ch;

    /// <summary>True if at least one source initialised — the recorder only adds an audio stream if so.</summary>
    public bool Active => _loopback != null || _mic != null;

    public GpuAudioCapture(bool captureSystem, bool captureMic, Action<byte[], int> onPcm16,
        MicProcessing micProc = default, string? micDeviceName = null)
    {
        _wantSystem = captureSystem;
        _wantMic = captureMic;
        _onPcm16 = onPcm16;

        _mixer = new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(Rate, Ch)) { ReadFully = true };

        if (_wantSystem)
        {
            try
            {
                _loopback = new WasapiLoopbackCapture();
                _sysBuf = MakeBuffer(_loopback.WaveFormat);
                _loopback.DataAvailable += (_, e) => _sysBuf!.AddSamples(e.Buffer, 0, e.BytesRecorded);
                _mixer.AddMixerInput(Adapt(_sysBuf.ToSampleProvider(), _loopback.WaveFormat));
            }
            catch { _loopback = null; _sysBuf = null; }
        }

        if (_wantMic)
        {
            try
            {
                var dev = ResolveMic(micDeviceName);
                try { _mic = dev != null ? new WasapiCapture(dev) : new WasapiCapture(); }
                catch { _mic = new WasapiCapture(); } // selected device unavailable -> default mic
                _micBuf = MakeBuffer(_mic.WaveFormat);
                _mic.DataAvailable += (_, e) => _micBuf!.AddSamples(e.Buffer, 0, e.BytesRecorded);

                // Mic cleanup chain: mono-ize -> spectral noise suppression -> noise gate -> 48k stereo.
                ISampleProvider micSp = _micBuf.ToSampleProvider();
                if (micSp.WaveFormat.Channels == 2) micSp = new StereoToMonoSampleProvider(micSp);
                if (micProc.SuppressEnabled && micSp.WaveFormat.Channels == 1)
                    micSp = new SpectralNoiseSuppressor(micSp, micProc.SuppressStrength);
                if (micProc.GateEnabled)
                    micSp = new NoiseGateSampleProvider(micSp, micProc.GateThresholdDb);
                _mixer.AddMixerInput(Adapt(micSp, micSp.WaveFormat));
            }
            catch { _mic = null; _micBuf = null; }
        }
    }

    // Best-effort map of a (DirectShow) mic name to a WASAPI capture device by friendly name.
    private static MMDevice? ResolveMic(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        try
        {
            var en = new MMDeviceEnumerator();
            MMDevice? best = null;
            foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                var fn = d.FriendlyName;
                if (string.Equals(fn, name, StringComparison.OrdinalIgnoreCase)) return d;
                if (best == null &&
                    (fn.Contains(name, StringComparison.OrdinalIgnoreCase) ||
                     name.Contains(fn, StringComparison.OrdinalIgnoreCase)))
                    best = d;
            }
            return best;
        }
        catch { return null; }
    }

    private static BufferedWaveProvider MakeBuffer(WaveFormat fmt) => new(fmt)
    {
        DiscardOnBufferOverflow = true,        // drop if a source races ahead — never block / desync
        BufferDuration = TimeSpan.FromSeconds(2),
        ReadFully = true,                      // return silence (zeros) when the source is momentarily empty
    };

    // Bring any source to the mixer's 48 kHz stereo float format.
    private static ISampleProvider Adapt(ISampleProvider src, WaveFormat fmt)
    {
        if (src.WaveFormat.Channels == 1) src = new MonoToStereoSampleProvider(src);
        if (src.WaveFormat.SampleRate != Rate) src = new WdlResamplingSampleProvider(src, Rate);
        return src;
    }

    public void Start()
    {
        // Inaudible silence on the render endpoint so loopback always produces frames.
        if (_wantSystem && _loopback != null)
        {
            try
            {
                var s = new WasapiOut(AudioClientShareMode.Shared, 100);
                s.Init(new SilenceProvider(_loopback.WaveFormat));
                s.Play();
                _silence = s;
            }
            catch { /* best effort */ }
        }

        try { _loopback?.StartRecording(); } catch { }
        try { _mic?.StartRecording(); } catch { }

        _running = true;
        _pump = new Thread(Pump) { IsBackground = true, Name = "GpuAudioPump", Priority = ThreadPriority.AboveNormal };
        _pump.Start();
    }

    private void Pump()
    {
        var sw = Stopwatch.StartNew();
        long produced = 0; // frames delivered so far
        while (_running)
        {
            Thread.Sleep(8);
            var mixer = _mixer;            // snapshot: Dispose may null it concurrently
            if (mixer is null || !_running) break;
            long target = sw.Elapsed.Ticks * Rate / TimeSpan.TicksPerSecond; // frames that should exist by now
            int need = (int)(target - produced);
            if (need <= 0) continue;

            int floats = need * Ch;
            if (_mixF.Length < floats) _mixF = new float[floats];
            int got = mixer.Read(_mixF, 0, floats); // ReadFully → always returns 'floats'
            if (got <= 0 || !_running) continue;

            int outBytes = got * 2;
            if (_pcm.Length < outBytes) _pcm = new byte[outBytes];
            var outS = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(_pcm.AsSpan(0, outBytes));
            for (int i = 0; i < got; i++)
            {
                float v = _mixF[i];
                v = v < -1f ? -1f : (v > 1f ? 1f : v);
                outS[i] = (short)(v * 32767f);
            }
            _onPcm16(_pcm, outBytes);
            produced += got / Ch;
        }
    }

    public void Dispose()
    {
        _running = false;
        // Join the pump BEFORE disposing the mixer/devices so it can't touch freed objects (it exits
        // within one ~8 ms cycle once _running is false).
        try { _pump?.Join(2000); } catch { }
        _pump = null;
        try { _loopback?.StopRecording(); } catch { }
        try { _mic?.StopRecording(); } catch { }
        try { _loopback?.Dispose(); } catch { }
        try { _mic?.Dispose(); } catch { }
        try { _silence?.Stop(); } catch { }
        try { _silence?.Dispose(); } catch { }
        _loopback = null; _mic = null; _silence = null;
        _sysBuf = null; _micBuf = null; _mixer = null;
    }
}
