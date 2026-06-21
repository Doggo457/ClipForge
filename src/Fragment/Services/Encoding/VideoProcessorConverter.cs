using System;
using System.Collections.Generic;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Fragment.Services.Encoding;

/// <summary>
/// Converts a captured BGRA texture to NV12 entirely on the GPU using the fixed-function D3D11 Video
/// Processor (near-zero CPU). The NV12 output stays on the GPU to be fed straight to the H.264 encoder.
/// </summary>
public sealed class VideoProcessorConverter : IDisposable
{
    private readonly ID3D11Device _device;
    private readonly ID3D11VideoDevice _videoDevice;
    private readonly ID3D11VideoContext _videoContext;
    private ID3D11VideoProcessor _processor;            // rebuilt if the input size changes (window resize / switch)
    private ID3D11VideoProcessorEnumerator _enumerator;
    private readonly int _fps;
    private int _inW, _inH;                              // current configured INPUT size

    // Processor views are bound to a specific texture and are expensive to build, so cache one per texture
    // (keyed by its native COM pointer) instead of creating + destroying two COM objects on every frame.
    // The set of textures is tiny and fixed (one capture surface in, a small NV12 pool out), so this caps at
    // a handful of entries. Views hold a ref to their texture, so a cached entry keeps its texture alive.
    private readonly Dictionary<IntPtr, ID3D11VideoProcessorInputView> _inputViews = new();
    private readonly Dictionary<IntPtr, ID3D11VideoProcessorOutputView> _outputViews = new();
    private readonly VideoProcessorStream[] _streams = new VideoProcessorStream[1]; // reused; no per-frame array alloc

    public int Width { get; }
    public int Height { get; }

    public VideoProcessorConverter(GpuRecordingDevice gpu, int width, int height, int fps)
    {
        _device = gpu.Device;
        Width = width + (width & 1);    // NV12 needs even dimensions (2x2 chroma)
        Height = height + (height & 1);
        _fps = fps > 0 ? fps : 60;

        _videoDevice = _device.QueryInterface<ID3D11VideoDevice>();
        _videoContext = gpu.Context.QueryInterface<ID3D11VideoContext>();

        _enumerator = null!; _processor = null!;
        BuildProcessor(Width, Height); // default: input == output (monitor/full-screen fast path, no scaling)
    }

    // (Re)builds the Video Processor for a given INPUT size scaling to the fixed Width×Height OUTPUT. Window
    // capture calls this whenever the source window changes size (resize) or the followed window switches.
    private void BuildProcessor(int inW, int inH)
    {
        foreach (var v in _inputViews.Values) { try { v.Dispose(); } catch { } }
        foreach (var v in _outputViews.Values) { try { v.Dispose(); } catch { } }
        _inputViews.Clear(); _outputViews.Clear();           // views are bound to the old enumerator
        try { _processor?.Dispose(); } catch { }
        try { _enumerator?.Dispose(); } catch { }

        var rate = new Rational((uint)_fps, 1u);
        var content = new VideoProcessorContentDescription
        {
            InputFrameFormat = VideoFrameFormat.Progressive,
            InputWidth = (uint)inW,
            InputHeight = (uint)inH,
            OutputWidth = (uint)Width,
            OutputHeight = (uint)Height,
            InputFrameRate = rate,
            OutputFrameRate = rate,
            Usage = VideoUsage.PlaybackNormal,
        };
        _enumerator = _videoDevice.CreateVideoProcessorEnumerator(content);
        _processor = _videoDevice.CreateVideoProcessor(_enumerator, 0);
        _inW = inW; _inH = inH;

        // Input: full-range RGB desktop. Output: BT.709 limited-range YCbCr (the standard for H.264 HD).
        _videoContext.VideoProcessorSetStreamColorSpace(_processor, 0, new VideoProcessorColorSpace
        {
            Usage = 0, RGB_Range = 0 /* full */, YCbCr_Matrix = 1 /* BT.709 */, YCbCr_xvYCC = 0, Nominal_Range = 2 /* 0-255 */,
        });
        _videoContext.VideoProcessorSetOutputColorSpace(_processor, new VideoProcessorColorSpace
        {
            Usage = 0, RGB_Range = 0, YCbCr_Matrix = 1 /* BT.709 */, YCbCr_xvYCC = 0, Nominal_Range = 1 /* 16-235 */,
        });
    }

    /// <summary>
    /// Convert + scale a variable-size source (a captured window) into the fixed canvas, preserving aspect
    /// ratio with black letterbox/pillarbox bars. Rebuilds the processor when the source size changes. Used
    /// by window / active-window capture where the source can be any size or change at runtime.
    /// </summary>
    public void ConvertScaled(ID3D11Texture2D bgraSource, int srcW, int srcH, ID3D11Texture2D nv12Dest)
    {
        srcW = Math.Max(2, srcW & ~1); srcH = Math.Max(2, srcH & ~1);
        if (srcW != _inW || srcH != _inH)
        {
            BuildProcessor(srcW, srcH);
            // Fit the source into the canvas preserving aspect ratio; center it; fill the rest with black.
            double s = Math.Min((double)Width / srcW, (double)Height / srcH);
            int dw = ((int)(srcW * s)) & ~1, dh = ((int)(srcH * s)) & ~1;
            int dx = ((Width - dw) / 2) & ~1, dy = ((Height - dh) / 2) & ~1;
            // The Blt fills the whole output with the processor's background (default black) before compositing
            // the stream into the dest rect, so the letterbox/pillarbox bars come out black with no extra call.
            _videoContext.VideoProcessorSetStreamSourceRect(_processor, 0, true, new Vortice.RawRect(0, 0, srcW, srcH));
            _videoContext.VideoProcessorSetStreamDestRect(_processor, 0, true, new Vortice.RawRect(dx, dy, dx + dw, dy + dh));
        }
        var input = GetInputView(bgraSource);
        var output = GetOutputView(nv12Dest);
        _streams[0] = new VideoProcessorStream { Enable = true, OutputIndex = 0, InputFrameOrField = 0, InputSurface = input };
        _videoContext.VideoProcessorBlt(_processor, output, 0, 1, _streams);
    }

    /// <summary>
    /// Creates a BGRA texture sized to the converter, usable as the VideoProcessor input. Uses
    /// <see cref="BindFlags.RenderTarget"/>: a ShaderResource-only texture is rejected as a VP input
    /// view by some drivers (AMD), whereas a render-target-capable Default texture is accepted everywhere.
    /// </summary>
    public ID3D11Texture2D CreateInputTexture()
        => _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)Width,
            Height = (uint)Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None,
        });

    /// <summary>Creates an NV12 render-target texture sized to the output, usable as the Blt target and MF input.</summary>
    public ID3D11Texture2D CreateNv12Texture()
        => _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)Width,
            Height = (uint)Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.NV12,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget, // required to create a VideoProcessorOutputView
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None,
        });

    /// <summary>GPU BGRA→NV12 blit. Both textures live on the shared device; no CPU touches the pixels.</summary>
    public void Convert(ID3D11Texture2D bgraSource, ID3D11Texture2D nv12Dest)
    {
        var input = GetInputView(bgraSource);
        var output = GetOutputView(nv12Dest);
        _streams[0] = new VideoProcessorStream
        {
            Enable = true,
            OutputIndex = 0,
            InputFrameOrField = 0,
            InputSurface = input,
        };
        _videoContext.VideoProcessorBlt(_processor, output, 0, 1, _streams);
    }

    private ID3D11VideoProcessorInputView GetInputView(ID3D11Texture2D bgraSource)
    {
        IntPtr key = bgraSource.NativePointer;
        if (!_inputViews.TryGetValue(key, out var v))
        {
            v = _videoDevice.CreateVideoProcessorInputView(bgraSource, _enumerator,
                new VideoProcessorInputViewDescription
                {
                    FourCC = 0,
                    ViewDimension = VideoProcessorInputViewDimension.Texture2D,
                    Texture2D = new Texture2DVideoProcessorInputView { MipSlice = 0, ArraySlice = 0 },
                });
            _inputViews[key] = v;
        }
        return v;
    }

    private ID3D11VideoProcessorOutputView GetOutputView(ID3D11Texture2D nv12Dest)
    {
        IntPtr key = nv12Dest.NativePointer;
        if (!_outputViews.TryGetValue(key, out var v))
        {
            v = _videoDevice.CreateVideoProcessorOutputView(nv12Dest, _enumerator,
                new VideoProcessorOutputViewDescription
                {
                    ViewDimension = VideoProcessorOutputViewDimension.Texture2D,
                    Texture2D = new Texture2DVideoProcessorOutputView { MipSlice = 0 },
                });
            _outputViews[key] = v;
        }
        return v;
    }

    public void Dispose()
    {
        foreach (var v in _inputViews.Values) { try { v.Dispose(); } catch { } }
        foreach (var v in _outputViews.Values) { try { v.Dispose(); } catch { } }
        _inputViews.Clear(); _outputViews.Clear();
        try { _processor?.Dispose(); } catch { }
        try { _enumerator?.Dispose(); } catch { }
        try { _videoContext?.Dispose(); } catch { }
        try { _videoDevice?.Dispose(); } catch { }
    }
}
