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
            if (mode == "6") RunMicDsp(W);
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
        IntPtr hmon = WgcCapture.MonitorFromPoint(0, 0); // primary
        string outPath = Path.Combine(Dir, "gpu_record.mp4");
        try { File.Delete(outPath); } catch { }

        using (var rec = new GpuVideoRecorder(gpu, hmon, outPath, 60, 16_000_000, captureCursor: true, audio: Fragment.Models.AudioMode.SystemOnly, diag: W))
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
            System.Threading.Interlocked.Add(ref totalBytes, s.Data.Length);
            lock (keyTimes) { if (s.KeyFrame) keyTimes.Add(s.TimeNs); if (firstFrames.Count < 6) firstFrames.Add($"{(s.KeyFrame ? "KEY" : "   ")} t={s.TimeNs / 10000}ms {s.Data.Length}B"); }
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
