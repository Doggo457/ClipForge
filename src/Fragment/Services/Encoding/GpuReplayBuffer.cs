using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Fragment.Models;
using Fragment.Services;
using Vortice.Direct3D11;
using Vortice.MediaFoundation;

namespace Fragment.Services.Encoding;

/// <summary>
/// Always-on instant-replay buffer built on the in-process GPU engine. Continuously captures (WGC) →
/// converts BGRA→NV12 on the GPU → encodes H.264 via the hardware MFT, and keeps the last N seconds of
/// ENCODED samples in an in-memory ring (audio is kept as PCM, encoded to AAC only when a clip is saved).
/// <see cref="SaveClipAsync"/> muxes the last clip-length seconds — GOP-aligned, PTS rebased to 0 — into an
/// MP4 (video passed through, audio encoded), while the buffer keeps recording. Near-zero idle CPU.
/// </summary>
public sealed class GpuReplayBuffer : IReplayBuffer, IDisposable
{
    private const int PoolSize = 12;
    private const int AudioRate = 48000;
    private const int AudioChannels = 2;

    private readonly object _ringLock = new();
    private readonly object _lifecycle = new();
    private readonly List<EncodedVideoSample> _videoRing = new();
    private readonly List<AudioPcmChunk> _audioRing = new();

    [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint ms);
    [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint ms);

    private GpuRecordingDevice? _gpu;
    private GpuWgcCapture? _cap;
    private VideoProcessorConverter? _conv;
    private MfH264EncoderMft? _enc;
    private GpuAudioCapture? _audio;
    private IMFMediaType? _videoOutType;
    private ID3D11Texture2D[]? _inputPool, _nv12Pool;

    private Thread? _feeder;
    private volatile bool _running;
    private bool _timerRaised;
    private int _fps, _audioBitrate;
    private long _bufferNs;
    private bool _wantAudio;

    private long _audioSamples, _audioAnchorNs;
    private bool _audioAnchored;

    private long _videoBytes;                                  // running sum of encoded bytes in the ring
    private const long MaxRingBytes = 2L * 1024 * 1024 * 1024; // safety cap so extreme settings can't OOM
    private readonly SemaphoreSlim _saveGate = new(1, 1);      // one clip-save at a time

    public bool IsRunning => _running;
    public event EventHandler? Stopped;
    public Action<string>? Diag;

    /// <summary>The GPU buffer handles MP4 full-screen / single-monitor capture (same as the GPU recorder).</summary>
    public static bool CanHandle(RecordingProfile p) =>
        p.Container == OutputContainer.Mp4 && p.Source is CaptureSource.FullScreen or CaptureSource.Monitor;

    public void Start(RecordingProfile profile, int bufferSeconds)
    {
        if (profile is null) throw new ArgumentNullException(nameof(profile));
        if (bufferSeconds <= 0) bufferSeconds = 120;

        lock (_lifecycle)
        {
            if (_running) throw new InvalidOperationException("Replay buffer is already running.");

            _fps = profile.Fps > 0 ? profile.Fps : 60;
            _bufferNs = bufferSeconds * 10_000_000L;
            _audioBitrate = Math.Max(64_000, profile.AudioBitrateKbps * 1000);
            int videoBps = Math.Max(1_000_000, profile.VideoBitrateKbps * 1000);
            _wantAudio = profile.Audio != AudioMode.None;
            bool wantSystem = profile.Audio is AudioMode.SystemOnly or AudioMode.SystemAndMic;
            bool wantMic = profile.Audio is AudioMode.MicOnly or AudioMode.SystemAndMic;

            GpuRecordingDevice? gpu = null;
            try
            {
                gpu = new GpuRecordingDevice();
                IntPtr hmon = ResolveMonitor(profile);
                var cap = new GpuWgcCapture(gpu, hmon, profile.CaptureCursor);
                if (!cap.WaitForFirstFrame(2500, out int w, out int h))
                {
                    cap.Dispose(); gpu.Dispose();
                    throw new InvalidOperationException("Replay buffer: no frame captured.");
                }
                var conv = new VideoProcessorConverter(gpu, w, h, _fps);
                var enc = new MfH264EncoderMft(gpu, conv.Width, conv.Height, _fps, videoBps, OnEncodedVideo);

                _inputPool = new ID3D11Texture2D[PoolSize];
                _nv12Pool = new ID3D11Texture2D[PoolSize];
                for (int i = 0; i < PoolSize; i++) { _inputPool[i] = conv.CreateInputTexture(); _nv12Pool[i] = conv.CreateNv12Texture(); }

                GpuAudioCapture? audio = null;
                if (wantSystem || wantMic)
                {
                    try { var a = new GpuAudioCapture(wantSystem, wantMic, OnAudioPcm, GpuScreenRecorder.MicProcFor(profile), profile.MicDevice); if (a.Active) audio = a; else a.Dispose(); }
                    catch { audio = null; }
                }
                _wantAudio = audio != null;

                _gpu = gpu; _cap = cap; _conv = conv; _enc = enc; _audio = audio;
                _videoOutType = enc.CloneOutputType(); // own an independent copy; the encoder owns its own
                lock (_ringLock) { _videoRing.Clear(); _audioRing.Clear(); _videoBytes = 0; _audioAnchored = false; _audioSamples = 0; }

                try { timeBeginPeriod(1); _timerRaised = true; } catch { }
                _running = true;
                _audio?.Start();
                _feeder = new Thread(Feeder) { IsBackground = true, Priority = ThreadPriority.AboveNormal, Name = "GpuReplayFeeder" };
                _feeder.Start();
            }
            catch (Exception ex)
            {
                // Stop + join the feeder (it may already be running) BEFORE TearDown disposes the D3D
                // device / encoder it uses — otherwise the live feeder races teardown (use-after-dispose).
                _running = false;
                try { _feeder?.Join(3000); } catch { }
                _feeder = null;
                TearDown();
                throw new InvalidOperationException("Failed to start the GPU replay buffer.", ex);
            }
        }
    }

    private Stopwatch? _clock;

    private void Feeder()
    {
        _clock = Stopwatch.StartNew();
        long freq = Stopwatch.Frequency;
        long frame = 0, frameDur = 10_000_000L / _fps;
        try
        {
            while (_running)
            {
                long deadline = frame * freq / _fps;
                long now = _clock.ElapsedTicks;
                if (deadline > now) { int ms = (int)((deadline - now) * 1000 / freq); if (ms > 0) Thread.Sleep(ms); }

                int slot = (int)(frame % PoolSize);
                if (_cap!.CopyLatestInto(_inputPool![slot]))
                {
                    _conv!.Convert(_inputPool[slot], _nv12Pool![slot]);
                    _gpu!.Context.Flush();
                    if (_enc!.TryConsumeNeedInput())
                        _enc.SubmitFrame(_nv12Pool[slot], _clock.Elapsed.Ticks, frameDur);
                }
                frame++;
            }
        }
        catch (Exception)
        {
            // Device loss / capture fault while still meant to be running -> self-terminate + notify.
            if (_running)
            {
                _running = false;
                ThreadPool.QueueUserWorkItem(_ => { try { Stopped?.Invoke(this, EventArgs.Empty); } catch { } });
            }
        }
    }

    private void OnEncodedVideo(EncodedVideoSample s)
    {
        lock (_ringLock)
        {
            _videoRing.Add(s);
            _videoBytes += s.Data.Length;

            // Evict only when a keyframe arrives (eviction can only advance at GOP boundaries) — keeps the
            // ring starting on a keyframe and bounds per-frame cost. Cut by time, then tighten for the byte cap.
            if (!s.KeyFrame) return;
            long horizon = s.TimeNs - _bufferNs;
            int cut = 0;
            for (int i = 0; i < _videoRing.Count; i++)
                if (_videoRing[i].KeyFrame && _videoRing[i].TimeNs <= horizon) cut = i;

            if (_videoBytes > MaxRingBytes)
            {
                long bytesFromCut = 0;
                for (int i = cut; i < _videoRing.Count; i++) bytesFromCut += _videoRing[i].Data.Length;
                for (int i = cut + 1; i < _videoRing.Count && bytesFromCut > MaxRingBytes; i++)
                    if (_videoRing[i].KeyFrame) { for (int j = cut; j < i; j++) bytesFromCut -= _videoRing[j].Data.Length; cut = i; }
            }

            if (cut > 0)
            {
                for (int i = 0; i < cut; i++) _videoBytes -= _videoRing[i].Data.Length;
                _videoRing.RemoveRange(0, cut);
            }
        }
    }

    private void OnAudioPcm(byte[] pcm, int count)
    {
        var clock = _clock;
        if (clock is null || count <= 0) return;
        int perChannel = count / (2 * AudioChannels);
        if (perChannel <= 0) return;

        long elapsed = clock.Elapsed.Ticks;
        var copy = new byte[count];
        Buffer.BlockCopy(pcm, 0, copy, 0, count);

        // All anchor/sample-count state + the ring are mutated under one lock so audio TimeNs stays
        // monotonic and a session reset (Start) can't interleave with a late callback.
        lock (_ringLock)
        {
            if (!_audioAnchored)
            {
                long bufDur = perChannel * 10_000_000L / AudioRate;
                _audioAnchorNs = Math.Max(0, elapsed - bufDur);
                _audioSamples = 0;
                _audioAnchored = true;
            }
            long ts = _audioAnchorNs + _audioSamples * 10_000_000L / AudioRate;
            long dur = perChannel * 10_000_000L / AudioRate;
            _audioRing.Add(new AudioPcmChunk { Pcm = copy, Count = count, TimeNs = ts, DurNs = dur });
            _audioSamples += perChannel;

            // Self-bound by the audio clock so a video stall can't grow the audio ring without limit.
            long aHorizon = ts - _bufferNs - 10_000_000L;
            int an = 0;
            while (an < _audioRing.Count && _audioRing[an].TimeNs < aHorizon) an++;
            if (an > 0) _audioRing.RemoveRange(0, an);
        }
    }

    public Task<string?> SaveClipAsync(int seconds, string outputPath)
    {
        if (string.IsNullOrWhiteSpace(outputPath)) throw new ArgumentException("Output path is required.", nameof(outputPath));
        if (seconds <= 0) seconds = 30;

        // One save at a time. Also mutually exclusive with Stop()/teardown so the muxer can't use a
        // disposed output type or ring. If a save is already running, ignore this request.
        if (!_saveGate.Wait(0)) return Task.FromResult<string?>(null);

        bool releaseNow = true;
        try
        {
            EncodedVideoSample[] video;
            AudioPcmChunk[] audio;
            long newestNs, startVideoNs;
            IMFMediaType? vType = _videoOutType;

            lock (_ringLock)
            {
                if (_videoRing.Count == 0 || vType is null) return Task.FromResult<string?>(null);
                newestNs = _videoRing[^1].TimeNs;
                long horizon = newestNs - seconds * 10_000_000L;

                int startIdx = -1;
                for (int i = 0; i < _videoRing.Count; i++)
                    if (_videoRing[i].KeyFrame && _videoRing[i].TimeNs <= horizon) startIdx = i;
                if (startIdx < 0) // window not full yet: fall back to the first keyframe
                    for (int i = 0; i < _videoRing.Count; i++) if (_videoRing[i].KeyFrame) { startIdx = i; break; }
                if (startIdx < 0) return Task.FromResult<string?>(null); // no keyframe yet

                startVideoNs = _videoRing[startIdx].TimeNs;
                video = _videoRing.GetRange(startIdx, _videoRing.Count - startIdx).ToArray();

                var aud = new List<AudioPcmChunk>();
                foreach (var c in _audioRing)
                    if (c.TimeNs + c.DurNs > startVideoNs && c.TimeNs <= newestNs) aud.Add(c);
                audio = aud.ToArray();
            }

            releaseNow = false; // the Task owns the gate now
            try
            {
                return Task.Run(() =>
                {
                    try { MuxClip(outputPath, vType!, video, audio, startVideoNs); return (string?)outputPath; }
                    finally { _saveGate.Release(); }
                });
            }
            catch { _saveGate.Release(); throw; } // scheduling failure: don't leak the gate
        }
        finally { if (releaseNow) _saveGate.Release(); }
    }

    private void MuxClip(string outputPath, IMFMediaType videoType, EncodedVideoSample[] video, AudioPcmChunk[] audio, long originNs)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);

        Diag?.Invoke($"  mux: {video.Length} video, {audio.Length} audio samples");
        IMFSinkWriter? w = null;
        try
        {
            w = MediaFactory.MFCreateSinkWriterFromURL(outputPath, null, null);

            int vIdx = w.AddStream(videoType);          // output = encoder's H.264 type (carries SPS/PPS)
            w.SetInputMediaType(vIdx, videoType, null); // input == output -> passthrough, no re-encode
            Diag?.Invoke("  mux: video stream added (passthrough)");

            int aIdx = -1;
            if (_wantAudio && audio.Length > 0)
            {
                using var aOut = MediaFactory.MFCreateMediaType();
                aOut.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
                aOut.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Aac);
                aOut.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, (uint)AudioRate);
                aOut.Set(MediaTypeAttributeKeys.AudioNumChannels, (uint)AudioChannels);
                aOut.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 16u);
                aOut.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, (uint)MfH264SinkWriter.SnapAacBytesPerSec(_audioBitrate));
                aOut.Set(MediaTypeAttributeKeys.AacAudioProfileLevelIndication, 0x29u);
                aIdx = w.AddStream(aOut);

                using var aIn = MediaFactory.MFCreateMediaType();
                aIn.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
                aIn.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Pcm);
                aIn.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, (uint)AudioRate);
                aIn.Set(MediaTypeAttributeKeys.AudioNumChannels, (uint)AudioChannels);
                aIn.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 16u);
                aIn.Set(MediaTypeAttributeKeys.AudioBlockAlignment, (uint)(AudioChannels * 2));
                aIn.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, (uint)(AudioRate * AudioChannels * 2));
                w.SetInputMediaType(aIdx, aIn, null);
                Diag?.Invoke("  mux: audio stream added (AAC encode)");
            }

            w.BeginWriting();
            Diag?.Invoke("  mux: writing (interleaved by timestamp)...");

            // Interleave video + audio in timestamp order so neither stream races ahead and back-pressures
            // the muxer (writing all of one stream first deadlocks WriteSample on stream sync).
            int vi = 0, ai = 0;
            while (vi < video.Length || (aIdx >= 0 && ai < audio.Length))
            {
                bool writeVideo;
                if (vi >= video.Length) writeVideo = false;
                else if (aIdx < 0 || ai >= audio.Length) writeVideo = true;
                else writeVideo = video[vi].TimeNs <= audio[ai].TimeNs;

                if (writeVideo)
                {
                    var s = video[vi++];
                    WriteBytes(w, vIdx, s.Data, s.Data.Length, Math.Max(0, s.TimeNs - originNs), s.DurNs, s.KeyFrame);
                }
                else
                {
                    var c = audio[ai++];
                    WriteBytes(w, aIdx, c.Pcm, c.Count, Math.Max(0, c.TimeNs - originNs), c.DurNs, false);
                }
            }

            Diag?.Invoke("  mux: finalizing...");
            w.Finalize();
            Diag?.Invoke("  mux: done");
        }
        finally { try { w?.Dispose(); } catch { } }
    }

    private static void WriteBytes(IMFSinkWriter w, int stream, byte[] data, int count, long timeNs, long durNs, bool key)
    {
        using var buf = MediaFactory.MFCreateMemoryBuffer(count); // disposed even if WriteSample throws
        buf.Lock(out IntPtr p, out _, out _);
        Marshal.Copy(data, 0, p, count);
        buf.Unlock();
        buf.CurrentLength = count;

        using var sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(buf);
        sample.SampleTime = timeNs;
        sample.SampleDuration = durNs;
        if (key) sample.Set(SampleAttributeKeys.CleanPoint, 1u);
        w.WriteSample(stream, sample);
    }

    public void Stop()
    {
        lock (_lifecycle)
        {
            if (!_running && _feeder is null && _gpu is null) return;
            _running = false;
            try { _feeder?.Join(3000); } catch { }
            _feeder = null;
            // Wait out any in-flight save (it holds _saveGate) before releasing the encoder/device.
            _saveGate.Wait();
            try { TearDown(); } finally { _saveGate.Release(); }
        }
    }

    // Releases all session resources. Caller holds _lifecycle and _saveGate.
    private void TearDown()
    {
        if (_timerRaised) { try { timeEndPeriod(1); } catch { } _timerRaised = false; }
        // Stop+join the audio pump BEFORE clearing the ring so a late callback can't touch freed state.
        try { _audio?.Dispose(); } catch { }
        try { _enc?.Dispose(); } catch { } // Dispose does the ordered drain/flush internally
        try { _videoOutType?.Dispose(); } catch { }
        if (_inputPool != null) foreach (var t in _inputPool) { try { t?.Dispose(); } catch { } }
        if (_nv12Pool != null) foreach (var t in _nv12Pool) { try { t?.Dispose(); } catch { } }
        try { _conv?.Dispose(); } catch { }
        try { _cap?.Dispose(); } catch { }
        try { _gpu?.Dispose(); } catch { }
        _audio = null; _enc = null; _conv = null; _cap = null; _gpu = null;
        _inputPool = null; _nv12Pool = null; _videoOutType = null; _clock = null;
        lock (_ringLock) { _videoRing.Clear(); _audioRing.Clear(); }
        // Finalize the prior WGC item off the UI/STA thread so the next session's border retires (border-restart fix).
        ThreadPool.QueueUserWorkItem(_ => { try { GC.Collect(); GC.WaitForPendingFinalizers(); } catch { } });
    }

    private static IntPtr ResolveMonitor(RecordingProfile p)
    {
        if (p.Source == CaptureSource.Monitor)
        {
            var mon = MonitorEnumerator.GetByIndex(p.MonitorIndex);
            return mon is not null ? WgcCapture.MonitorFromPoint(mon.X + 1, mon.Y + 1) : WgcCapture.MonitorFromPoint(0, 0);
        }
        return WgcCapture.MonitorFromPoint(0, 0);
    }

    public void Dispose() => Stop();
}
