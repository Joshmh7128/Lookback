using Lookback.Media.Frames;

namespace Lookback.Signals.Audio;

/// <summary>Audio counterpart of <see cref="Video.IVideoSignal"/>: one output vector per hop.</summary>
public interface IAudioSignal
{
    IReadOnlyList<string> Names { get; }

    void Process(AudioBlock block, SpectrumFrame spectrum, Span<float> output);

    void Reset();
}
