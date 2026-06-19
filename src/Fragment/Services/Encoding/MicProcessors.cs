using System;
using NAudio.Wave;

namespace Fragment.Services.Encoding;

/// <summary>Mic cleanup options (per recording profile). Off by default.</summary>
public readonly record struct MicProcessing(
    bool GateEnabled, float GateThresholdDb,
    bool SuppressEnabled, float SuppressStrength)
{
    public static readonly MicProcessing None = new(false, -40f, false, 0.6f);
    public bool Any => GateEnabled || SuppressEnabled;
}

/// <summary>
/// Downward noise gate: when the signal sits below a threshold (for a hold time) the output is smoothly
/// faded out, so room hum / fan / keyboard noise between speech is removed. Smooth attack/hold/release
/// envelope avoids choppiness. Works on any channel count (gain driven by the loudest channel per frame).
/// </summary>
public sealed class NoiseGateSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _src;
    private readonly int _ch;
    private readonly float _thresh;        // linear threshold
    private readonly float _attack, _release; // one-pole coefficients per frame
    private readonly int _hold;            // frames to hold open after dropping below threshold
    private float _gain;                   // current gain 0..1
    private int _holdCtr;

    public WaveFormat WaveFormat => _src.WaveFormat;

    public NoiseGateSampleProvider(ISampleProvider src, float thresholdDb,
        float attackMs = 5f, float holdMs = 150f, float releaseMs = 200f)
    {
        _src = src;
        _ch = src.WaveFormat.Channels;
        int rate = src.WaveFormat.SampleRate;
        _thresh = (float)Math.Pow(10.0, thresholdDb / 20.0);
        _attack = (float)Math.Exp(-1.0 / Math.Max(1, rate * attackMs / 1000.0));
        _release = (float)Math.Exp(-1.0 / Math.Max(1, rate * releaseMs / 1000.0));
        _hold = (int)(rate * holdMs / 1000.0);
        _gain = 0f;
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int n = _src.Read(buffer, offset, count);
        for (int i = 0; i + _ch <= n; i += _ch)
        {
            float lvl = 0f;
            for (int c = 0; c < _ch; c++) { float a = Math.Abs(buffer[offset + i + c]); if (a > lvl) lvl = a; }

            bool open;
            if (lvl >= _thresh) { _holdCtr = _hold; open = true; }
            else if (_holdCtr > 0) { _holdCtr--; open = true; }
            else open = false;

            float target = open ? 1f : 0f;
            float coef = target > _gain ? _attack : _release; // fast-ish open, slow close
            _gain = target + (_gain - target) * coef;

            for (int c = 0; c < _ch; c++) buffer[offset + i + c] *= _gain;
        }
        return n;
    }
}

/// <summary>
/// Spectral noise suppressor (STFT spectral gate). Estimates a per-frequency noise floor by minimum
/// statistics and attenuates bins that sit near the floor, reducing steady background noise (hiss, fans,
/// hum) even while speaking. Mono in/out (the mic is mono-mixed first). 512-pt FFT, 75% overlap-add,
/// Hann window, with time smoothing of the gain mask to limit musical-noise artifacts.
/// </summary>
public sealed class SpectralNoiseSuppressor : ISampleProvider
{
    private const int N = 512;
    private const int Hop = 128;          // 75% overlap
    private const int Bins = N / 2 + 1;

    private readonly ISampleProvider _src; // mono
    private readonly float[] _win = new float[N];
    private readonly float _ola1; // overlap-add normalization (constant for fixed window+hop)
    private readonly float _floorGain;
    private readonly float _sensitivity;

    private readonly float[] _frame = new float[N];   // sliding analysis frame
    private readonly float[] _re = new float[N];
    private readonly float[] _im = new float[N];
    private readonly float[] _noise = new float[Bins];
    private readonly float[] _gain = new float[Bins];
    private bool _noiseInit;

    // input accumulation (samples awaiting a full hop) and output FIFO
    private readonly float[] _ola = new float[N];     // overlap-add accumulator
    private readonly float[] _outFifo = new float[N * 8];
    private int _outHead, _outCount;
    private int _frameFill;                           // how much of _frame is primed

    public WaveFormat WaveFormat => _src.WaveFormat;

    public SpectralNoiseSuppressor(ISampleProvider src, float strength)
    {
        _src = src;
        for (int i = 0; i < N; i++) _win[i] = (float)(0.5 * (1 - Math.Cos(2 * Math.PI * i / (N - 1)))); // Hann

        // Overlap-add normalization: steady-state sum of win^2 across overlapping frames.
        var acc = new float[N * 4];
        for (int f = 0; f * Hop + N <= acc.Length; f++)
            for (int i = 0; i < N; i++) acc[f * Hop + i] += _win[i] * _win[i];
        _ola1 = acc[N * 2] > 1e-6f ? acc[N * 2] : 1f;

        strength = Math.Clamp(strength, 0f, 1f);
        _floorGain = (float)Math.Pow(10.0, (-6.0 - 20.0 * strength) / 20.0); // -6 dB (weak) .. -26 dB (strong)
        _sensitivity = 1.6f + 3.0f * strength;                               // higher = more aggressive
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int produced = 0;
        while (produced < count)
        {
            if (_outCount > 0)
            {
                int take = Math.Min(count - produced, _outCount);
                for (int i = 0; i < take; i++) { buffer[offset + produced + i] = _outFifo[_outHead]; _outHead = (_outHead + 1) % _outFifo.Length; }
                _outCount -= take; produced += take;
                continue;
            }

            // Need a new hop of input.
            int got = ReadHop();
            if (got == 0) break;          // source drained
            ProcessFrame();
        }
        return produced;
    }

    private float[] _hopBuf = new float[Hop];
    private int ReadHop()
    {
        int total = 0;
        while (total < Hop)
        {
            int r = _src.Read(_hopBuf, total, Hop - total);
            if (r == 0) break;
            total += r;
        }
        if (total == 0) return 0;
        for (int i = total; i < Hop; i++) _hopBuf[i] = 0f; // pad tail

        // slide frame left by Hop, append new hop
        Array.Copy(_frame, Hop, _frame, 0, N - Hop);
        Array.Copy(_hopBuf, 0, _frame, N - Hop, Hop);
        if (_frameFill < N) { _frameFill += Hop; if (_frameFill < N) return total; } // prime the frame first
        return total;
    }

    private void ProcessFrame()
    {
        for (int i = 0; i < N; i++) { _re[i] = _frame[i] * _win[i]; _im[i] = 0f; }
        Fft(_re, _im, false);

        for (int b = 0; b < Bins; b++)
        {
            float mag = (float)Math.Sqrt(_re[b] * _re[b] + _im[b] * _im[b]);
            if (!_noiseInit) _noise[b] = mag;
            else _noise[b] = Math.Min(mag, _noise[b] * 1.0015f); // minimum statistics: instant fall, slow rise

            float g = mag > _noise[b] * _sensitivity ? 1f : _floorGain;
            g = _gain[b] * 0.6f + g * 0.4f; // time-smooth the mask
            _gain[b] = g;

            _re[b] *= g; _im[b] *= g;
            if (b > 0 && b < N / 2) { _re[N - b] *= g; _im[N - b] *= g; } // mirror to conjugate bin
        }
        _noiseInit = true;

        Fft(_re, _im, true);

        // synthesis window + overlap-add, emit oldest Hop samples
        for (int i = 0; i < N; i++) _ola[i] += _re[i] * _win[i];
        for (int i = 0; i < Hop; i++)
        {
            float s = _ola[i] / _ola1;
            _outFifo[(_outHead + _outCount) % _outFifo.Length] = s;
            if (_outCount < _outFifo.Length) _outCount++;
        }
        Array.Copy(_ola, Hop, _ola, 0, N - Hop);
        Array.Clear(_ola, N - Hop, Hop);
    }

    // In-place iterative radix-2 FFT. inverse divides by N (FFT->IFFT is identity).
    private static void Fft(float[] re, float[] im, bool inverse)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = 2 * Math.PI / len * (inverse ? 1 : -1);
            float wr = (float)Math.Cos(ang), wi = (float)Math.Sin(ang);
            for (int i = 0; i < n; i += len)
            {
                float cwr = 1f, cwi = 0f;
                for (int k = 0; k < len / 2; k++)
                {
                    int a = i + k, b = a + len / 2;
                    float tr = cwr * re[b] - cwi * im[b], ti = cwr * im[b] + cwi * re[b];
                    re[b] = re[a] - tr; im[b] = im[a] - ti;
                    re[a] += tr; im[a] += ti;
                    float ncwr = cwr * wr - cwi * wi; cwi = cwr * wi + cwi * wr; cwr = ncwr;
                }
            }
        }
        if (inverse) for (int i = 0; i < n; i++) { re[i] /= n; im[i] /= n; }
    }
}
