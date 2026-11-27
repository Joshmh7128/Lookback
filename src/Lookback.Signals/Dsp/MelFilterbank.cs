namespace Lookback.Signals.Dsp;

/// <summary>
/// Triangular mel-scale filters over an FFT magnitude spectrum. The mel
/// scale spaces bands the way hearing does, so low frequencies (footsteps,
/// explosions) get finer resolution than high ones, which suits the audio
/// event models used later.
/// </summary>
public sealed class MelFilterbank
{
    private readonly (int Start, float[] Weights)[] _filters;

    public MelFilterbank(int bands, int fftSize, int sampleRate, float minHz = 20f, float? maxHz = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(bands, 1);
        Bands = bands;
        float top = maxHz ?? sampleRate / 2f;
        int bins = (fftSize / 2) + 1;
        double melMin = HzToMel(minHz), melMax = HzToMel(top);

        // bands + 2 edge points, evenly spaced in mel, mapped to fractional FFT bins.
        var edges = new double[bands + 2];
        for (int i = 0; i < edges.Length; i++)
        {
            double hz = MelToHz(melMin + ((melMax - melMin) * i / (bands + 1)));
            edges[i] = hz * fftSize / sampleRate;
        }

        _filters = new (int, float[])[bands];
        for (int b = 0; b < bands; b++)
        {
            double lo = edges[b], mid = edges[b + 1], hi = edges[b + 2];
            int start = Math.Max(0, (int)Math.Floor(lo));
            int end = Math.Min(bins - 1, (int)Math.Ceiling(hi));
            var w = new float[Math.Max(1, end - start + 1)];
            for (int k = start; k <= end; k++)
            {
                double v = k <= mid ? (k - lo) / Math.Max(mid - lo, 1e-9) : (hi - k) / Math.Max(hi - mid, 1e-9);
                w[k - start] = (float)Math.Max(0, v);
            }

            _filters[b] = (start, w);
        }
    }

    public int Bands { get; }

    /// <summary>Writes log10 band energies of <paramref name="power"/> (|X|², bins 0..N/2) into <paramref name="output"/>.</summary>
    public void Apply(ReadOnlySpan<float> power, Span<float> output)
    {
        for (int b = 0; b < _filters.Length; b++)
        {
            (int start, float[] w) = _filters[b];
            double e = 0;
            for (int i = 0; i < w.Length && start + i < power.Length; i++)
            {
                e += w[i] * power[start + i];
            }

            output[b] = (float)Math.Log10(e + 1e-10);
        }
    }

    public static double HzToMel(double hz) => 2595.0 * Math.Log10(1 + (hz / 700.0));

    public static double MelToHz(double mel) => 700.0 * (Math.Pow(10, mel / 2595.0) - 1);
}
