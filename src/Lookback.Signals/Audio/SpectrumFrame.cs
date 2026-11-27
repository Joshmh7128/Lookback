using Lookback.Media.Frames;
using Lookback.Signals.Dsp;

namespace Lookback.Signals.Audio;

/// <summary>
/// Sliding-window power spectrum, updated once per hop and shared by every
/// spectral signal. Keeps the last <c>fftSize</c> samples (for example 1024,
/// ~21 ms at 48 kHz), applies a Hann window and computes |X|² for bins
/// 0..N/2.
/// </summary>
public sealed class SpectrumFrame
{
    private readonly Fft _fft;
    private readonly float[] _history;
    private readonly float[] _window;
    private readonly float[] _re;
    private readonly float[] _im;
    private readonly float[] _power;

    public SpectrumFrame(int fftSize, int sampleRate)
    {
        _fft = new Fft(fftSize);
        SampleRate = sampleRate;
        _history = new float[fftSize];
        _window = new float[fftSize];
        for (int i = 0; i < fftSize; i++)
        {
            _window[i] = (float)(0.5 - (0.5 * Math.Cos(2 * Math.PI * i / (fftSize - 1))));
        }

        _re = new float[fftSize];
        _im = new float[fftSize];
        _power = new float[(fftSize / 2) + 1];
    }

    public int FftSize => _fft.Size;

    public int SampleRate { get; }

    /// <summary>|X|² per bin, bins 0..N/2. Bin k is k·SampleRate/FftSize Hz.</summary>
    public ReadOnlySpan<float> Power => _power;

    public void Update(AudioBlock block)
    {
        ArgumentNullException.ThrowIfNull(block);
        ReadOnlySpan<float> s = block.Samples;
        int n = _history.Length;
        if (s.Length >= n)
        {
            s[^n..].CopyTo(_history);
        }
        else
        {
            _history.AsSpan(s.Length).CopyTo(_history); // shift left
            s.CopyTo(_history.AsSpan(n - s.Length));
        }

        for (int i = 0; i < n; i++)
        {
            _re[i] = _history[i] * _window[i];
            _im[i] = 0;
        }

        _fft.Forward(_re, _im);
        for (int k = 0; k < _power.Length; k++)
        {
            _power[k] = (_re[k] * _re[k]) + (_im[k] * _im[k]);
        }
    }

    public void Reset() => Array.Clear(_history);
}
