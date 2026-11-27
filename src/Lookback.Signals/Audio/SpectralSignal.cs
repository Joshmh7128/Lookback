using Lookback.Media.Frames;
using Lookback.Signals.Dsp;

namespace Lookback.Signals.Audio;

/// <summary>
/// Spectral shape per hop.
/// <list type="bullet">
/// <item><c>spectral.flux</c>: mean positive change in compressed magnitude,
/// log10(1 + γ|X|), since the last hop. The compression keeps near-empty bins
/// near zero; a plain log would turn rounding noise in silent bins into
/// large swings. Gunshots, impacts and explosions are sharp onsets with high
/// flux; music and engine drone are not.</item>
/// <item><c>spectral.onset</c>: 1 when flux clears an adaptive threshold:
/// above the running mean by k standard deviations <i>and</i> by a ratio of
/// the mean, after a short warm-up. The ratio stops codec noise on a steady
/// tone from counting as onsets; the adaptive part keeps onset rates
/// comparable across loud and quiet games.</item>
/// <item><c>spectral.centroid_hz</c>: brightness; explosions are dark, UI
/// stingers and gunfire bright.</item>
/// <item><c>mel.00</c>..: log mel band energies, the input for later sound
/// event models.</item>
/// </list>
/// </summary>
public sealed class SpectralSignal : IAudioSignal
{
    private const float ThresholdK = 2.5f;
    private const float ThresholdRatio = 1.5f;
    private const float MinimumFlux = 0.05f;
    private const float Compression = 100f;
    private const int WarmupHops = 20;        // 200 ms before onsets are reported
    private const float StatsAlpha = 0.02f;   // ~0.5 s memory at 10 ms hops

    private readonly MelFilterbank _mel;
    private readonly float[] _previousLog; // compressed magnitude per bin
    private bool _hasPrevious;
    private float _fluxMean;
    private float _fluxVar;
    private int _hops;

    public SpectralSignal(int fftSize, int sampleRate, int melBands = 64)
    {
        _mel = new MelFilterbank(melBands, fftSize, sampleRate);
        _previousLog = new float[(fftSize / 2) + 1];

        var names = new List<string> { "spectral.flux", "spectral.onset", "spectral.centroid_hz" };
        for (int b = 0; b < melBands; b++)
        {
            names.Add(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"mel.{b:00}"));
        }

        Names = names;
    }

    public IReadOnlyList<string> Names { get; }

    public void Process(AudioBlock block, SpectrumFrame spectrum, Span<float> output)
    {
        ArgumentNullException.ThrowIfNull(spectrum);
        ReadOnlySpan<float> power = spectrum.Power;

        double flux = 0, weighted = 0, total = 0;
        double hzPerBin = (double)spectrum.SampleRate / spectrum.FftSize;
        for (int k = 0; k < power.Length; k++)
        {
            float log = MathF.Log10(1 + (Compression * MathF.Sqrt(power[k])));
            if (_hasPrevious)
            {
                flux += Math.Max(0, log - _previousLog[k]);
            }

            _previousLog[k] = log;
            weighted += power[k] * k * hzPerBin;
            total += power[k];
        }

        float f = _hasPrevious ? (float)(flux / power.Length) : 0;
        _hasPrevious = true;

        // Onset when flux clears the running mean by k standard deviations; update stats afterwards.
        float threshold = Math.Max(_fluxMean + (ThresholdK * MathF.Sqrt(_fluxVar)), _fluxMean * ThresholdRatio);
        bool onset = _hops >= WarmupHops && f > threshold && f > MinimumFlux;
        _hops++;
        float d = f - _fluxMean;
        _fluxMean += StatsAlpha * d;
        _fluxVar = (1 - StatsAlpha) * (_fluxVar + (StatsAlpha * d * d));

        output[0] = f;
        output[1] = onset ? 1 : 0;
        output[2] = total > 1e-12 ? (float)(weighted / total) : 0;
        _mel.Apply(power, output[3..]);
    }

    public void Reset()
    {
        _hasPrevious = false;
        _fluxMean = 0;
        _fluxVar = 0;
        _hops = 0;
    }
}
