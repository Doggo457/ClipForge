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

    // The ring keeps only lightweight metadata; the encoded bytes live in a fixed, pre-allocated circular
    // arena — ONE stable allocation with zero per-frame churn. Allocating + retaining a byte[] per frame
    // (the previous design) promoted thousands of variable-sized arrays into gen2/LOH and fragmented
    // committed memory, so the working set crept up over time even though live managed bytes stayed flat.
    private readonly List<VideoEntry> _videoRing = new();
    private readonly List<AudioEntry> _audioRing = new();
    private ByteArena? _videoArena, _audioArena;
    private long _videoWritePos, _audioWritePos;

    private readonly struct VideoEntry
    {
        public VideoEntry(long off, int len, long t, long d, bool key) { Offset = off; Length = len; TimeNs = t; DurNs = d; KeyFrame = key; }
        public readonly long Offset; public readonly int Length; public readonly long TimeNs, DurNs; public readonly bool KeyFrame;
    }
    private readonly struct AudioEntry
    {
        public AudioEntry(long off, int count, long t, long d) { Offset = off; Count = count; TimeNs = t; DurNs = d; }
        public readonly long Offset; public readonly int Count; public readonly long TimeNs, DurNs;
    }

    // Fixed circular byte buffer in NATIVE memory (not the GC heap). A multi-hundred-MB managed byte[] would
    // be a giant LOH object that fragments the LOH and holds committed memory the background GC won't return
    // without an (expensive) LOH compaction. Native memory keeps the managed heap tiny, so RSS stays flat with
    // no forced GC. All access is under _ringLock (writers = feeder/audio threads; reader = save). Freed in Dispose.
    private sealed unsafe class ByteArena : IDisposable
    {
        private byte* _buf;
        private readonly long _cap;
        public long Capacity => _cap;
        public ByteArena(long capacity)
        {
            _cap = capacity;
            _buf = (byte*)System.Runtime.InteropServices.NativeMemory.Alloc((nuint)capacity);
        }
        public void Write(byte[] src, int srcOff, long pos, int len)
        {
            if (_buf == null) return; // defensive: never touch freed memory (callers also hold _ringLock)
            int p = (int)(pos % _cap);
            int first = Math.Min(len, (int)(_cap - p));
            System.Runtime.InteropServices.Marshal.Copy(src, srcOff, (IntPtr)(_buf + p), first);
            if (first < len) System.Runtime.InteropServices.Marshal.Copy(src, srcOff + first, (IntPtr)_buf, len - first);
        }
        public byte[] Read(long pos, int len)
        {
            var d = new byte[len];
            if (_buf == null) return d; // defensive: never touch freed memory (callers also hold _ringLock)
            int p = (int)(pos % _cap);
            int first = Math.Min(len, (int)(_cap - p));
            System.Runtime.InteropServices.Marshal.Copy((IntPtr)(_buf + p), d, 0, first);
            if (first < len) System.Runtime.InteropServices.Marshal.Copy((IntPtr)_buf, d, first, len - first);
            return d;
        }
        public void Dispose()
        {
            if (_buf != null) { System.Runtime.InteropServices.NativeMemory.Free(_buf); _buf = null; }
        }
    }

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

    private long _videoBytes;                                  // live encoded bytes in the ring (== arena span)
    private long _audioBytes;                                  // live PCM bytes in the audio ring
    private const long MaxRingBytes = 0x7FFFFFC7L;            // == Array.MaxLength: the arena is ONE byte[], so this is its hard ceiling
    private readonly SemaphoreSlim _saveGate = new(1, 1);      // one clip-save at a time

    public bool IsRunning => _running;
    public event EventHandler? Stopped;
    public Action<string>? Diag;

    // Leak-tracing diagnostics (read-only snapshots; used by the headless self-test).
    internal int DiagVideoRingCount { get { lock (_ringLock) return _videoRing.Count; } }
    internal long DiagVideoRingBytes { get { lock (_ringLock) return _videoBytes; } }
    internal int DiagAudioRingCount { get { lock (_ringLock) return _audioRing.Count; } }
    internal long DiagEncSamples => _enc?.SamplesOut ?? 0;
    internal long DiagEncKeyframes => _enc?.KeyFramesOut ?? 0;
    internal long DiagArrived => _cap?.ArrivedCount ?? 0;

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

                // Size the fixed arenas from the window + bitrate (+ headroom for VBR overshoot), capped.
                long vCap = Math.Clamp((long)(bufferSeconds * (videoBps / 8.0) * 1.3) + 8L * 1024 * 1024, 16L * 1024 * 1024, MaxRingBytes);
                long aCap = Math.Clamp((long)bufferSeconds * AudioRate * AudioChannels * 2 * 3 / 2 + 1024 * 1024, 1L * 1024 * 1024, MaxRingBytes);
                lock (_ringLock)
                {
                    _videoRing.Clear(); _audioRing.Clear();
                    _videoArena = new ByteArena(vCap); _audioArena = new ByteArena(aCap);
                    _videoWritePos = _audioWritePos = 0; _videoBytes = _audioBytes = 0;
                    _audioAnchored = false; _audioSamples = 0;
                }

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
            var arena = _videoArena;
            if (arena is null) return;          // stopped / not started
            int len = s.Length;
            if (len <= 0 || len > arena.Capacity) return; // skip a frame larger than the whole arena (pathological)

            // Make room: drop the oldest frames until the new one fits in the fixed arena (VBR-spike guard).
            int drop = 0; long live = _videoBytes;
            while (drop < _videoRing.Count && live + len > arena.Capacity) { live -= _videoRing[drop].Length; drop++; }
            if (drop > 0)
            {
                // Prefer to leave the ring starting on a keyframe (a saved clip must start on one). Advance past
                // any leading P-frames the byte-drop exposed — but only if a keyframe still remains (never drop
                // everything; the next keyframe will re-establish the start).
                int kfDrop = drop; long kfLive = live;
                while (kfDrop < _videoRing.Count && !_videoRing[kfDrop].KeyFrame) { kfLive -= _videoRing[kfDrop].Length; kfDrop++; }
                if (kfDrop < _videoRing.Count) { drop = kfDrop; live = kfLive; }
                _videoBytes = live; _videoRing.RemoveRange(0, drop);
            }

            arena.Write(s.Data, 0, _videoWritePos, len);
            _videoRing.Add(new VideoEntry(_videoWritePos, len, s.TimeNs, s.DurNs, s.KeyFrame));
            _videoWritePos += len;
            _videoBytes += len;

            // Time eviction only at keyframes (a clip must start on one) — keeps ~_bufferNs of footage.
            if (!s.KeyFrame) return;
            long horizon = s.TimeNs - _bufferNs;
            int cut = 0;
            for (int i = 0; i < _videoRing.Count; i++)
                if (_videoRing[i].KeyFrame && _videoRing[i].TimeNs <= horizon) cut = i;
            if (cut > 0)
            {
                for (int i = 0; i < cut; i++) _videoBytes -= _videoRing[i].Length;
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

        // All anchor/sample-count state + the ring + the arena are mutated under one lock so audio TimeNs
        // stays monotonic and a session reset (Start) can't interleave with a late callback.
        lock (_ringLock)
        {
            var arena = _audioArena;
            if (arena is null || count > arena.Capacity) return;
            if (!_audioAnchored)
            {
                long bufDur = perChannel * 10_000_000L / AudioRate;
                _audioAnchorNs = Math.Max(0, elapsed - bufDur);
                _audioSamples = 0;
                _audioAnchored = true;
            }
            long ts = _audioAnchorNs + _audioSamples * 10_000_000L / AudioRate;
            long dur = perChannel * 10_000_000L / AudioRate;

            // Make room in the fixed arena (defensive; the time eviction below normally keeps it under cap).
            int drop = 0; long live = _audioBytes;
            while (drop < _audioRing.Count && live + count > arena.Capacity) { live -= _audioRing[drop].Count; drop++; }
            if (drop > 0) { _audioBytes = live; _audioRing.RemoveRange(0, drop); }

            arena.Write(pcm, 0, _audioWritePos, count);
            _audioRing.Add(new AudioEntry(_audioWritePos, count, ts, dur));
            _audioWritePos += count;
            _audioBytes += count;
            _audioSamples += perChannel;

            // Self-bound by the audio clock so a video stall can't grow the audio ring without limit.
            long aHorizon = ts - _bufferNs - 10_000_000L;
            int an = 0;
            while (an < _audioRing.Count && _audioRing[an].TimeNs < aHorizon) an++;
            if (an > 0) { for (int i = 0; i < an; i++) _audioBytes -= _audioRing[i].Count; _audioRing.RemoveRange(0, an); }
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
                var vArena = _videoArena; var aArena = _audioArena;
                if (vArena is null) return Task.FromResult<string?>(null);

                // Copy the clip OUT of the circular arena into owned arrays, so the background mux is
                // independent of the live ring (which keeps overwriting the arena as new frames arrive).
                video = new EncodedVideoSample[_videoRing.Count - startIdx];
                for (int i = startIdx; i < _videoRing.Count; i++)
                {
                    var e = _videoRing[i];
                    video[i - startIdx] = new EncodedVideoSample { Data = vArena.Read(e.Offset, e.Length), Length = e.Length, TimeNs = e.TimeNs, DurNs = e.DurNs, KeyFrame = e.KeyFrame };
                }

                var aud = new List<AudioPcmChunk>();
                if (aArena != null)
                    foreach (var c in _audioRing)
                        if (c.TimeNs + c.DurNs > startVideoNs && c.TimeNs <= newestNs)
                            aud.Add(new AudioPcmChunk { Pcm = aArena.Read(c.Offset, c.Count), Count = c.Count, TimeNs = c.TimeNs, DurNs = c.DurNs });
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
                    WriteBytes(w, vIdx, s.Data, s.Length, Math.Max(0, s.TimeNs - originNs), s.DurNs, s.KeyFrame);
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
        lock (_ringLock)
        {
            _videoRing.Clear(); _audioRing.Clear();
            try { _videoArena?.Dispose(); } catch { } try { _audioArena?.Dispose(); } catch { } // free native memory
            _videoArena = null; _audioArena = null;
            _videoBytes = _audioBytes = 0; _videoWritePos = _audioWritePos = 0;
        }
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
