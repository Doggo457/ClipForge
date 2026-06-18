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
            if (mode == "3") RunRecord(W);
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
