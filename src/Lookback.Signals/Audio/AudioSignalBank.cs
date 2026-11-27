using Lookback.Media.Frames;

namespace Lookback.Signals.Audio;

/// <summary>Runs every audio signal on each hop; one output vector per hop. Allocation-free per hop.</summary>
public sealed class AudioSignalBank
{
    private readonly IAudioSignal[] _signals;
    private readonly int[] _offsets;
    private readonly SpectrumFrame _spectrum;
    private readonly float[] _values;

    public AudioSignalBank(int fftSize, int sampleRate, IEnumerable<IAudioSignal> signals)
    {
        ArgumentNullException.ThrowIfNull(signals);
        _signals = [.. signals];
        _spectrum = new SpectrumFrame(fftSize, sampleRate);
        _offsets = new int[_signals.Length];
        var names = new List<string>();
        for (int i = 0; i < _signals.Length; i++)
        {
            _offsets[i] = names.Count;
            names.AddRange(_signals[i].Names);
        }

        Names = names;
        _values = new float[names.Count];
    }

    /// <summary>Loudness plus spectral signals with a 1024-point FFT and 64 mel bands.</summary>
    public static AudioSignalBank CreateDefault(AnalysisFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);
        const int fft = 1024;
        return new AudioSignalBank(fft, format.SampleRate, [new LoudnessSignal(), new SpectralSignal(fft, format.SampleRate)]);
    }

    public IReadOnlyList<string> Names { get; }

    public ReadOnlySpan<float> Process(AudioBlock block)
    {
        _spectrum.Update(block);
        for (int i = 0; i < _signals.Length; i++)
        {
            _signals[i].Process(block, _spectrum, _values.AsSpan(_offsets[i], _signals[i].Names.Count));
        }

        return _values;
    }

    public void Reset()
    {
        _spectrum.Reset();
        foreach (IAudioSignal s in _signals)
        {
            s.Reset();
        }
    }
}
