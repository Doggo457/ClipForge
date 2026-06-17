using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Threading;
using WinRT;
using Windows.Foundation.Metadata;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Fragment.Services;

/// <summary>
/// Screen capture via Windows.Graphics.Capture (WGC) — the same modern API OBS uses. Unlike
/// gdigrab (GDI) and ddagrab (DXGI Desktop Duplication), WGC captures hardware-overlay / MPO /
/// flip-model content, which on many machines (AMD GPUs, VR/streaming overlays running) bypasses
/// the composed desktop and starves the older capture APIs to a few unique frames per second.
///
/// The captured BGRA frames are pulled to system memory; <see cref="WgcFramePump"/> then writes
/// them to an ffmpeg "-f rawvideo -i -" stdin at a fixed rate for encoding.
/// </summary>
public sealed class WgcCapture : IDisposable
{
    public static bool IsSupported
    {
        get { try { return GraphicsCaptureSession.IsSupported(); } catch { return false; } }
    }

    /// <summary>HMONITOR for the monitor whose bounds contain the given virtual-desktop point.</summary>
    public static IntPtr MonitorFromPoint(int x, int y)
        => MonitorFromPointNative(new POINT { X = x, Y = y }, MONITOR_DEFAULTTOPRIMARY);

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [DllImport("user32.dll", EntryPoint = "MonitorFromPoint")]
    private static extern IntPtr MonitorFromPointNative(POINT pt, uint dwFlags);
    private const uint MONITOR_DEFAULTTOPRIMARY = 1;

    private static readonly Guid GraphicsCaptureItemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid ID3D11Texture2DIid = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");

    [DllImport("d3d11.dll", EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice", PreserveSig = true)]
    private static extern uint CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly IDirect3DDevice _winrtDevice;
    private GraphicsCaptureItem? _item; // nulled on Dispose so its WinRT registration can be GC-released
    private readonly Direct3D11CaptureFramePool _framePool;
    private readonly GraphicsCaptureSession _session;
    private readonly object _gate = new();         // guards the triple-buffer index swaps
    private readonly object _captureLock = new();   // serializes OnFrameArrived and gates teardown

    private int _disposeGuard;                      // Interlocked: ensures Dispose runs its body once

    private ID3D11Texture2D? _staging;
    private SizeInt32 _lastSize;

    // Tear-free, allocation-free triple buffer. Three reused buffers are addressed by three indices
    // that are always a permutation of {0,1,2}: the capture thread fills buffer[_wi] OUTSIDE the lock,
    // then publishes by swapping _wi<->_yi under the lock (a pointer-index swap only); the pump claims
    // the newest frame by swapping _ri<->_yi under the lock, then sends buffer[_ri] OUTSIDE the lock.
    // Because _wi != _ri always, the two threads never touch the same buffer (no torn frames) and the
    // lock is only ever held for a swap. Reusing buffers removes the ~0.5 GB/s Large Object Heap churn
    // that per-frame allocation caused, which had been triggering gen2 GC pauses that froze the capture
    // thread and dropped unique frames (the source of the choppiness vs the no-audio prototype).
    private byte[][]? _buf;      // 3 frame buffers, (re)allocated only on size change
    private int _wi, _ri = 1, _yi = 2; // write / read / ready indices (always distinct)
    private bool _hasNew;       // a fresh frame sits in buffer[_yi]
    private int _frameLen;
    private int _w, _h;
    private bool _hasFrame;
    private volatile bool _disposed;

    // Diagnostics: number of frames WGC has actually delivered (published). Read per second by the
    // pump's diagnostic logger to tell capture-side loss (GC stalls) from downstream loss.
    private long _arrivedCount;
    public long ArrivedCount => Interlocked.Read(ref _arrivedCount);

    public WgcCapture(IntPtr hmon, bool captureCursor)
    {
        D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
            new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 },
            out _device, out _context).CheckError();

        using (var dxgi = _device.QueryInterface<IDXGIDevice>())
        {
            uint hr = CreateDirect3D11DeviceFromDXGIDevice(dxgi.NativePointer, out IntPtr abi);
            if (hr != 0) Marshal.ThrowExceptionForHR((int)hr);
            _winrtDevice = MarshalInterface<IDirect3DDevice>.FromAbi(abi);
            Marshal.Release(abi);
        }

        var interop = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();
        Guid iid = GraphicsCaptureItemIid;
        IntPtr itemAbi = interop.CreateForMonitor(hmon, ref iid);
        _item = MarshalInspectable<GraphicsCaptureItem>.FromAbi(itemAbi);
        Marshal.Release(itemAbi);

        _lastSize = _item.Size;
        // 5 buffers (not 2): gives the free-threaded FrameArrived callback headroom so a brief overrun
        // (a GC pause, an encoder spike) doesn't make WGC drop the next frame. Recommended 3-6.
        _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            _winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 5, _lastSize);
        _session = _framePool.CreateCaptureSession(_item);

        // Hide the yellow capture border. This only exists on build 20348+ (Windows 11); on Windows 10
        // the API isn't implemented and calling it throws E_NOINTERFACE, so gate it carefully.
        try
        {
            if (ApiInformation.IsApiContractPresent("Windows.Foundation.UniversalApiContract", 12) &&
                ApiInformation.IsPropertyPresent("Windows.Graphics.Capture.GraphicsCaptureSession", "IsBorderRequired"))
            {
                _session.IsBorderRequired = false;
            }
        }
        catch { /* not available on this build — the border is unavoidable on Windows 10 */ }

        // Cursor toggle (build 19041+ — present on the supported floor).
        try
        {
            if (ApiInformation.IsPropertyPresent("Windows.Graphics.Capture.GraphicsCaptureSession", "IsCursorCaptureEnabled"))
                _session.IsCursorCaptureEnabled = captureCursor;
        }
        catch { }

        _framePool.FrameArrived += OnFrameArrived;
        _session.StartCapture();
    }

    /// <summary>Blocks up to <paramref name="timeoutMs"/> for the first frame; returns its size.</summary>
    public bool WaitForFirstFrame(int timeoutMs, out int width, out int height)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            lock (_gate) { if (_hasFrame) { width = _w; height = _h; return true; } }
            Thread.Sleep(8);
        }
        width = height = 0;
        return false;
    }

    /// <summary>
    /// Returns the most recent BGRA frame, repeating the last one when nothing new has arrived (exactly
    /// what an even N-fps timeline needs). Claims the newest buffer by swapping read/ready indices under
    /// the lock; the returned buffer is owned by the pump until its next call, so the capture never
    /// overwrites it (tear-free). The lock is only held for the swap. Returns null before the first
    /// frame. When the returned reference equals the previous call's, it's a repeat (capture produced
    /// nothing new since last tick).
    /// </summary>
    public byte[]? AcquireLatest(out int length)
    {
        lock (_gate)
        {
            if (!_hasFrame || _buf == null) { length = 0; return null; }
            if (_hasNew)
            {
                (_ri, _yi) = (_yi, _ri);
                _hasNew = false;
            }
            length = _frameLen;
            return _buf[_ri];
        }
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        // _captureLock serializes callbacks (the free-threaded pool may overlap deliveries, and the
        // shared _staging/_context are not reentrant) AND coordinates with Dispose: Dispose takes this
        // same lock before freeing the D3D objects, so a callback can never run CopyResource/Map on a
        // disposed context. The pump uses the separate _gate, so it is never blocked by this lock.
        lock (_captureLock)
        {
            if (_disposed) return;
            try
            {
                using var frame = sender.TryGetNextFrame();
                if (frame is null) return;

                using var src = GetTexture(frame.Surface);
                var desc = src.Description;

                if (_staging is null || _staging.Description.Width != desc.Width || _staging.Description.Height != desc.Height)
                {
                    _staging?.Dispose();
                    _staging = _device.CreateTexture2D(new Texture2DDescription
                    {
                        Width = desc.Width,
                        Height = desc.Height,
                        MipLevels = 1,
                        ArraySize = 1,
                        Format = Format.B8G8R8A8_UNorm,
                        SampleDescription = new SampleDescription(1, 0),
                        Usage = ResourceUsage.Staging,
                        BindFlags = BindFlags.None,
                        CPUAccessFlags = CpuAccessFlags.Read,
                        MiscFlags = ResourceOptionFlags.None,
                    });
                }

                _context.CopyResource(_staging, src);
                var map = _context.Map(_staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                try
                {
                    int w = (int)desc.Width, h = (int)desc.Height, tight = w * 4, total = tight * h;

                    // Select (and, on size change, allocate) the write buffer under _gate so the buffer
                    // reference and the _wi index are always a consistent pair. The expensive copy below
                    // then happens outside _gate so the pump's AcquireLatest is never blocked by it.
                    byte[] dst;
                    lock (_gate)
                    {
                        if (_buf is null || _buf[0].Length != total)
                        {
                            _buf = new[] { new byte[total], new byte[total], new byte[total] };
                            _wi = 0; _ri = 1; _yi = 2; _hasNew = false;
                        }
                        dst = _buf[_wi];
                    }

                    unsafe
                    {
                        byte* p = (byte*)map.DataPointer;
                        for (int y = 0; y < h; y++)
                            Marshal.Copy((IntPtr)(p + y * map.RowPitch), dst, y * tight, tight);
                    }

                    // Publish: index swap only (write becomes ready). No data copy under the lock.
                    lock (_gate)
                    {
                        (_wi, _yi) = (_yi, _wi);
                        _hasNew = true;
                        _frameLen = total;
                        _w = w; _h = h; _hasFrame = true;
                    }
                    Interlocked.Increment(ref _arrivedCount);
                }
                finally { _context.Unmap(_staging, 0); }

                if (frame.ContentSize.Width != _lastSize.Width || frame.ContentSize.Height != _lastSize.Height)
                {
                    _lastSize = frame.ContentSize;
                    try { sender.Recreate(_winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 5, _lastSize); } catch { }
                }
            }
            catch
            {
                // Defensive: swallow any stray teardown race rather than crash the pool thread.
            }
        }
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
        // Idempotent: this capture is reachable from BOTH the explicit Stop/StopAsync path and the
        // process-Exited handler, so Dispose is normally called twice. Run the teardown exactly once.
        if (Interlocked.Exchange(ref _disposeGuard, 1) != 0) return;

        // Detach the free-threaded handler first, then take _captureLock so any in-flight callback has
        // finished (and no new one will start) before we free the D3D objects it uses — no use-after-
        // free. Disposing the session (GraphicsCaptureSession.Close) is what retires the Win10 border.
        try { _framePool.FrameArrived -= OnFrameArrived; } catch { }

        lock (_captureLock)
        {
            _disposed = true;
            try { _session?.Dispose(); } catch { }   // retires the capture border
            try { _framePool?.Dispose(); } catch { }
            try { _winrtDevice?.Dispose(); } catch { } // release the WinRT D3D device wrapper
            // GraphicsCaptureItem isn't IDisposable; drop our reference so its WinRT capture
            // registration can be finalized. Leaving it referenced across a stop/start cycle is what
            // makes Windows 10 fail to retire the yellow border on the SECOND session's close.
            _item = null;
            try { _staging?.Dispose(); } catch { }
            try { _context?.Dispose(); } catch { }
            try { _device?.Dispose(); } catch { }
        }
    }
}

[ComImport, System.Runtime.InteropServices.Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IGraphicsCaptureItemInterop
{
    IntPtr CreateForWindow([In] IntPtr window, [In] ref Guid iid);
    IntPtr CreateForMonitor([In] IntPtr monitor, [In] ref Guid iid);
}

[ComImport, System.Runtime.InteropServices.Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDirect3DDxgiInterfaceAccess
{
    IntPtr GetInterface([In] ref Guid iid);
}

/// <summary>
/// Streams <see cref="WgcCapture"/> frames to ffmpeg over a named pipe. Crucially it DECOUPLES
/// sampling from writing: a sampler thread snapshots the latest captured frame into a small ring at an
/// even N-fps cadence (and is never blocked by ffmpeg), while a writer thread drains the ring to the
/// pipe (and absorbs ffmpeg's back-pressure). This is what makes recordings smooth: ffmpeg accepts
/// video unevenly when it is also muxing live audio, and the old single-threaded pump sampled at that
/// uneven cadence, so frames carried uneven timing (visible as "moves, stops, moves"). Sampling on its
/// own clock fixes the timing regardless of how jittery ffmpeg's consumption is. A pipe (not stdin) is
/// used so stdin stays free for the graceful 'q' stop while the live audio inputs are still open.
/// </summary>
public sealed class WgcFramePump : IDisposable
{
    private const int RingSize = 12; // ~200ms of slack to absorb ffmpeg/audio jitter without blocking the sampler

    // Per-second capture diagnostics are OFF by default (they append to a log file forever). Set the
    // environment variable FRAGMENT_DIAG=1 to enable them for troubleshooting.
    private static readonly bool DiagEnabled =
        Environment.GetEnvironmentVariable("FRAGMENT_DIAG") == "1";

    private readonly string _pipeName;
    private NamedPipeServerStream? _server;
    private readonly WgcCapture _capture;
    private readonly int _fps;
    private readonly Thread _sampler;
    private readonly Thread _writer;
    private volatile bool _running = true;
    private volatile bool _connected;

    // SPSC ring of reused frame buffers. The sampler writes _ring[_head], the writer reads _ring[_tail];
    // while not full _head != _tail, so the two threads never touch the same buffer and only the indices
    // are guarded by _qlock (the 8MB copy and the blocking pipe write both happen OUTSIDE the lock).
    private readonly object _qlock = new();
    private byte[][]? _ring;
    private int _ringLen, _head, _tail, _count;

    // Diagnostics (per-second, to a log file, no on-screen output).
    private long _writes;
    private double _blockedMs;

    public WgcFramePump(WgcCapture capture, int fps, int width, int height)
    {
        _capture = capture;
        _fps = fps > 0 ? fps : 60;
        _ = width; _ = height; // frame size is owned by the capture's triple buffer
        _pipeName = "fragment_vid_" + Guid.NewGuid().ToString("N");
        _server = new NamedPipeServerStream(
            _pipeName, PipeDirection.Out, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous,
            1 << 24, 1 << 24); // 16MB OS pipe buffer
        _server.BeginWaitForConnection(OnConnected, null);

        _sampler = new Thread(SampleLoop) { IsBackground = true, Name = "WgcSampler" };
        _writer = new Thread(WriteLoop) { IsBackground = true, Name = "WgcWriter" };
        _sampler.Start();
        _writer.Start();
    }

    /// <summary>The path ffmpeg reads the raw frames from, e.g. <c>\\.\pipe\fragment_vid_xxxx</c>.</summary>
    public string FfmpegInputPath => $@"\\.\pipe\{_pipeName}";

    private void OnConnected(IAsyncResult ar)
    {
        try { _server?.EndWaitForConnection(ar); _connected = true; }
        catch { /* disposed before ffmpeg connected */ }
    }

    // Samples the latest captured frame at an even fps cadence into the ring. Never blocks on ffmpeg.
    private void SampleLoop()
    {
        while (_running && !_connected) Thread.Sleep(2);
        if (!_running) return;

        var clock = Stopwatch.StartNew();
        long i = 0;

        long lastArrived = _capture.ArrivedCount;
        int lastGen2 = GC.CollectionCount(2), lastGen0 = GC.CollectionCount(0);
        long secSamples = 0, secSkips = 0;
        double nextLogMs = 1000;

        while (_running)
        {
            double due = i * (1000.0 / _fps);
            double now = clock.Elapsed.TotalMilliseconds;
            if (due > now) Thread.Sleep((int)Math.Min(16, due - now));
            i++;

            byte[]? frame = _capture.AcquireLatest(out int len);
            if (frame != null)
            {
                int slot = -1;
                lock (_qlock)
                {
                    if (_ring is null || _ringLen != len)
                    {
                        _ring = new byte[RingSize][];
                        for (int k = 0; k < RingSize; k++) _ring[k] = new byte[len];
                        _ringLen = len; _head = _tail = _count = 0;
                    }
                    if (_count < RingSize) slot = _head; // else: ring full (ffmpeg behind) -> skip this sample
                }

                if (slot >= 0)
                {
                    Buffer.BlockCopy(frame, 0, _ring![slot], 0, len);     // snapshot OUTSIDE the lock
                    lock (_qlock) { _head = (_head + 1) % RingSize; _count++; Monitor.Pulse(_qlock); }
                    secSamples++;
                }
                else
                {
                    secSkips++;
                }
            }

            if (DiagEnabled && clock.Elapsed.TotalMilliseconds >= nextLogMs)
            {
                long arrivedNow = _capture.ArrivedCount;
                int g2 = GC.CollectionCount(2), g0 = GC.CollectionCount(0);
                long w; double b;
                lock (_qlock) { w = _writes; _writes = 0; b = _blockedMs; _blockedMs = 0; }
                DiagLog($"capture={arrivedNow - lastArrived} samples={secSamples} skips={secSkips} " +
                        $"writes={w} blockedMs={b:F0} gen2={g2 - lastGen2} gen0={g0 - lastGen0}");
                lastArrived = arrivedNow; lastGen2 = g2; lastGen0 = g0;
                secSamples = 0; secSkips = 0; nextLogMs += 1000;
            }
        }

        lock (_qlock) { Monitor.PulseAll(_qlock); } // wake the writer to exit
    }

    // Drains the ring to the pipe. Blocks on the write (ffmpeg back-pressure) without affecting sampling.
    private void WriteLoop()
    {
        var sw = new Stopwatch();
        while (true)
        {
            byte[][] ring; int slot, len;
            lock (_qlock)
            {
                while (_running && _count == 0) Monitor.Wait(_qlock);
                if (_count == 0) break; // stopped and drained
                ring = _ring!; slot = _tail; len = _ringLen;
            }

            sw.Restart();
            try { _server!.Write(ring[slot], 0, len); }
            catch { break; } // ffmpeg closed the pipe
            sw.Stop();

            lock (_qlock)
            {
                // If the ring was reallocated (resolution change) while we wrote, its indices were reset;
                // don't advance the new tail in that case.
                if (ReferenceEquals(ring, _ring)) { _tail = (slot + 1) % RingSize; _count--; }
                _writes++; _blockedMs += sw.Elapsed.TotalMilliseconds;
            }
        }
    }

    // Appends one diagnostic line per second to %TEMP%\Fragment\diag.log. Best-effort; never throws.
    private void DiagLog(string msg)
    {
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "Fragment");
            Directory.CreateDirectory(dir);
            string sid = _pipeName.Length >= 6 ? _pipeName.Substring(_pipeName.Length - 6) : _pipeName;
            File.AppendAllText(Path.Combine(dir, "diag.log"),
                $"{DateTime.Now:HH:mm:ss} sid={sid} {msg}{Environment.NewLine}");
        }
        catch { }
    }

    public void Stop()
    {
        _running = false;
        lock (_qlock) { Monitor.PulseAll(_qlock); } // unblock the writer's wait
        try { _sampler.Join(1000); } catch { }
        try { _writer.Join(1000); } catch { }
        try { _server?.Dispose(); } catch { }
        _server = null;
    }

    public void Dispose() => Stop();
}
