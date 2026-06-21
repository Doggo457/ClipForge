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
        // Read len bytes at pos into dest[0..len) (dest must be >= len). Lets the muxer reuse one buffer
        // instead of allocating a managed array per frame (which, for a multi-minute clip, spiked the heap).
        public void ReadInto(long pos, int len, byte[] dest)
        {
            if (_buf == null) return;
            int p = (int)(pos % _cap);
            int first = Math.Min(len, (int)(_cap - p));
            System.Runtime.InteropServices.Marshal.Copy((IntPtr)(_buf + p), dest, 0, first);
            if (first < len) System.Runtime.InteropServices.Marshal.Copy((IntPtr)_buf, dest, first, len - first);
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
    // The Video Processor reads the capture's "latest" BGRA texture directly (no intermediate input copy),
    // so we only keep the NV12 output pool. The pool lets the encoder read one slot asynchronously while the
    // feeder writes the next — encoded output is not necessarily consumed by the time we produce the next frame.
    private ID3D11Texture2D[]? _nv12Pool;

    private Thread? _feeder;
    private volatile bool _running;
    private volatile bool _saveInFlight; // while true, the feeder pauses writing so a save can stream the arena
    private bool _timerRaised;
    private int _fps, _audioBitrate;
    private long _bufferNs;
    private bool _wantAudio;
    private bool _captureIsWindow;  // window/active-window capture letterbox-scales into the fixed canvas

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
    // A/V-sync diagnostics: the audio timeline position vs the master (video) wall clock. These should stay
    // within a few ms of each other even across a clip save; if audio falls behind, that's the desync.
    internal long DiagAudioClockNs { get { lock (_ringLock) return _audioAnchored ? _audioAnchorNs + _audioSamples * 10_000_000L / AudioRate : 0; } }
    internal long DiagVideoClockNs => _clock?.Elapsed.Ticks ?? 0;
    internal void DebugHoldSave(int ms) { _saveInFlight = true; try { Thread.Sleep(ms); } finally { _saveInFlight = false; } }

    /// <summary>The GPU buffer handles MP4 full-screen / monitor / window / active-window capture.</summary>
    public static bool CanHandle(RecordingProfile p) =>
        p.Container == OutputContainer.Mp4 &&
        p.Source is CaptureSource.FullScreen or CaptureSource.Monitor or CaptureSource.Window or CaptureSource.ActiveWindow;

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
                var target = CaptureTarget.Resolve(profile);
                _captureIsWindow = target.IsWindow;
                var cap = new GpuWgcCapture(gpu, target.Handle, target.CaptureCursor, target.IsWindow, target.FollowActive);
                if (!cap.WaitForFirstFrame(2500, out int w, out int h))
                {
                    cap.Dispose(); gpu.Dispose();
                    throw new InvalidOperationException("Replay buffer: no frame captured.");
                }
                // Window/active capture scales into a fixed canvas so the encoder size never changes mid-stream.
                int canvasW = target.CanvasWidth > 0 ? target.CanvasWidth : w;
                int canvasH = target.CanvasHeight > 0 ? target.CanvasHeight : h;
                var conv = new VideoProcessorConverter(gpu, canvasW, canvasH, _fps);
                var enc = new MfH264EncoderMft(gpu, conv.Width, conv.Height, _fps, videoBps, OnEncodedVideo);

                _nv12Pool = new ID3D11Texture2D[PoolSize];
                for (int i = 0; i < PoolSize; i++) _nv12Pool[i] = conv.CreateNv12Texture();

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

                // While a clip is being saved we pause capture so the muxer can stream the encoded bytes
                // straight from the arena (no overwrite, no multi-hundred-MB copy). Brief buffering gap only.
                if (!_saveInFlight)
                {
                    _cap!.PullLatest();                       // refresh the latest frame (drains the WGC pool)
                    var src = _cap.LatestTexture;
                    if (src is not null)                      // null only before the very first frame
                    {
                        int slot = (int)(frame % PoolSize);
                        if (_captureIsWindow)                  // letterbox-scale the (any-size) window into the canvas
                            _conv!.ConvertScaled(src, _cap.LatestWidth, _cap.LatestHeight, _nv12Pool![slot]);
                        else
                            _conv!.Convert(src, _nv12Pool![slot]); // monitor: straight BGRA→NV12, no scaling
                        _gpu!.Context.Flush();
                        if (_enc!.TryConsumeNeedInput())
                            _enc.SubmitFrame(_nv12Pool[slot], _clock.Elapsed.Ticks, frameDur);
                    }
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
            if (arena is null || _saveInFlight) return; // stopped, or paused while a save streams the arena
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
            if (arena is null) return; // stopped / torn down
            if (!_audioAnchored)
            {
                long bufDur = perChannel * 10_000_000L / AudioRate;
                _audioAnchorNs = Math.Max(0, elapsed - bufDur);
                _audioSamples = 0;
                _audioAnchored = true;
            }
            // While a clip is being saved we can't write the arena (the muxer is streaming it), but we MUST keep
            // the audio sample clock advancing. If we froze it, the audio timeline would stall for the whole mux
            // while video PTS keeps tracking the wall clock — shifting every later audio sample behind the video
            // and ACCUMULATING into seconds of A/V desync across multiple saves. Advancing the count turns the
            // dropped span into brief silence (matching the paused video) instead of a permanent offset.
            if (_saveInFlight || count > arena.Capacity) { _audioSamples += perChannel; return; }
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
            VideoEntry[] vEntries;
            AudioEntry[] aEntries;
            long newestNs, startVideoNs;
            IMFMediaType? vType = _videoOutType;
            ByteArena? vArena, aArena;

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
                vArena = _videoArena; aArena = _audioArena;
                if (vArena is null) return Task.FromResult<string?>(null);

                // Snapshot only the lightweight METADATA (offsets/lengths — a few hundred KB), NOT the encoded
                // bytes. The muxer streams the bytes straight from the arena; we set _saveInFlight so the feeder
                // pauses and can't overwrite them. This avoids copying the whole (multi-minute) clip into managed
                // memory, which spiked + retained hundreds of MB.
                vEntries = new VideoEntry[_videoRing.Count - startIdx];
                _videoRing.CopyTo(startIdx, vEntries, 0, vEntries.Length);

                var aud = new List<AudioEntry>();
                foreach (var c in _audioRing)
                    if (c.TimeNs + c.DurNs > startVideoNs && c.TimeNs <= newestNs) aud.Add(c);
                aEntries = aud.ToArray();

                _saveInFlight = true; // pause capture for the duration of the mux (cleared in the Task's finally)
            }

            releaseNow = false; // the Task owns the gate + the _saveInFlight flag now
            try
            {
                return Task.Run(() =>
                {
                    try { MuxClip(outputPath, vType!, vArena, vEntries, aArena, aEntries, startVideoNs); return (string?)outputPath; }
                    finally { _saveInFlight = false; _saveGate.Release(); }
                });
            }
            catch { _saveInFlight = false; _saveGate.Release(); throw; } // scheduling failure: don't leak the gate/flag
        }
        finally { if (releaseNow) _saveGate.Release(); }
    }

    private void MuxClip(string outputPath, IMFMediaType videoType, ByteArena vArena, VideoEntry[] video, ByteArena? aArena, AudioEntry[] audio, long originNs)
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
            // the muxer (writing all of one stream first deadlocks WriteSample on stream sync). Each frame is
            // streamed from the arena into a single reused buffer per stream — no per-frame allocation, so a
            // long clip doesn't spike the heap. Capture is paused (_saveInFlight), so the arena is stable.
            byte[] vbuf = Array.Empty<byte>(), abuf = Array.Empty<byte>();
            int vi = 0, ai = 0;
            while (vi < video.Length || (aIdx >= 0 && ai < audio.Length))
            {
                bool writeVideo;
                if (vi >= video.Length) writeVideo = false;
                else if (aIdx < 0 || ai >= audio.Length) writeVideo = true;
                else writeVideo = video[vi].TimeNs <= audio[ai].TimeNs;

                if (writeVideo)
                {
                    var e = video[vi];
                    // Derive display duration from the gap to the next frame's PTS. The feeder runs variable-rate
                    // (it drops to a low keepalive rate on static screens), so the per-sample duration can't be
                    // trusted to reflect actual spacing; the PTS timeline is the source of truth for total length.
                    long nextNs = (vi + 1 < video.Length) ? video[vi + 1].TimeNs : e.TimeNs + e.DurNs;
                    long vdur = Math.Max(1, nextNs - e.TimeNs);
                    vi++;
                    if (vbuf.Length < e.Length) vbuf = new byte[e.Length];
                    vArena.ReadInto(e.Offset, e.Length, vbuf);
                    WriteBytes(w, vIdx, vbuf, e.Length, Math.Max(0, e.TimeNs - originNs), vdur, e.KeyFrame);
                }
                else
                {
                    var c = audio[ai++];
                    if (abuf.Length < c.Count) abuf = new byte[c.Count];
                    aArena!.ReadInto(c.Offset, c.Count, abuf);
                    WriteBytes(w, aIdx, abuf, c.Count, Math.Max(0, c.TimeNs - originNs), c.DurNs, false);
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
            // Wait out any in-flight save (it holds _saveGate) before tearing down the encoder/device/arena.
            // Bounded so a stuck muxer (severe disk/MF fault) can't deadlock shutdown forever; on timeout we
            // abandon teardown rather than free native memory the stuck mux is still reading (it would crash).
            // The leaked session is reclaimed on process exit. 30 s comfortably covers any real clip mux.
            if (!_saveGate.Wait(30000))
            {
                try { Diag?.Invoke("Stop: clip save still running after 30s; abandoning teardown."); } catch { }
                return;
            }
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
        if (_nv12Pool != null) foreach (var t in _nv12Pool) { try { t?.Dispose(); } catch { } }
        try { _conv?.Dispose(); } catch { }
        try { _cap?.Dispose(); } catch { }
        try { _gpu?.Dispose(); } catch { }
        _audio = null; _enc = null; _conv = null; _cap = null; _gpu = null;
        _nv12Pool = null; _videoOutType = null; _clock = null;
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

    public void Dispose() => Stop();
}
