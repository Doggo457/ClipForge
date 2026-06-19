using System;
using System.Runtime.InteropServices;
using System.Threading;
using SharpGen.Runtime;
using Vortice.Direct3D11;
using Vortice.MediaFoundation;

namespace Fragment.Services.Encoding;

/// <summary>
/// Drives the hardware H.264 encoder MFT directly (the AMD encoder is an ASYNC MFT, so this is the
/// event-driven model) to turn NV12 GPU textures into encoded H.264 samples WITHOUT writing a file —
/// the samples are handed to a callback (the replay ring). The MFT shares our D3D11 device via the DXGI
/// device manager (zero-copy NV12 input).
///
/// Threading: <see cref="TryConsumeNeedInput"/> + <see cref="SubmitFrame"/> are called on the single feeder
/// thread (the only thread allowed to touch the D3D context). A private event thread pumps METransform*
/// events and calls ProcessOutput. All IMFTransform calls are serialized by <c>_transformLock</c>.
/// </summary>
public sealed class MfH264EncoderMft : IDisposable
{
    private static readonly Guid ID3D11Texture2DIid = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");

    private const uint MFVideoInterlace_Progressive = 2;
    private const uint eAVEncH264VProfile_Main = 77;
    private static readonly Guid MF_LOW_LATENCY = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
    private static readonly Guid CODECAPI_AVEncCommonRateControlMode = new("1c0608e9-370c-4710-8a58-cb6181c42423");
    private static readonly Guid CODECAPI_AVEncCommonMeanBitRate = new("f7222374-2144-4815-b550-a37f8e12ee52");
    private static readonly Guid CODECAPI_AVEncMPVGOPSize = new("95f31b26-95a4-41aa-9303-246a7fc6eef1");
    private const uint eAVEncCommonRateControlMode_UnconstrainedVBR = 2; // mean target; matches the proven sink-writer path

    private const int MF_E_TRANSFORM_NEED_MORE_INPUT = unchecked((int)0xC00D6D72);
    private const int MF_E_TRANSFORM_STREAM_CHANGE = unchecked((int)0xC00D6D61);

    private readonly Action<EncodedVideoSample> _onOutput;
    private readonly IMFTransform _mft;
    private readonly IMFMediaEventGenerator _events;
    private readonly object _transformLock = new();  // serializes all IMFTransform calls
    private readonly object _typeLock = new();        // guards _outputType
    private IMFMediaType _outputType;
    private Thread? _eventThread;
    private volatile bool _disposed;
    private int _needInput;
    private readonly ManualResetEventSlim _drainDone = new(false);

    public long SamplesOut; // diagnostics
    public long KeyFramesOut;

    public MfH264EncoderMft(GpuRecordingDevice gpu, int width, int height, int fps, int bitrate,
        Action<EncodedVideoSample> onOutput)
    {
        _onOutput = onOutput;
        _mft = CreateHardwareEncoder();
        try
        {
            _mft.Attributes.Set(TransformAttributeKeys.TransformAsyncUnlock, 1u);
            // NOTE: MF_LOW_LATENCY is deliberately NOT set — on the AMD encoder it forces a high fixed
            // bitrate that ignores rate control. Rate control is applied via ICodecAPI (SetRateControl).

            _mft.ProcessMessage(TMessageType.MessageSetD3DManager,
                unchecked((UIntPtr)(ulong)gpu.DeviceManager.NativePointer.ToInt64()));

            // Rate control must be set BEFORE the output type — the AMD encoder bakes it in at SetOutputType.
            SetRateControl(bitrate, fps);

            using (var outType = MediaFactory.MFCreateMediaType())
            {
                outType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
                outType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264);
                outType.Set(MediaTypeAttributeKeys.AvgBitrate, (uint)bitrate);
                outType.Set(MediaTypeAttributeKeys.InterlaceMode, MFVideoInterlace_Progressive);
                outType.Set(MediaTypeAttributeKeys.Mpeg2Profile, eAVEncH264VProfile_Main);
                SetRatio(outType, MediaTypeAttributeKeys.FrameSize, width, height);
                SetRatio(outType, MediaTypeAttributeKeys.FrameRate, fps, 1);
                SetRatio(outType, MediaTypeAttributeKeys.PixelAspectRatio, 1, 1);
                _mft.SetOutputType(0, outType, 0);
            }

            using (var inType = MediaFactory.MFCreateMediaType())
            {
                inType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
                inType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.NV12);
                inType.Set(MediaTypeAttributeKeys.InterlaceMode, MFVideoInterlace_Progressive);
                SetRatio(inType, MediaTypeAttributeKeys.FrameSize, width, height);
                SetRatio(inType, MediaTypeAttributeKeys.FrameRate, fps, 1);
                SetRatio(inType, MediaTypeAttributeKeys.PixelAspectRatio, 1, 1);
                _mft.SetInputType(0, inType, 0);
            }

            _outputType = _mft.GetOutputCurrentType(0);
            _events = _mft.QueryInterface<IMFMediaEventGenerator>();

            _mft.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
            _mft.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);

            _eventThread = new Thread(EventLoop) { IsBackground = true, Name = "MfH264EncoderEvents" };
            _eventThread.Start();
        }
        catch
        {
            // Don't leak the native HW encoder MFT (limited HW sessions) if init fails after activation.
            try { _outputType?.Dispose(); } catch { }
            try { _events?.Dispose(); } catch { }
            try { _mft.Dispose(); } catch { }
            throw;
        }
    }

    private static IMFTransform CreateHardwareEncoder()
    {
        var inInfo = new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = VideoFormatGuids.NV12 };
        var outInfo = new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = VideoFormatGuids.H264 };
        uint flags = (uint)(EnumFlag.EnumFlagHardware | EnumFlag.EnumFlagAsyncmft | EnumFlag.EnumFlagSortandfilter);

        MediaFactory.MFTEnumEx(TransformCategoryGuids.VideoEncoder, flags, inInfo, outInfo, out IntPtr pp, out uint count);
        if (count == 0 || pp == IntPtr.Zero)
            throw new InvalidOperationException("No hardware H.264 encoder MFT found.");

        IMFTransform? chosen = null;
        try
        {
            for (int i = 0; i < count; i++)
            {
                IntPtr actPtr = Marshal.ReadIntPtr(pp, i * IntPtr.Size);
                if (actPtr == IntPtr.Zero) continue;
                using var act = MarshallingHelpers.FromPointer<IMFActivate>(actPtr);
                if (chosen is null && act != null)
                {
                    try { chosen = act.ActivateObject<IMFTransform>(); } catch { chosen = null; }
                }
            }
        }
        finally { Marshal.FreeCoTaskMem(pp); }

        return chosen ?? throw new InvalidOperationException("Failed to activate the hardware H.264 encoder MFT.");
    }

    private static void SetRatio(IMFMediaType t, Guid key, int hi, int lo)
        => t.Set(key, ((ulong)(uint)hi << 32) | (uint)lo);

    // Configure rate control through ICodecAPI (the encoder exposes it; Vortice doesn't bind it, so we
    // QI the raw COM interface). UnconstrainedVBR targeting the mean bitrate -> efficient, honours the slider.
    private void SetRateControl(int bitrate, int fps)
    {
        object? rcw = null;
        try
        {
            rcw = Marshal.GetObjectForIUnknown(_mft.NativePointer);
            if (rcw is ICodecAPI codec)
            {
                int h1 = CodecSet(codec, CODECAPI_AVEncCommonRateControlMode, eAVEncCommonRateControlMode_UnconstrainedVBR);
                int h2 = CodecSet(codec, CODECAPI_AVEncCommonMeanBitRate, (uint)bitrate);
                int h4 = CodecSet(codec, CODECAPI_AVEncMPVGOPSize, (uint)(fps * 2)); // 2 s GOP: fewer I-frames -> smaller
                Diag($"ICodecAPI ok: mode=0x{h1:X} mean=0x{h2:X} gop=0x{h4:X} (bitrate={bitrate})");
            }
            else Diag("ICodecAPI: QI FAILED (encoder keeps default rate control)");
        }
        catch (Exception ex) { Diag("ICodecAPI threw: " + ex.Message); }
        finally { if (rcw != null) { try { Marshal.ReleaseComObject(rcw); } catch { } } }
    }

    private static int CodecSet(ICodecAPI codec, Guid api, uint value)
    {
        object v = value; // boxed uint -> VARIANT VT_UI4
        try { return codec.SetValue(ref api, ref v); } catch (Exception ex) { return ex.HResult; }
    }

    private static void Diag(string m)
    {
        try
        {
            var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Fragment");
            System.IO.Directory.CreateDirectory(dir);
            System.IO.File.AppendAllText(System.IO.Path.Combine(dir, "gpu_engine.log"), $"{DateTime.Now:HH:mm:ss} ENC {m}{Environment.NewLine}");
        }
        catch { }
    }

    /// <summary>Returns an independent copy of the encoder's negotiated output type (SPS/PPS) for the save muxer.</summary>
    public IMFMediaType CloneOutputType()
    {
        lock (_typeLock)
        {
            var c = MediaFactory.MFCreateMediaType();
            _outputType.CopyAllItems(c);
            return c;
        }
    }

    public bool TryConsumeNeedInput()
    {
        while (true)
        {
            int n = _needInput;
            if (n <= 0) return false;
            if (Interlocked.CompareExchange(ref _needInput, n - 1, n) == n) return true;
        }
    }

    /// <summary>Feeder-thread only: hand one NV12 texture to the encoder. Call after TryConsumeNeedInput() is true.</summary>
    public void SubmitFrame(ID3D11Texture2D nv12, long sampleTime100ns, long duration100ns)
    {
        using var buffer = MediaFactory.MFCreateDXGISurfaceBuffer(ID3D11Texture2DIid, nv12, 0, false);
        try { using var b2d = buffer.QueryInterface<IMF2DBuffer>(); buffer.CurrentLength = b2d.ContiguousLength; }
        catch { }

        using var sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(buffer); // AddBuffer takes its own ref; the using disposes our creation ref safely
        sample.SampleTime = sampleTime100ns;
        sample.SampleDuration = duration100ns;
        lock (_transformLock) { if (!_disposed) _mft.ProcessInput(0, sample, 0); }
    }

    private void EventLoop()
    {
        while (true)
        {
            IMFMediaEvent ev;
            try { ev = _events.GetEvent(0); } // blocking; unblocked by drain events or generator shutdown
            catch { break; }

            bool stop = false;
            try
            {
                var type = ev.EventType;
                if (type == MediaEventTypes.TransformNeedInput) Interlocked.Increment(ref _needInput);
                else if (type == MediaEventTypes.TransformHaveOutput) DrainOutputs();
                else if (type == MediaEventTypes.TransformDrainComplete) { _drainDone.Set(); stop = true; }
            }
            catch { }
            finally { try { ev.Dispose(); } catch { } }

            if (stop) break; // drain completed -> shutdown
        }
    }

    private void DrainOutputs()
    {
        while (true)
        {
            var buf = new OutputDataBuffer { StreamID = 0 };
            Result hr;
            lock (_transformLock) { hr = _mft.ProcessOutput(ProcessOutputFlags.None, 1, ref buf, out _); }
            try { buf.Events?.Dispose(); } catch { }

            if (hr.Code == MF_E_TRANSFORM_NEED_MORE_INPUT) return;
            if (hr.Code == MF_E_TRANSFORM_STREAM_CHANGE)
            {
                lock (_transformLock)
                {
                    try
                    {
                        using var nt = _mft.GetOutputAvailableType(0, 0);
                        _mft.SetOutputType(0, nt, 0);
                    }
                    catch { }
                    try { lock (_typeLock) { var old = _outputType; _outputType = _mft.GetOutputCurrentType(0); old?.Dispose(); } } catch { }
                }
                continue;
            }
            if (hr.Failure || buf.Sample is null) return;

            var sample = buf.Sample;
            try
            {
                using var mb = sample.ConvertToContiguousBuffer();
                mb.Lock(out IntPtr p, out _, out int curLen);
                var data = new byte[curLen];
                Marshal.Copy(p, data, 0, curLen);
                mb.Unlock();

                bool key = sample.GetUInt32(SampleAttributeKeys.CleanPoint, out uint v).Success && v != 0;
                SamplesOut++;
                if (key) KeyFramesOut++;
                if (!_disposed)
                    _onOutput(new EncodedVideoSample { Data = data, TimeNs = sample.SampleTime, DurNs = sample.SampleDuration, KeyFrame = key });
            }
            finally { try { sample.Dispose(); } catch { } }
        }
    }

    // Minimal ICodecAPI binding (Vortice doesn't expose it). Only SetValue is called; the methods before
    // it are vtable placeholders so SetValue lands at the correct slot.
    [ComImport, Guid("901db4c7-31ce-41a2-85dc-8fa0bf41b8da"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICodecAPI
    {
        void IsSupported();
        void IsModifiable();
        void GetParameterRange();
        void GetParameterValues();
        void GetDefaultValue();
        void GetValue();
        [PreserveSig] int SetValue([In] ref Guid Api, [In, MarshalAs(UnmanagedType.Struct)] ref object Value);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Ordered async-MFT shutdown that also unblocks the event thread's blocking GetEvent:
        // EndOfStream -> Drain (the MFT emits remaining outputs then DrainComplete, which wakes GetEvent and
        // makes the event loop exit) -> join -> EndStreaming -> release.
        bool joined = false;
        try
        {
            _drainDone.Reset();
            lock (_transformLock)
            {
                try { _mft.ProcessMessage(TMessageType.MessageNotifyEndOfStream, UIntPtr.Zero); } catch { }
                try { _mft.ProcessMessage(TMessageType.MessageCommandDrain, UIntPtr.Zero); } catch { }
            }

            joined = _eventThread == null || _eventThread.Join(3000);
            if (!joined)
            {
                // Device-loss / no DrainComplete: force the blocked GetEvent to return by tearing down the
                // generator, then wait again (the thread exits once GetEvent throws / ProcessOutput returns).
                try { _events.Dispose(); } catch { }
                joined = _eventThread!.Join(2000);
            }
        }
        catch { }

        try { lock (_transformLock) _mft.ProcessMessage(TMessageType.MessageNotifyEndStreaming, UIntPtr.Zero); } catch { }
        try { _events.Dispose(); } catch { }
        try { lock (_typeLock) _outputType?.Dispose(); } catch { }

        // Only release the MFT/event once the event thread is definitely gone. If a faulted driver never
        // returned from ProcessOutput, leak them rather than risk a use-after-free crash (rare + safe).
        if (joined)
        {
            try { _mft.Dispose(); } catch { }
            _drainDone.Dispose();
        }
    }
}
