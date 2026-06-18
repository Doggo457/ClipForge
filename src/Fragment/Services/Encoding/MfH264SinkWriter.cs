using System;
using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.MediaFoundation;

namespace Fragment.Services.Encoding;

/// <summary>
/// In-process H.264 + AAC encoder and MP4 muxer built on Media Foundation's sink writer. The sink writer
/// is handed our shared D3D11 device (via the DXGI device manager) so the hardware H.264 MFT consumes the
/// NV12 textures straight off the GPU — no CPU readback, near-zero CPU. Audio is an optional second stream
/// fed interleaved 16-bit PCM; the muxer interleaves both by sample timestamp for A/V sync.
/// </summary>
public sealed class MfH264SinkWriter : IDisposable
{
    // IID_ID3D11Texture2D — MFCreateDXGISurfaceBuffer needs the riid of the surface we wrap.
    private static readonly Guid ID3D11Texture2DIid = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");

    private const uint MFVideoInterlace_Progressive = 2;
    private const uint eAVEncH264VProfile_High = 100;

    // ICodecAPI properties (passed to the encoder via SetInputMediaType's encodingParameters) so the
    // hardware encoder honours a target bitrate instead of its default quality-VBR (which over-shoots).
    private static readonly Guid CODECAPI_AVEncCommonRateControlMode = new("1c0608e9-370c-4710-8a58-cb6181c42423");
    private static readonly Guid CODECAPI_AVEncCommonMeanBitRate = new("f7222374-2144-4815-b550-a37f8e12ee52");
    private static readonly Guid CODECAPI_AVEncMPVGOPSize = new("95f31b26-95a4-41aa-9303-246a7fc6eef1");
    private const uint eAVEncCommonRateControlMode_UnconstrainedVBR = 2; // target the mean; efficient for screen content

    private readonly IMFSinkWriter _writer;
    private readonly int _videoStream;
    private readonly int _audioStream = -1;
    private readonly object _writeGate = new(); // serialize muxer access (video feeder thread + audio thread)
    private bool _finalized;

    public bool HasAudio => _audioStream >= 0;

    /// <param name="audioSampleRate">0 disables audio. Otherwise the captured rate (e.g. 48000).</param>
    public MfH264SinkWriter(GpuRecordingDevice gpu, string path, int width, int height, int fps, int bitrate,
        int audioSampleRate = 0, int audioChannels = 2, int audioBitrateBps = 160_000)
    {
        using var attrs = MediaFactory.MFCreateAttributes(4);
        attrs.Set(SinkWriterAttributeKeys.D3DManager, gpu.DeviceManager);        // share our device → HW encoder, zero-copy
        attrs.Set(SinkWriterAttributeKeys.ReadwriteEnableHardwareTransforms, 1u); // prefer the hardware H.264 MFT
        // NOTE: throttling left ENABLED. It applies natural back-pressure so encoded samples can't pile up
        // unbounded (which wedges the shared-device encoder). A HW encoder keeps up at 1080p60, so in
        // practice WriteSample doesn't block; the FrameFeeder's cadence stays smooth.

        _writer = MediaFactory.MFCreateSinkWriterFromURL(path, null, attrs);

        // --- Video stream: H.264 (the .mp4 URL selects the MPEG-4 file sink + muxer) ---
        using var outType = MediaFactory.MFCreateMediaType();
        outType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        outType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264);
        outType.Set(MediaTypeAttributeKeys.AvgBitrate, (uint)bitrate);
        outType.Set(MediaTypeAttributeKeys.InterlaceMode, MFVideoInterlace_Progressive);
        outType.Set(MediaTypeAttributeKeys.Mpeg2Profile, eAVEncH264VProfile_High);
        SetRatio(outType, MediaTypeAttributeKeys.FrameSize, width, height);
        SetRatio(outType, MediaTypeAttributeKeys.FrameRate, fps, 1);
        SetRatio(outType, MediaTypeAttributeKeys.PixelAspectRatio, 1, 1);
        _videoStream = _writer.AddStream(outType);

        // --- Audio stream: AAC (optional) ---
        if (audioSampleRate > 0)
        {
            using var aOut = MediaFactory.MFCreateMediaType();
            aOut.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
            aOut.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Aac);
            aOut.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, (uint)audioSampleRate);
            aOut.Set(MediaTypeAttributeKeys.AudioNumChannels, (uint)audioChannels);
            aOut.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 16u);
            aOut.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, (uint)SnapAacBytesPerSec(audioBitrateBps));
            aOut.Set(MediaTypeAttributeKeys.AacAudioProfileLevelIndication, 0x29u); // AAC-LC, level 2
            _audioStream = _writer.AddStream(aOut);
        }

        // --- Video input: the NV12 textures the converter produces ---
        using var inType = MediaFactory.MFCreateMediaType();
        inType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        inType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.NV12);
        inType.Set(MediaTypeAttributeKeys.InterlaceMode, MFVideoInterlace_Progressive);
        SetRatio(inType, MediaTypeAttributeKeys.FrameSize, width, height);
        SetRatio(inType, MediaTypeAttributeKeys.FrameRate, fps, 1);
        SetRatio(inType, MediaTypeAttributeKeys.PixelAspectRatio, 1, 1);

        using var enc = MediaFactory.MFCreateAttributes(3);
        enc.Set(CODECAPI_AVEncCommonRateControlMode, eAVEncCommonRateControlMode_UnconstrainedVBR);
        enc.Set(CODECAPI_AVEncCommonMeanBitRate, (uint)bitrate);
        enc.Set(CODECAPI_AVEncMPVGOPSize, (uint)(fps * 2));
        _writer.SetInputMediaType(_videoStream, inType, enc);

        // --- Audio input: interleaved 16-bit PCM ---
        if (_audioStream >= 0)
        {
            using var aIn = MediaFactory.MFCreateMediaType();
            aIn.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
            aIn.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Pcm);
            aIn.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, (uint)audioSampleRate);
            aIn.Set(MediaTypeAttributeKeys.AudioNumChannels, (uint)audioChannels);
            aIn.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 16u);
            aIn.Set(MediaTypeAttributeKeys.AudioBlockAlignment, (uint)(audioChannels * 2));
            aIn.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, (uint)(audioSampleRate * audioChannels * 2));
            _writer.SetInputMediaType(_audioStream, aIn, null);
        }

        _writer.BeginWriting();
    }

    // The MF AAC encoder only accepts a fixed set of output byte-rates; snap to the nearest.
    private static int SnapAacBytesPerSec(int bitsPerSec)
    {
        int target = bitsPerSec / 8;
        int[] valid = { 12000, 16000, 20000, 24000 };
        int best = valid[0];
        foreach (int v in valid) if (Math.Abs(v - target) < Math.Abs(best - target)) best = v;
        return best;
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
        lock (_writeGate) { if (!_finalized) _writer.WriteSample(_videoStream, sample); }

        buffer.Dispose(); // the MFT keeps its own ref while encoding; our wrapper is done
    }

    /// <summary>Queues interleaved 16-bit PCM audio (system memory). Times are in 100-ns units.</summary>
    public void WriteAudio(byte[] pcm, int count, long sampleTime100ns, long duration100ns)
    {
        if (_audioStream < 0 || count <= 0) return;

        var buffer = MediaFactory.MFCreateMemoryBuffer(count);
        buffer.Lock(out IntPtr dst, out _, out _);
        Marshal.Copy(pcm, 0, dst, count);
        buffer.Unlock();
        buffer.CurrentLength = count;

        using var sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(buffer);
        sample.SampleTime = sampleTime100ns;
        sample.SampleDuration = duration100ns;
        lock (_writeGate) { if (!_finalized) _writer.WriteSample(_audioStream, sample); }

        buffer.Dispose();
    }

    /// <summary>Flushes and finalizes the MP4 (writes the moov box). Safe to call once.</summary>
    public void Stop()
    {
        lock (_writeGate)
        {
            if (_finalized) return;
            _finalized = true;
            try { _writer.Finalize(); } catch { }
        }
    }

    public void Dispose()
    {
        Stop();
        try { _writer.Dispose(); } catch { }
    }
}
