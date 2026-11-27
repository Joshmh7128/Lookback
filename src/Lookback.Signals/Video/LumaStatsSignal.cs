using Lookback.Media.Frames;

namespace Lookback.Signals.Video;

/// <summary>
/// Brightness and contrast, and a black-frame flag. Black frames mark loading
/// screens, cuts to black and capture failures; QA rejects clips full of them.
/// </summary>
public sealed class LumaStatsSignal : IVideoSignal
{
    public const float BlackMeanThreshold = 0.06f;
    public const float BlackStdDevThreshold = 0.03f;

    public IReadOnlyList<string> Names { get; } = ["luma.mean", "luma.std", "luma.black"];

    public void Process(VideoFrame frame, LumaPlane luma, Span<float> output)
    {
        ArgumentNullException.ThrowIfNull(luma);
        output[0] = luma.Mean;
        output[1] = luma.StdDev;
        output[2] = luma.Mean < BlackMeanThreshold && luma.StdDev < BlackStdDevThreshold ? 1f : 0f;
    }

    public void Reset()
    {
    }
}
