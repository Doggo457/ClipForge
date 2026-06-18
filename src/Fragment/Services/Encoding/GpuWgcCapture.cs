using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Fragment.Services; // reuse WgcCapture's interop interfaces + MonitorFromPoint
using Vortice.Direct3D11;
using Vortice.DXGI;
using WinRT;
using Windows.Foundation.Metadata;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

namespace Fragment.Services.Encoding;

/// <summary>
/// Windows.Graphics.Capture that keeps each frame ON THE GPU. Frames are <b>pulled</b> by the caller
/// (the FrameFeeder thread) via <see cref="CopyLatestInto"/> rather than pushed on WGC's free-threaded
/// FrameArrived event — so every D3D context operation (capture copy, convert, encode) happens on the
/// single feeder thread, avoiding cross-thread contention with the encoder MFT over the shared device.
/// The captured BGRA surface is copied GPU→GPU into an app-owned "latest" texture (no readback).
/// </summary>
public sealed class GpuWgcCapture : IDisposable
{
    private static readonly Guid GraphicsCaptureItemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid ID3D11Texture2DIid = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");

    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly IDirect3DDevice _winrtDevice;
    private GraphicsCaptureItem? _item;
    private readonly Direct3D11CaptureFramePool _framePool;
    private readonly GraphicsCaptureSession _session;

    private ID3D11Texture2D? _latest; // app-owned BGRA copy of the most recent frame (on the GPU)
    private int _w, _h;
    private bool _hasFrame;
    private volatile bool _disposed;
    private int _disposeGuard;
    private long _arrivedCount;

    public long ArrivedCount => Interlocked.Read(ref _arrivedCount);

    public GpuWgcCapture(GpuRecordingDevice gpu, IntPtr hmon, bool captureCursor)
    {
        _device = gpu.Device;
        _context = gpu.Context;
        _winrtDevice = gpu.WinRtDevice;

        var interop = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();
        Guid iid = GraphicsCaptureItemIid;
        IntPtr itemAbi = interop.CreateForMonitor(hmon, ref iid);
        _item = MarshalInspectable<GraphicsCaptureItem>.FromAbi(itemAbi);
        Marshal.Release(itemAbi);

        var size = _item.Size;
        _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            _winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 3, size);
        _session = _framePool.CreateCaptureSession(_item);

        try
        {
            if (ApiInformation.IsApiContractPresent("Windows.Foundation.UniversalApiContract", 12) &&
                ApiInformation.IsPropertyPresent("Windows.Graphics.Capture.GraphicsCaptureSession", "IsBorderRequired"))
                _session.IsBorderRequired = false;
        }
        catch { /* Win10: border unavoidable */ }
        try
        {
            if (ApiInformation.IsPropertyPresent("Windows.Graphics.Capture.GraphicsCaptureSession", "IsCursorCaptureEnabled"))
                _session.IsCursorCaptureEnabled = captureCursor;
        }
        catch { }

        _session.StartCapture();
    }

    /// <summary>Polls the capture pool (on the calling thread) until the first frame lands.</summary>
    public bool WaitForFirstFrame(int timeoutMs, out int width, out int height)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (PullLatest()) { width = _w; height = _h; return true; }
            Thread.Sleep(8);
        }
        width = height = 0;
        return false;
    }

    /// <summary>
    /// Refreshes from the capture pool then copies the most recent frame into <paramref name="dest"/>
    /// (caller-owned BGRA texture on the same device). Call on the feeder thread only. If no new frame
    /// has arrived since last time, the previous frame is re-copied (frame-repeat → smooth cadence).
    /// Returns false before the very first frame.
    /// </summary>
    public bool CopyLatestInto(ID3D11Texture2D dest)
    {
        PullLatest();
        if (!_hasFrame || _latest is null) return false;
        _context.CopyResource(dest, _latest);
        return true;
    }

    /// <summary>Drains the pool to the newest queued frame and copies it into <see cref="_latest"/>.</summary>
    private bool PullLatest()
    {
        if (_disposed) return false;
        Direct3D11CaptureFrame? latest = null, f;
        while ((f = _framePool.TryGetNextFrame()) != null) { latest?.Dispose(); latest = f; }
        if (latest is null) return false;

        using (latest)
        using (var src = GetTexture(latest.Surface))
        {
            var desc = src.Description;
            int w = (int)desc.Width, h = (int)desc.Height;
            if (_latest is null || _w != w || _h != h)
            {
                _latest?.Dispose();
                _latest = _device.CreateTexture2D(new Texture2DDescription
                {
                    Width = (uint)w,
                    Height = (uint)h,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = Format.B8G8R8A8_UNorm,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Default,
                    // RenderTarget (not ShaderResource): a ShaderResource-only texture is rejected as a
                    // VideoProcessor input view by some drivers (AMD).
                    BindFlags = BindFlags.RenderTarget,
                    CPUAccessFlags = CpuAccessFlags.None,
                    MiscFlags = ResourceOptionFlags.None,
                });
                _w = w; _h = h;
            }
            _context.CopyResource(_latest, src);
            _hasFrame = true;
        }
        Interlocked.Increment(ref _arrivedCount);
        return true;
    }

    private ID3D11Texture2D GetTexture(IDirect3DSurface surface)
    {
        var access = surface.As<IDirect3DDxgiInterfaceAccess>();
        Guid iid = ID3D11Texture2DIid;
        IntPtr tex = access.GetInterface(ref iid);
        return new ID3D11Texture2D(tex);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeGuard, 1) != 0) return;
        _disposed = true;

        try { _session?.Dispose(); } catch { }   // retires the Win10 border
        try { _framePool?.Dispose(); } catch { }
        try { _latest?.Dispose(); } catch { } _latest = null;
        _item = null; // release the WGC item so its registration can be GC-finalized (border-restart fix)
    }
}
