using System;
using Vortice.Direct3D11;
using Vortice.MediaFoundation;

namespace Fragment.Services.Encoding;

/// <summary>
/// In-process H.264 encoder + MP4 muxer built on Media Foundation's sink writer. The sink writer is
/// handed our shared D3D11 device (via the DXGI device manager) so the hardware H.264 MFT consumes the
/// NV12 textures straight off the GPU — no CPU readback, near-zero CPU. Throttling is disabled because
/// the <see cref="GpuVideoRecorder"/> paces frames itself (the smooth-cadence FrameFeeder).
/// </summary>
public sealed class MfH264SinkWriter : IDisposable
{
    // IID_ID3D11Texture2D — MFCreateDXGISurfaceBuffer needs the riid of the surface we wrap.
    private static readonly Guid ID3D11Texture2DIid = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");

    private const uint MFVideoInterlace_Progressive = 2;
    private const uint eAVEncH264VProfile_High = 100;

    private readonly IMFSinkWriter _writer;
    private readonly int _stream;
    private bool _finalized;

    public MfH264SinkWriter(GpuRecordingDevice gpu, string path, int width, int height, int fps, int bitrate)
    {
        using var attrs = MediaFactory.MFCreateAttributes(4);
        attrs.Set(SinkWriterAttributeKeys.D3DManager, gpu.DeviceManager);        // share our device → HW encoder, zero-copy
        attrs.Set(SinkWriterAttributeKeys.ReadwriteEnableHardwareTransforms, 1u); // prefer the hardware H.264 MFT
        // NOTE: throttling left ENABLED. It applies natural back-pressure so encoded samples can't pile up
        // unbounded (which wedges the shared-device encoder). A HW encoder keeps up at 1080p60, so in
        // practice WriteSample doesn't block; the FrameFeeder's cadence stays smooth.

        _writer = MediaFactory.MFCreateSinkWriterFromURL(path, null, attrs);

        // Output: H.264 (the .mp4 URL selects the MPEG-4 file sink + muxer).
        using var outType = MediaFactory.MFCreateMediaType();
        outType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        outType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264);
        outType.Set(MediaTypeAttributeKeys.AvgBitrate, (uint)bitrate);
        outType.Set(MediaTypeAttributeKeys.InterlaceMode, MFVideoInterlace_Progressive);
        outType.Set(MediaTypeAttributeKeys.Mpeg2Profile, eAVEncH264VProfile_High);
        SetRatio(outType, MediaTypeAttributeKeys.FrameSize, width, height);
        SetRatio(outType, MediaTypeAttributeKeys.FrameRate, fps, 1);
        SetRatio(outType, MediaTypeAttributeKeys.PixelAspectRatio, 1, 1);
        _stream = _writer.AddStream(outType);

        // Input: the NV12 textures the converter produces.
        using var inType = MediaFactory.MFCreateMediaType();
        inType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        inType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.NV12);
        inType.Set(MediaTypeAttributeKeys.InterlaceMode, MFVideoInterlace_Progressive);
        SetRatio(inType, MediaTypeAttributeKeys.FrameSize, width, height);
        SetRatio(inType, MediaTypeAttributeKeys.FrameRate, fps, 1);
        SetRatio(inType, MediaTypeAttributeKeys.PixelAspectRatio, 1, 1);
        _writer.SetInputMediaType(_stream, inType, null);

        _writer.BeginWriting();
    }

    // MF packs paired UINT32s (size / frame-rate / aspect-ratio) into one UINT64: (hi << 32) | lo.
    private static void SetRatio(IMFMediaType t, Guid key, int hi, int lo)
        => t.Set(key, ((ulong)(uint)hi << 32) | (uint)lo);

    /// <summary>Queues one NV12 frame (GPU texture, zero-copy) to the encoder. Times are in 100-ns units.</summary>
    public void WriteFrame(ID3D11Texture2D nv12, long sampleTime100ns, long duration100ns)
    {
        var buffer = MediaFactory.MFCreateDXGISurfaceBuffer(ID3D11Texture2DIid, nv12, 0, false);
        try { using var b2d = buffer.QueryInterface<IMF2DBuffer>(); buffer.CurrentLength = b2d.ContiguousLength; }
        catch { /* some buffers report length already */ }

        using var sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(buffer);
        sample.SampleTime = sampleTime100ns;
        sample.SampleDuration = duration100ns;
        _writer.WriteSample(_stream, sample);

        buffer.Dispose(); // the MFT keeps its own ref while encoding; our wrapper is done
    }

    /// <summary>Flushes and finalizes the MP4 (writes the moov box). Safe to call once.</summary>
    public void Stop()
    {
        if (_finalized) return;
        _finalized = true;
        try { _writer.Finalize(); } catch { }
    }

    public void Dispose()
    {
        Stop();
        try { _writer.Dispose(); } catch { }
    }
}
