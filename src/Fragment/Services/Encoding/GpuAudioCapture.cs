using System;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Fragment.Services.Encoding;

/// <summary>
/// Captures system (desktop) audio via WASAPI loopback and delivers it as interleaved 16-bit PCM —
/// the format the Media Foundation AAC encoder wants — to a callback. A silent keep-alive render
/// stream keeps the endpoint active so loopback keeps delivering even when nothing is playing
/// (otherwise WASAPI loopback stalls during silence and the audio drifts out of sync).
/// </summary>
public sealed class GpuAudioCapture : IDisposable
{
    private readonly Action<byte[], int> _onPcm16; // (interleaved int16 buffer, valid byte count)
    private WasapiLoopbackCapture? _cap;
    private IWavePlayer? _silence;
    private byte[] _pcm = Array.Empty<byte>();
    private bool _isFloat;
    private int _inBytesPerSample;
    private volatile bool _disposed;

    public int SampleRate { get; }
    public int Channels { get; }

    public GpuAudioCapture(Action<byte[], int> onPcm16)
    {
        _onPcm16 = onPcm16;
        // Construct now so the caller can read the device format before the encoder is configured.
        _cap = new WasapiLoopbackCapture();
        var f = _cap.WaveFormat;
        SampleRate = f.SampleRate;
        Channels = f.Channels;
        _isFloat = f.Encoding == WaveFormatEncoding.IeeeFloat;
        _inBytesPerSample = f.BitsPerSample / 8;
    }

    public void Start()
    {
        if (_cap is null) return;

        // Inaudible silence on the render endpoint so loopback always produces frames.
        try
        {
            var s = new WasapiOut(AudioClientShareMode.Shared, 100);
            s.Init(new SilenceProvider(_cap.WaveFormat));
            s.Play();
            _silence = s;
        }
        catch { /* best effort */ }

        _cap.DataAvailable += OnData;
        _cap.StartRecording();
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        if (_disposed || e.BytesRecorded <= 0) return;

        int sampleCount = e.BytesRecorded / _inBytesPerSample; // total samples across all channels
        int outBytes = sampleCount * 2;
        if (_pcm.Length < outBytes) _pcm = new byte[outBytes];

        var outShorts = MemoryMarshal.Cast<byte, short>(_pcm.AsSpan(0, outBytes));
        if (_isFloat)
        {
            var src = MemoryMarshal.Cast<byte, float>(e.Buffer.AsSpan(0, e.BytesRecorded));
            for (int i = 0; i < src.Length; i++)
            {
                float v = src[i];
                v = v < -1f ? -1f : (v > 1f ? 1f : v);
                outShorts[i] = (short)(v * 32767f);
            }
        }
        else if (_inBytesPerSample == 2)
        {
            Buffer.BlockCopy(e.Buffer, 0, _pcm, 0, outBytes);
        }
        else // 24/32-bit integer PCM → take the high 16 bits
        {
            for (int i = 0; i < sampleCount; i++)
            {
                int off = i * _inBytesPerSample;
                short hi = (short)(e.Buffer[off + _inBytesPerSample - 2] | (e.Buffer[off + _inBytesPerSample - 1] << 8));
                outShorts[i] = hi;
            }
        }

        _onPcm16(_pcm, outBytes);
    }

    public void Dispose()
    {
        _disposed = true;
        try { if (_cap != null) _cap.DataAvailable -= OnData; } catch { }
        try { _cap?.StopRecording(); } catch { }
        try { _cap?.Dispose(); } catch { }
        try { _silence?.Stop(); } catch { }
        try { _silence?.Dispose(); } catch { }
        _cap = null;
        _silence = null;
    }
}
