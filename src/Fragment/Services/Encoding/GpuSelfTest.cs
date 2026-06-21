using System;
using System.IO;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Fragment.Services;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Fragment.Services.Encoding;

/// <summary>
/// Headless self-tests for the GPU recording engine (run via the FRAGMENT_GPUTEST env var) so the
/// engine can be validated without the GUI. Writes to %TEMP%\Fragment\gputest.log and dumps any images.
/// </summary>
internal static class GpuSelfTest
{
    private static string Dir => Path.Combine(Path.GetTempPath(), "Fragment");

    public static void Run(string mode)
    {
        try { Directory.CreateDirectory(Dir); } catch { }
        var log = Path.Combine(Dir, "gputest.log");
        var logGate = new object();
        void W(string s) { lock (logGate) { try { File.AppendAllText(log, s + Environment.NewLine); } catch { } } }
        try { File.WriteAllText(log, $"GPUTEST mode={mode} at {DateTime.Now:HH:mm:ss}{Environment.NewLine}"); } catch { }

        try
        {
            if (mode == "17") RunSettingsRoundTrip(W);
            else if (mode == "16") RunFollowFallbackTest(W);
            else if (mode == "14") RunWindowReplay(W);
            else if (mode == "13") RunWindowCapture(W);
            else if (mode == "12") RunAudioSaveSyncTest(W);
            else if (mode == "11") RunAudioLagTest(W);
            else if (mode == "10") RunWgcCadenceTest(W);
            else if (mode == "9") RunVariablePtsTest(W);
            else if (mode == "8") RunIsolationLeakTrace(W);
            else if (mode == "7") RunReplayLeakTrace(W);
            else if (mode == "6") RunMicDsp(W);
            else if (mode == "5") RunReplayBuffer(W);
            else if (mode == "4") RunEncoderMft(W);
            else if (mode == "3") RunRecord(W);
            else if (mode == "2") RunCaptureConvert(W);
            else RunDeviceOnly(W);
        }
        catch (Exception ex)
        {
            W("RESULT: FAIL");
            W(ex.ToString());
        }
    }

    private static void RunDeviceOnly(Action<string> W)
    {
        using var dev = new GpuRecordingDevice();
        W("shared D3D11 device created (BGRA+VideoSupport), multithread-protected");
        W($"WinRT device bridge: {(dev.WinRtDevice != null ? "ok" : "null")}");
        W($"MF DXGI device manager: {(dev.DeviceManager != null ? "ok" : "null")}");
        W("RESULT: PASS");
    }

    private static void RunCaptureConvert(Action<string> W)
    {
        using var gpu = new GpuRecordingDevice();
        IntPtr hmon = WgcCapture.MonitorFromPoint(0, 0); // primary
        using var cap = new GpuWgcCapture(gpu, hmon, captureCursor: true);
        if (!cap.WaitForFirstFrame(3000, out int w, out int h))
        {
            W("RESULT: FAIL - no frame captured in 3s");
            return;
        }
        W($"captured first frame {w}x{h}, arrived={cap.ArrivedCount}");

        using var conv = new VideoProcessorConverter(gpu, w, h, 60);
        using var bgra = conv.CreateInputTexture();
        using var nv12 = conv.CreateNv12Texture();

        if (!cap.CopyLatestInto(bgra)) { W("RESULT: FAIL - CopyLatestInto returned false"); return; }
        DumpBgra(gpu, bgra, conv.Width, conv.Height, Path.Combine(Dir, "capture_bgra.png"));
        W("wrote capture_bgra.png");

        // GPU BGRA->NV12, then read back NV12 and dump as a PNG (validates conversion + colours).
        conv.Convert(bgra, nv12);
        DumpNv12(gpu, nv12, conv.Width, conv.Height, Path.Combine(Dir, "capture_nv12.png"));
        W($"wrote capture_nv12.png ({conv.Width}x{conv.Height})");
        W("RESULT: PASS");
    }

    private static void RunRecord(Action<string> W)
    {
        using var gpu = new GpuRecordingDevice();
        var target = new CaptureTarget { Handle = WgcCapture.MonitorFromPoint(0, 0), IsWindow = false, CaptureCursor = true };
        string outPath = Path.Combine(Dir, "gpu_record.mp4");
        try { File.Delete(outPath); } catch { }

        using (var rec = new GpuVideoRecorder(gpu, target, outPath, 60, 16_000_000, audio: Fragment.Models.AudioMode.SystemOnly, diag: W))
        {
            var proc = System.Diagnostics.Process.GetCurrentProcess();
            W($"recording 10s at {rec.Width}x{rec.Height}@60 (audio={rec.HasAudio}) to gpu_record.mp4 ...");
            rec.Start();

            var cpu0 = proc.TotalProcessorTime;
            var wall = System.Diagnostics.Stopwatch.StartNew();
            System.Threading.Thread.Sleep(10000);
            double cpuMs = (proc.TotalProcessorTime - cpu0).TotalMilliseconds;
            double wallMs = wall.Elapsed.TotalMilliseconds;

            rec.Stop();
            int cores = Environment.ProcessorCount;
            W($"frames emitted: {rec.FramesEmitted}, copyFalse: {rec.CopyFalseCount}, arrived: {rec.ArrivedCount}, audioBufs: {rec.AudioBuffers}");
            W($"CPU: {cpuMs:F0}ms over {wallMs:F0}ms = {cpuMs / wallMs * 100:F1}% of 1 core ({cpuMs / wallMs / cores * 100:F2}% of all {cores} cores)");
            if (rec.LastError != null) W("lastError: " + rec.LastError);
        }
        W("recorder disposed");

        var fi = new FileInfo(outPath);
        W(fi.Exists ? $"wrote gpu_record.mp4 ({fi.Length:N0} bytes)" : "RESULT: FAIL - file missing");
        if (fi.Exists && fi.Length > 0) W("RESULT: PASS");
    }

    // Mic DSP correctness: gate passes loud audio / blocks silence; suppressor stays finite + right length.
    private sealed class TestTone : NAudio.Wave.ISampleProvider
    {
        private readonly float _amp, _freq; private int _pos; private readonly int _len;
        public TestTone(float amp, float freq, int lenSamples) { _amp = amp; _freq = freq; _len = lenSamples; }
        public NAudio.Wave.WaveFormat WaveFormat { get; } = NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(48000, 1);
        public int Read(float[] b, int off, int count)
        {
            int n = Math.Min(count, _len - _pos);
            for (int i = 0; i < n; i++) { b[off + i] = _amp * (float)Math.Sin(2 * Math.PI * _freq * _pos / 48000.0); _pos++; }
            return n;
        }
    }

    private static (double rms, long n, bool finite) Drain(NAudio.Wave.ISampleProvider p)
    {
        var buf = new float[4096]; double sq = 0; long n = 0; bool finite = true; int r;
        while ((r = p.Read(buf, 0, buf.Length)) > 0)
            for (int i = 0; i < r; i++) { float v = buf[i]; if (!float.IsFinite(v)) finite = false; sq += v * v; n++; }
        return (Math.Sqrt(sq / Math.Max(1, n)), n, finite);
    }

    private static void RunMicDsp(Action<string> W)
    {
        int len = 48000; // 1 s
        var gLoud = Drain(new NoiseGateSampleProvider(new TestTone(0.5f, 440, len), -40f));
        var gQuiet = Drain(new NoiseGateSampleProvider(new TestTone(0.0008f, 440, len), -40f)); // ~ -62 dBFS -> gated
        var sup = Drain(new SpectralNoiseSuppressor(new TestTone(0.5f, 440, len), 0.6f));

        W($"gate(loud  0.5): outRMS={gLoud.rms:F4} n={gLoud.n} finite={gLoud.finite}  (expect ~0.35, pass-through)");
        W($"gate(quiet -62dB): outRMS={gQuiet.rms:F5} n={gQuiet.n}  (expect ~0, gated)");
        W($"suppress(0.6): outRMS={sup.rms:F4} n={sup.n} finite={sup.finite}  (finite + ~len samples)");

        bool ok = gLoud.finite && gLoud.rms > 0.2 &&         // loud tone passes the gate
                  gQuiet.rms < 0.02 &&                        // quiet gated out
                  sup.finite && sup.n > len - 2048;           // suppressor stable + ~full length
        W(ok ? "RESULT: PASS" : "RESULT: FAIL");
    }

    // Phase 5: always-on GPU replay buffer -> save a GOP-aligned clip.
    private static void RunReplayBuffer(Action<string> W)
    {
        string outPath = Path.Combine(Dir, "gpu_clip.mp4");
        try { File.Delete(outPath); } catch { }

        var profile = new Fragment.Models.RecordingProfile
        {
            Source = Fragment.Models.CaptureSource.FullScreen,
            Container = Fragment.Models.OutputContainer.Mp4,
            Fps = 60,
            VideoBitrateKbps = 16000,
            AudioBitrateKbps = 160,
            Audio = Fragment.Models.AudioMode.SystemOnly,
            CaptureCursor = true,
        };

        using var buf = new GpuReplayBuffer { Diag = W };
        var proc = System.Diagnostics.Process.GetCurrentProcess();
        W("starting replay buffer (15s window)...");
        buf.Start(profile, bufferSeconds: 15);
        W($"IsRunning={buf.IsRunning}");

        var cpu0 = proc.TotalProcessorTime;
        var wall = System.Diagnostics.Stopwatch.StartNew();
        System.Threading.Thread.Sleep(12000); // accumulate footage
        double cpuMs = (proc.TotalProcessorTime - cpu0).TotalMilliseconds;
        double wallMs = wall.Elapsed.TotalMilliseconds;
        W($"idle CPU while buffering: {cpuMs / wallMs * 100:F1}% of 1 core ({cpuMs / wallMs / Environment.ProcessorCount * 100:F2}% of {Environment.ProcessorCount} cores)");

        W("saving 8s clip...");
        string? saved = buf.SaveClipAsync(8, outPath).GetAwaiter().GetResult();
        W($"SaveClipAsync returned: {(saved ?? "null")}");

        buf.Stop();
        W($"IsRunning after stop={buf.IsRunning}");

        var fi = new FileInfo(outPath);
        if (saved != null && fi.Exists && fi.Length > 0)
        {
            W($"wrote gpu_clip.mp4 ({fi.Length:N0} bytes)");
            W("RESULT: PASS");
        }
        else W("RESULT: FAIL - no clip produced");
    }

    // Isolation leak trace: run ONLY capture -> convert -> (optionally) encode in a tight 60fps loop,
    // discarding output (no ring, no audio), logging process memory every 5s. Binary-search the leak:
    //   FRAGMENT_GPUTEST_NOENC=1  -> capture + convert only (no encoder)   -> isolates WGC/VideoProcessor
    //   (default)                 -> capture + convert + encode            -> adds the encoder MFT path
    // If NOENC is flat but the default climbs, the leak is in the encoder submit/drain path, and vice versa.
    private static void RunIsolationLeakTrace(Action<string> W)
    {
        int seconds = 120;
        if (int.TryParse(Environment.GetEnvironmentVariable("FRAGMENT_GPUTEST_SECONDS"), out int s) && s > 0) seconds = s;
        bool noEnc = Environment.GetEnvironmentVariable("FRAGMENT_GPUTEST_NOENC") == "1";
        const int Fps = 60, Pool = 12;

        using var gpu = new GpuRecordingDevice();
        IntPtr hmon = WgcCapture.MonitorFromPoint(0, 0);
        using var cap = new GpuWgcCapture(gpu, hmon, captureCursor: true);
        if (!cap.WaitForFirstFrame(5000, out int w, out int h)) { W("RESULT: FAIL - no frame"); return; }
        using var conv = new VideoProcessorConverter(gpu, w, h, Fps);

        long encOut = 0;
        MfH264EncoderMft? enc = noEnc ? null : new MfH264EncoderMft(gpu, conv.Width, conv.Height, Fps, 16_000_000,
            _ => System.Threading.Interlocked.Increment(ref encOut));

        // Optional: run system-audio capture too (isolates GpuAudioCapture as a leak source).
        GpuAudioCapture? audio = null;
        if (Environment.GetEnvironmentVariable("FRAGMENT_GPUTEST_AUDIO") == "1")
        {
            audio = new GpuAudioCapture(captureSystem: true, captureMic: false, (_, __) => { });
            audio.Start();
        }

        var input = new ID3D11Texture2D[Pool];
        var nv12 = new ID3D11Texture2D[Pool];
        for (int i = 0; i < Pool; i++) { input[i] = conv.CreateInputTexture(); nv12[i] = conv.CreateNv12Texture(); }

        var proc = System.Diagnostics.Process.GetCurrentProcess();
        W($"isolation trace: {seconds}s, encoder={(noEnc ? "OFF" : "ON")} audio={(audio != null ? "ON" : "OFF")} {conv.Width}x{conv.Height}@{Fps}. columns: t frames enc | managedMB wsMB privMB gen2");

        var clock = System.Diagnostics.Stopwatch.StartNew();
        long freq = System.Diagnostics.Stopwatch.Frequency;
        long frame = 0, nextLog = 5;
        while (clock.Elapsed.TotalSeconds < seconds)
        {
            long deadline = frame * freq / Fps;
            long now = clock.ElapsedTicks;
            if (deadline > now) { int ms = (int)((deadline - now) * 1000 / freq); if (ms > 0) System.Threading.Thread.Sleep(ms); }

            int slot = (int)(frame % Pool);
            if (cap.CopyLatestInto(input[slot]))
            {
                conv.Convert(input[slot], nv12[slot]);
                gpu.Context.Flush();
                if (enc != null && enc.TryConsumeNeedInput())
                    enc.SubmitFrame(nv12[slot], clock.Elapsed.Ticks, 10_000_000L / Fps);
            }
            frame++;

            if (clock.Elapsed.TotalSeconds >= nextLog)
            {
                nextLog += 5;
                proc.Refresh();
                W($"t={clock.Elapsed.TotalSeconds,4:F0}s  frames={frame,6} enc={System.Threading.Interlocked.Read(ref encOut),6} | managedMB={GC.GetTotalMemory(false) / 1048576.0,6:F1} wsMB={proc.WorkingSet64 / 1048576.0,7:F1} privMB={proc.PrivateMemorySize64 / 1048576.0,7:F1} gen2={GC.CollectionCount(2)}");
            }
        }

        for (int i = 0; i < Pool; i++) { try { input[i].Dispose(); } catch { } try { nv12[i].Dispose(); } catch { } }
        try { enc?.Dispose(); } catch { }
        try { audio?.Dispose(); } catch { }
        W("RESULT: isolation trace complete");
    }

    // Leak trace: run the replay buffer for N seconds (FRAGMENT_GPUTEST_SECONDS, default 240) with a SMALL
    // 30 s window, logging ring + process memory every 5 s. The ring should plateau by ~35 s; if working-set
    // keeps climbing after that it's a leak, and the per-metric breakdown localises it (managed vs native).
    private static void RunReplayLeakTrace(Action<string> W)
    {
        int seconds = 240;
        if (int.TryParse(Environment.GetEnvironmentVariable("FRAGMENT_GPUTEST_SECONDS"), out int s) && s > 0) seconds = s;

        int mon = int.TryParse(Environment.GetEnvironmentVariable("FRAGMENT_GPUTEST_MON"), out int mi) ? mi : -1;
        var profile = new Fragment.Models.RecordingProfile
        {
            Source = mon >= 0 ? Fragment.Models.CaptureSource.Monitor : Fragment.Models.CaptureSource.FullScreen,
            MonitorIndex = Math.Max(0, mon),
            Container = Fragment.Models.OutputContainer.Mp4,
            Fps = 60,
            VideoBitrateKbps = 16000,
            AudioBitrateKbps = 160,
            Audio = Fragment.Models.AudioMode.SystemOnly,
            CaptureCursor = true,
        };
        W($"capture source: {(mon >= 0 ? $"monitor {mon}" : "primary/fullscreen")}");

        using var buf = new GpuReplayBuffer { Diag = _ => { } }; // suppress per-event diag noise
        var proc = System.Diagnostics.Process.GetCurrentProcess();
        const int windowSec = 30;
        W($"leak trace: {seconds}s run, {windowSec}s ring window (plateau expected ~{windowSec + 5}s). columns: t ring ringMB aud enc key arrived | managedMB wsMB privMB gen2");
        buf.Start(profile, bufferSeconds: windowSec);

        bool forceGc = Environment.GetEnvironmentVariable("FRAGMENT_GPUTEST_FORCEGC") == "1";
        int saveAt = int.TryParse(Environment.GetEnvironmentVariable("FRAGMENT_GPUTEST_SAVEAT"), out int sa) ? sa : 0;
        bool saved = false;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < seconds && buf.IsRunning)
        {
            System.Threading.Thread.Sleep(5000);
            if (saveAt > 0 && !saved && sw.Elapsed.TotalSeconds >= saveAt)
            {
                saved = true;
                W($"--- SAVE START at t={sw.Elapsed.TotalSeconds:F0}s ---");
                string clip = System.IO.Path.Combine(Dir, "leaktrace_clip.mp4");
                try { System.IO.File.Delete(clip); } catch { }
                _ = buf.SaveClipAsync(windowSec - 5, clip).ContinueWith(t =>
                    W($"--- SAVE DONE: {(t.Result ?? "null")} ({(System.IO.File.Exists(clip) ? new System.IO.FileInfo(clip).Length : 0)} bytes) ---"));
            }
            if (forceGc) // measure the TRUE reclaimable floor: blocking compacting gen2 + LOH compaction
            {
                System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();
                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            }
            proc.Refresh();
            double manMB = GC.GetTotalMemory(false) / 1048576.0;
            double wsMB = proc.WorkingSet64 / 1048576.0;
            double pvMB = proc.PrivateMemorySize64 / 1048576.0;
            W($"t={sw.Elapsed.TotalSeconds,4:F0}s  ring={buf.DiagVideoRingCount,5} ringMB={buf.DiagVideoRingBytes / 1048576.0,6:F1} aud={buf.DiagAudioRingCount,4} enc={buf.DiagEncSamples,6} key={buf.DiagEncKeyframes,4} arrived={buf.DiagArrived,6} | managedMB={manMB,6:F1} wsMB={wsMB,7:F1} privMB={pvMB,7:F1} gen2={GC.CollectionCount(2)}");
        }

        buf.Stop();
        W("RESULT: trace complete");
    }

    // Reproduces the EXACT settings-dialog save flow (Clone -> edit the clone -> CopyFrom -> Save -> reload)
    // to find why settings don't persist across restarts. Non-destructive: backs up + restores settings.json.
    private static void RunSettingsRoundTrip(Action<string> W)
    {
        string path = SettingsService.SettingsPath;
        string? backup = File.Exists(path) ? File.ReadAllText(path) : null;
        try
        {
            var live = SettingsService.Load();                  // what MainViewModel holds
            var clone = live.Clone();                           // what the dialog edits
            clone.ClipLengthSeconds = clone.ClipLengthSeconds == 77 ? 78 : 77;
            clone.UseGpuEngine = !clone.UseGpuEngine;
            clone.ActiveProfile().Fps = clone.ActiveProfile().Fps == 47 ? 48 : 47;
            clone.ActiveProfile().Source = Fragment.Models.CaptureSource.ActiveWindow;
            clone.ActiveProfile().VideoBitrateKbps = 23456;
            int wantClip = clone.ClipLengthSeconds, wantFps = clone.ActiveProfile().Fps; bool wantGpu = clone.UseGpuEngine;

            live.CopyFrom(clone);                               // dialog Save step 1
            SettingsService.Save(live);                         // dialog Save step 2 (writes disk)

            var reloaded = SettingsService.Load();              // next app launch
            var p = reloaded.ActiveProfile();
            W($"ClipLengthSeconds: got {reloaded.ClipLengthSeconds}, want {wantClip} -> {(reloaded.ClipLengthSeconds == wantClip ? "OK" : "LOST")}");
            W($"profile.Fps: got {p.Fps}, want {wantFps} -> {(p.Fps == wantFps ? "OK" : "LOST")}");
            W($"profile.Source: got {p.Source}, want ActiveWindow -> {(p.Source == Fragment.Models.CaptureSource.ActiveWindow ? "OK" : "LOST")}");
            W($"profile.VideoBitrateKbps: got {p.VideoBitrateKbps}, want 23456 -> {(p.VideoBitrateKbps == 23456 ? "OK" : "LOST")}");
            W($"UseGpuEngine: got {reloaded.UseGpuEngine}, want {wantGpu} -> {(reloaded.UseGpuEngine == wantGpu ? "OK" : "LOST")}");
            bool ok = reloaded.ClipLengthSeconds == wantClip && p.Fps == wantFps && p.Source == Fragment.Models.CaptureSource.ActiveWindow && p.VideoBitrateKbps == 23456 && reloaded.UseGpuEngine == wantGpu;
            W(ok ? "RESULT: PASS - dialog save flow persists" : "RESULT: FAIL - some settings did not persist");
        }
        finally
        {
            try { if (backup != null) File.WriteAllText(path, backup); else File.Delete(path); } catch { }
            W("(restored original settings.json)");
        }
    }

    // Active-window FALLBACK: when there is no followable window (desktop focused / last window closed),
    // capture must fall back to full screen, not stop. Proven by constructing a follow-active capture with a
    // deliberately bogus initial handle (the "no window" worst case): the OLD code threw; the new code resolves
    // its own target (window or monitor) and starts. Also checks the IsFollowable predicate that drives it.
    private static void RunFollowFallbackTest(Action<string> W)
    {
        uint self = (uint)Environment.ProcessId;
        IntPtr fg = WindowEnumerator.Foreground();
        W($"IsFollowable(foreground \"{WindowEnumerator.TitleOf(fg)}\") = {WindowEnumerator.IsFollowable(fg, self)}");
        W($"IsFollowable(IntPtr.Zero / no window) = {WindowEnumerator.IsFollowable(IntPtr.Zero, self)} (False => falls back to full screen)");

        using var gpu = new GpuRecordingDevice();
        using var cap = new GpuWgcCapture(gpu, IntPtr.Zero, captureCursor: true, isWindow: false, followActive: true);
        bool ok = cap.WaitForFirstFrame(3000, out int w, out int h);
        W($"follow-active capture built with a BOGUS initial handle -> firstFrame={ok} {w}x{h}");
        // Pull a few more frames to exercise the per-frame retarget path without throwing.
        for (int i = 0; i < 10; i++) { cap.PullLatest(); System.Threading.Thread.Sleep(20); }
        W($"after 10 pulls: arrived={cap.ArrivedCount}, latest={cap.LatestWidth}x{cap.LatestHeight}");
        W(ok ? "RESULT: PASS - follow-active starts + survives with no valid initial window (falls back, never stops)" : "RESULT: FAIL");
    }

    // End-to-end: the replay buffer capturing the ACTIVE WINDOW (follow-active), scaling into the fixed canvas,
    // then saving a clip. Proves window capture -> letterbox scale -> encode -> save through the real buffer.
    // FRAGMENT_GPUTEST_SOURCE=window targets the foreground window fixed (no follow) instead of active-follow.
    private static void RunWindowReplay(Action<string> W)
    {
        bool fixedWindow = Environment.GetEnvironmentVariable("FRAGMENT_GPUTEST_SOURCE") == "window";
        var profile = new Fragment.Models.RecordingProfile
        {
            Source = fixedWindow ? Fragment.Models.CaptureSource.Window : Fragment.Models.CaptureSource.ActiveWindow,
            WindowHandle = fixedWindow ? WindowEnumerator.Foreground() : IntPtr.Zero,
            Container = Fragment.Models.OutputContainer.Mp4,
            Fps = 60, VideoBitrateKbps = 12000, AudioBitrateKbps = 160,
            Audio = Fragment.Models.AudioMode.SystemOnly, CaptureCursor = true,
        };
        W($"replay buffer source={profile.Source}, foreground=\"{WindowEnumerator.TitleOf(WindowEnumerator.Foreground())}\"");
        using var buf = new GpuReplayBuffer { Diag = _ => { } };
        buf.Start(profile, bufferSeconds: 15);
        W($"IsRunning={buf.IsRunning}");
        System.Threading.Thread.Sleep(8000);
        string clip = Path.Combine(Dir, "window_replay.mp4");
        try { File.Delete(clip); } catch { }
        var r = buf.SaveClipAsync(6, clip).GetAwaiter().GetResult();
        buf.Stop();
        long sz = File.Exists(clip) ? new FileInfo(clip).Length : 0;
        W($"saved: {(r ?? "null")} ({sz:N0} bytes)");
        W(sz > 100_000 ? "RESULT: PASS" : "RESULT: FAIL - clip missing/too small");
    }

    // Proves WGC per-WINDOW capture: lists capturable windows, then captures the FOREGROUND window via
    // CreateForWindow and dumps a frame. FRAGMENT_GPUTEST_HWND=<hex> targets a specific window instead.
    private static void RunWindowCapture(Action<string> W)
    {
        var wins = WindowEnumerator.List();
        W($"capturable windows: {wins.Count}");
        int shown = 0;
        foreach (var win in wins) { if (shown++ >= 12) break; W($"  hwnd=0x{win.Handle.ToInt64():X} pid={win.ProcessId,6} [{win.ProcessName}] {win.Title}"); }

        IntPtr hwnd = WindowEnumerator.Foreground();
        var env = Environment.GetEnvironmentVariable("FRAGMENT_GPUTEST_HWND");
        if (!string.IsNullOrWhiteSpace(env) && long.TryParse(env, System.Globalization.NumberStyles.HexNumber, null, out long h)) hwnd = (IntPtr)h;
        W($"capturing window 0x{hwnd.ToInt64():X} \"{WindowEnumerator.TitleOf(hwnd)}\"");

        using var gpu = new GpuRecordingDevice();
        using var cap = new GpuWgcCapture(gpu, hwnd, captureCursor: true, isWindow: true);
        if (!cap.WaitForFirstFrame(3000, out int w, out int hh)) { W("RESULT: FAIL - no window frame in 3s"); return; }
        W($"first window frame {w}x{hh}, arrived={cap.ArrivedCount}");

        // Scale the (any-size) window into a FIXED 1280x720 canvas with letterbox bars — exactly what the
        // recorder/replay-buffer will do so the encoder size stays constant across window resizes/switches.
        using var conv = new VideoProcessorConverter(gpu, 1280, 720, 60);
        using var nv12 = conv.CreateNv12Texture();
        for (int i = 0; i < 6; i++) { cap.PullLatest(); System.Threading.Thread.Sleep(20); }
        var src = cap.LatestTexture;
        if (src is null) { W("RESULT: FAIL - no latest texture"); return; }
        conv.ConvertScaled(src, w, hh, nv12);
        gpu.Context.Flush();
        DumpNv12(gpu, nv12, conv.Width, conv.Height, Path.Combine(Dir, "window_capture.png"));
        W($"wrote window_capture.png ({w}x{hh} window -> {conv.Width}x{conv.Height} canvas, letterboxed)");
        W("RESULT: PASS");
    }

    // Regression test for the clip-save A/V desync: a clip save pauses capture (_saveInFlight) while the muxer
    // streams the arena. The audio sample-clock MUST keep advancing during that pause, else audio falls behind
    // the wall-clock-stamped video by the mux duration, accumulating across saves. Amplifies a save into a
    // measurable 2s hold and checks the audio↔video skew is unchanged across it.
    private static void RunAudioSaveSyncTest(Action<string> W)
    {
        var profile = new Fragment.Models.RecordingProfile
        {
            Source = Fragment.Models.CaptureSource.FullScreen,
            Container = Fragment.Models.OutputContainer.Mp4,
            Fps = 60, VideoBitrateKbps = 16000, AudioBitrateKbps = 160,
            Audio = Fragment.Models.AudioMode.SystemOnly, CaptureCursor = true,
        };
        using var buf = new GpuReplayBuffer { Diag = _ => { } };
        buf.Start(profile, bufferSeconds: 30);
        System.Threading.Thread.Sleep(4000); // let the audio anchor + settle
        double skew0 = (buf.DiagVideoClockNs - buf.DiagAudioClockNs) / 10000.0;
        W($"before save-pause: video={buf.DiagVideoClockNs / 10000.0:F0}ms audio={buf.DiagAudioClockNs / 10000.0:F0}ms skew={skew0:F0}ms");
        W("simulating a 2000ms clip-save pause (_saveInFlight held)...");
        buf.DebugHoldSave(2000);
        System.Threading.Thread.Sleep(600); // let audio resume for a few callbacks
        double skew1 = (buf.DiagVideoClockNs - buf.DiagAudioClockNs) / 10000.0;
        W($"after save-pause:  video={buf.DiagVideoClockNs / 10000.0:F0}ms audio={buf.DiagAudioClockNs / 10000.0:F0}ms skew={skew1:F0}ms");
        double drift = skew1 - skew0;
        W($"A/V skew change across the save: {drift:F0}ms  (FIXED: ~0; BUG froze audio: ~+2000ms)");
        buf.Stop();
        W(Math.Abs(drift) < 300 ? "RESULT: PASS - audio clock stayed aligned across the save" : "RESULT: FAIL - audio fell behind the video by ~the save duration");
    }

    // Measures the audio A/V-sync lag at its source: runs the real GpuAudioCapture (system + mic, matching the
    // user's SystemAndMic config) and logs how much un-read audio sits in each source buffer over time. The pump
    // drains at exactly real-time rate and never catches up, so any PERSISTENT buffer backlog (in ms) is frozen
    // into the timeline as a fixed audio-lags-video offset. Content-independent, so it measures even with silence.
    private static void RunAudioLagTest(Action<string> W)
    {
        var total = new long[1];
        using var cap = new GpuAudioCapture(captureSystem: true, captureMic: true,
            (_, n) => System.Threading.Interlocked.Add(ref total[0], n)) { Diag = W };
        if (!cap.Active) { W("RESULT: FAIL - no audio source initialised"); return; }
        W($"audio: sys+mic, {cap.SampleRate}Hz x{cap.Channels}");
        int seconds = int.TryParse(Environment.GetEnvironmentVariable("FRAGMENT_GPUTEST_SECONDS"), out int s) && s > 0 ? s : 8;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        cap.Start();
        W("columns: t(ms) | sysBufferedMs micBufferedMs (un-read backlog == the A/V lag) | pcmDeliveredMs (should track t)");
        double maxSys = 0, maxMic = 0;
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            System.Threading.Thread.Sleep(300);
            long bytes = System.Threading.Interlocked.Read(ref total[0]);
            double pcmMs = bytes / (double)(cap.SampleRate * cap.Channels * 2) * 1000.0;
            double sb = cap.SysBufferedMs, mb = cap.MicBufferedMs;
            if (sb > maxSys) maxSys = sb;
            if (mb > maxMic) maxMic = mb;
            W($"t={sw.ElapsedMilliseconds,5}  sysBuf={sb,7:F0}ms  micBuf={mb,7:F0}ms  pcmDelivered={pcmMs,7:F0}ms");
        }
        W($"SETTLED backlog (== fixed audio lag): sys≈{cap.SysBufferedMs:F0}ms mic≈{cap.MicBufferedMs:F0}ms (peak sys={maxSys:F0} mic={maxMic:F0})");
        W("RESULT: PASS");
    }

    // Determines WGC's delivery cadence for STATIC content: does the frame pool hand us a fresh frame every
    // refresh (so "a new frame arrived" != "the screen changed"), or only when pixels actually change? Polls at
    // 60fps, reads back a sparse pixel sample of each delivered frame, and counts how many are byte-identical to
    // the previous. High duplicates while arrived keeps climbing => encode-on-change must compare frames itself.
    private static void RunWgcCadenceTest(Action<string> W)
    {
        int mon = int.TryParse(Environment.GetEnvironmentVariable("FRAGMENT_GPUTEST_MON"), out int mi) ? mi : 0;
        int seconds = int.TryParse(Environment.GetEnvironmentVariable("FRAGMENT_GPUTEST_SECONDS"), out int s) && s > 0 ? s : 5;
        using var gpu = new GpuRecordingDevice();
        IntPtr hmon = WgcCapture.MonitorFromPoint(0, 0);
        if (mon > 0) { var m = Fragment.Services.MonitorEnumerator.GetByIndex(mon); if (m != null) hmon = WgcCapture.MonitorFromPoint(m.X + 1, m.Y + 1); }
        using var cap = new GpuWgcCapture(gpu, hmon, captureCursor: false); // cursor off: isolate CONTENT change
        if (!cap.WaitForFirstFrame(5000, out int w, out int h)) { W("RESULT: FAIL - no frame"); return; }
        W($"monitor {mon}: {w}x{h}, polling at 60fps for {seconds}s (cursor capture off)");

        ID3D11Texture2D? staging = null;
        long lastArrived = -1, prevHash = 0; int unique = 0, dup = 0, polls = 0;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        long freq = System.Diagnostics.Stopwatch.Frequency; long frame = 0;
        while (clock.Elapsed.TotalSeconds < seconds)
        {
            long deadline = frame * freq / 60; long now = clock.ElapsedTicks;
            if (deadline > now) { int ms = (int)((deadline - now) * 1000 / freq); if (ms > 0) System.Threading.Thread.Sleep(ms); }
            frame++; polls++;

            cap.PullLatest();
            var src = cap.LatestTexture; if (src is null) continue;
            long arrived = cap.ArrivedCount;
            if (arrived == lastArrived) continue; // no new frame delivered this poll
            lastArrived = arrived;

            var d = src.Description;
            if (staging is null)
                staging = gpu.Device.CreateTexture2D(new Texture2DDescription
                {
                    Width = d.Width, Height = d.Height, MipLevels = 1, ArraySize = 1, Format = d.Format,
                    SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Staging,
                    BindFlags = BindFlags.None, CPUAccessFlags = CpuAccessFlags.Read, MiscFlags = ResourceOptionFlags.None,
                });
            gpu.Context.CopyResource(staging, src);
            var map = gpu.Context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            long hash = 1469598103934665603L; // FNV-ish over a sparse grid of pixels
            unsafe
            {
                byte* basep = (byte*)map.DataPointer;
                for (int y = 0; y < (int)d.Height; y += 37)
                {
                    uint* row = (uint*)(basep + (long)y * map.RowPitch);
                    for (int x = 0; x < (int)d.Width; x += 53) { hash = (hash ^ row[x]) * 1099511628211L; }
                }
            }
            gpu.Context.Unmap(staging, 0);
            if (hash == prevHash) dup++; else unique++;
            prevHash = hash;
        }
        try { staging?.Dispose(); } catch { }
        W($"polls={polls} arrived={cap.ArrivedCount} delivered-frames-sampled={unique + dup} unique={unique} duplicate={dup}");
        W($"delivery rate={cap.ArrivedCount / (double)seconds:F0}/s, of which ~{dup * 100.0 / Math.Max(1, unique + dup):F0}% were byte-identical to the previous");
        W(dup > unique
            ? "VERDICT: WGC delivers DUPLICATE frames for static content -> encode-on-change must diff frames itself"
            : "VERDICT: most delivered frames differ -> screen was active (or WGC coalesces); inconclusive for idle unless duplicate% is high");
        W("RESULT: PASS");
    }

    // Linchpin test for encode-on-change: submit frames at deliberately VARIABLE intervals (mimicking a static
    // screen that drops to the keepalive rate) and confirm the hardware encoder PRESERVES the input PTS on its
    // output (rather than re-timing to a constant frame rate). If the output PTS span tracks the input span, a
    // variable-rate replay stream muxes to a clip with the correct total length; if not, encode-on-change is unsafe.
    private static void RunVariablePtsTest(Action<string> W)
    {
        const int Fps = 60, Pool = 6;
        using var gpu = new GpuRecordingDevice();
        IntPtr hmon = WgcCapture.MonitorFromPoint(0, 0);
        using var cap = new GpuWgcCapture(gpu, hmon, captureCursor: true);
        if (!cap.WaitForFirstFrame(5000, out int w, out int h)) { W("RESULT: FAIL - no frame"); return; }
        using var conv = new VideoProcessorConverter(gpu, w, h, Fps);
        var nv12 = new ID3D11Texture2D[Pool];
        for (int i = 0; i < Pool; i++) nv12[i] = conv.CreateNv12Texture();

        var outTimes = new System.Collections.Generic.List<long>();
        int outKeys = 0;
        var gate = new object();
        using (var enc = new MfH264EncoderMft(gpu, conv.Width, conv.Height, Fps, 16_000_000, s =>
        {
            lock (gate) { outTimes.Add(s.TimeNs); if (s.KeyFrame) outKeys++; }
        }))
        {
            // Inter-frame gaps in ms: a few 60fps frames, then long static-keepalive gaps, then motion again.
            long[] gapsMs = { 0, 16, 16, 1000, 1000, 1000, 16, 16, 16, 2000, 1000, 500 };
            long tNs = 0; int submitted = 0;
            for (int i = 0; i < gapsMs.Length; i++)
            {
                tNs += gapsMs[i] * 10_000L;                       // ms -> 100ns ticks
                cap.PullLatest(); var src = cap.LatestTexture; if (src is null) continue;
                int spin = 0; while (!enc.TryConsumeNeedInput() && spin++ < 1500) System.Threading.Thread.Sleep(2);
                int slot = submitted % Pool;
                conv.Convert(src, nv12[slot]); gpu.Context.Flush();
                enc.RequestKeyFrame();                            // force a keyframe (as the static keepalive does)
                enc.SubmitFrame(nv12[slot], tNs, 10_000_000L / Fps);
                submitted++;
                System.Threading.Thread.Sleep((int)Math.Max(1, gapsMs[i])); // pace in real time
            }
            System.Threading.Thread.Sleep(2000);                  // let in-flight encodes arrive
            long inSpan = tNs;
            lock (gate)
            {
                W($"submitted {submitted} frames; input PTS span={inSpan / 10000.0:F0}ms");
                W($"got {outTimes.Count} outputs ({outKeys} keyframes)");
                outTimes.Sort();
                for (int i = 0; i < outTimes.Count; i++) W($"  out[{i,2}] t={outTimes[i] / 10000.0,7:F1}ms");
                long outSpan = outTimes.Count > 1 ? outTimes[^1] - outTimes[0] : 0;
                W($"output PTS span={outSpan / 10000.0:F0}ms (expected ~{inSpan / 10000.0:F0}ms)");
                bool preserved = outTimes.Count >= submitted - 1 && inSpan > 0 && Math.Abs(outSpan - inSpan) < inSpan * 0.15;
                W(preserved ? "RESULT: PASS - encoder preserves variable PTS (encode-on-change is safe)"
                            : "RESULT: FAIL - encoder did NOT preserve variable PTS (encode-on-change would distort timing)");
            }
        }
        foreach (var t in nv12) { try { t.Dispose(); } catch { } }
    }

    // Step 1+2 isolation: real capture -> NV12 -> hardware H.264 encoder MFT (direct), count samples/keyframes.
    private static void RunEncoderMft(Action<string> W)
    {
        const int Fps = 60, Pool = 12;
        using var gpu = new GpuRecordingDevice();
        IntPtr hmon = WgcCapture.MonitorFromPoint(0, 0);
        using var cap = new GpuWgcCapture(gpu, hmon, captureCursor: true);
        if (!cap.WaitForFirstFrame(5000, out int w, out int h)) { W("RESULT: FAIL - no frame"); return; }
        using var conv = new VideoProcessorConverter(gpu, w, h, Fps);

        long totalBytes = 0;
        var keyTimes = new System.Collections.Generic.List<long>();
        var firstFrames = new System.Collections.Generic.List<string>();
        using var enc = new MfH264EncoderMft(gpu, conv.Width, conv.Height, Fps, 16_000_000, s =>
        {
            System.Threading.Interlocked.Add(ref totalBytes, s.Length);
            lock (keyTimes) { if (s.KeyFrame) keyTimes.Add(s.TimeNs); if (firstFrames.Count < 6) firstFrames.Add($"{(s.KeyFrame ? "KEY" : "   ")} t={s.TimeNs / 10000}ms {s.Length}B"); }
        });
        W($"encoder created: {conv.Width}x{conv.Height}@{Fps}");

        var input = new ID3D11Texture2D[Pool];
        var nv12 = new ID3D11Texture2D[Pool];
        for (int i = 0; i < Pool; i++) { input[i] = conv.CreateInputTexture(); nv12[i] = conv.CreateNv12Texture(); }

        var proc = System.Diagnostics.Process.GetCurrentProcess();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        long freq = System.Diagnostics.Stopwatch.Frequency;
        long frame = 0, submitted = 0, dropped = 0;
        var cpu0 = proc.TotalProcessorTime;

        while (clock.Elapsed.TotalSeconds < 10)
        {
            long deadline = frame * freq / Fps;
            long now = clock.ElapsedTicks;
            if (deadline > now) { int ms = (int)((deadline - now) * 1000 / freq); if (ms > 0) System.Threading.Thread.Sleep(ms); }

            int slot = (int)(frame % Pool);
            if (cap.CopyLatestInto(input[slot]))
            {
                conv.Convert(input[slot], nv12[slot]);
                gpu.Context.Flush();
                if (enc.TryConsumeNeedInput())
                {
                    long ts = clock.Elapsed.Ticks;
                    enc.SubmitFrame(nv12[slot], ts, 10_000_000L / Fps);
                    submitted++;
                }
                else { dropped++; }
            }
            frame++;
        }

        double cpuMs = (proc.TotalProcessorTime - cpu0).TotalMilliseconds;
        System.Threading.Thread.Sleep(300); // let in-flight encodes arrive (Dispose drains on scope exit)

        foreach (var f in firstFrames) W("  " + f);
        // keyframe interval stats
        string kf = "n/a";
        lock (keyTimes)
        {
            if (keyTimes.Count >= 2)
            {
                long span = keyTimes[^1] - keyTimes[0];
                kf = $"{keyTimes.Count} keyframes over {span / 10000}ms (~every {span / 10000 / (keyTimes.Count - 1)}ms)";
            }
            else kf = $"{keyTimes.Count} keyframes seen";
        }
        W($"submitted={submitted} dropped={dropped} samplesOut={enc.SamplesOut} keyOut={enc.KeyFramesOut} bytes={totalBytes:N0}");
        W($"keyframes: {kf}");
        W($"CPU: {cpuMs / 10000.0:F1}% of 1 core ({cpuMs / 10000.0 / Environment.ProcessorCount:F2}% of {Environment.ProcessorCount} cores)");

        for (int i = 0; i < Pool; i++) { try { input[i].Dispose(); } catch { } try { nv12[i].Dispose(); } catch { } }
        W(enc.SamplesOut > 0 && enc.KeyFramesOut > 0 ? "RESULT: PASS" : "RESULT: FAIL - no encoded samples/keyframes");
    }

    // --- read-back + PNG helpers (TEST ONLY; the real pipeline never reads back) ---

    private static unsafe void DumpBgra(GpuRecordingDevice gpu, ID3D11Texture2D src, int w, int h, string path)
    {
        using var staging = gpu.Device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm, SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging, BindFlags = BindFlags.None, CPUAccessFlags = CpuAccessFlags.Read,
        });
        gpu.Context.CopyResource(staging, src);
        var map = gpu.Context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            int stride = w * 4;
            var buf = new byte[stride * h];
            byte* p = (byte*)map.DataPointer;
            for (int y = 0; y < h; y++)
                System.Runtime.InteropServices.Marshal.Copy((IntPtr)(p + y * map.RowPitch), buf, y * stride, stride);
            SavePng(buf, w, h, path);
        }
        finally { gpu.Context.Unmap(staging, 0); }
    }

    private static unsafe void DumpNv12(GpuRecordingDevice gpu, ID3D11Texture2D src, int w, int h, string path)
    {
        using var staging = gpu.Device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1,
            Format = Format.NV12, SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging, BindFlags = BindFlags.None, CPUAccessFlags = CpuAccessFlags.Read,
        });
        gpu.Context.CopyResource(staging, src);
        var map = gpu.Context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            int pitch = (int)map.RowPitch;
            byte* baseP = (byte*)map.DataPointer;
            byte* yPlane = baseP;
            byte* uvPlane = baseP + pitch * h; // NV12: UV plane follows the Y plane
            var bgra = new byte[w * 4 * h];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int Y = yPlane[y * pitch + x];
                    int uvRow = (y >> 1) * pitch;
                    int uvCol = (x >> 1) * 2;
                    int U = uvPlane[uvRow + uvCol];
                    int V = uvPlane[uvRow + uvCol + 1];
                    // BT.709 limited-range YCbCr -> RGB
                    double c = Y - 16, d = U - 128, e = V - 128;
                    int r = Clamp(1.164 * c + 1.793 * e);
                    int g = Clamp(1.164 * c - 0.213 * d - 0.533 * e);
                    int b = Clamp(1.164 * c + 2.112 * d);
                    int o = (y * w + x) * 4;
                    bgra[o] = (byte)b; bgra[o + 1] = (byte)g; bgra[o + 2] = (byte)r; bgra[o + 3] = 255;
                }
            }
            SavePng(bgra, w, h, path);
        }
        finally { gpu.Context.Unmap(staging, 0); }
    }

    private static int Clamp(double v) => v < 0 ? 0 : (v > 255 ? 255 : (int)(v + 0.5));

    private static void SavePng(byte[] bgra, int w, int h, string path)
    {
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, bgra, w * 4);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        enc.Save(fs);
    }
}
