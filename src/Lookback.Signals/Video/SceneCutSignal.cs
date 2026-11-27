using Lookback.Media.Frames;

namespace Lookback.Signals.Video;

/// <summary>
/// Histogram distance between consecutive frames, in [0, 1]. Camera motion
/// keeps the luma histogram roughly the same; a cut, a death cam, a respawn or
/// a menu changes it wholesale. Half the L1 distance between normalised
/// histograms is the fraction of pixels that would have to change bins.
/// </summary>
public sealed class SceneCutSignal : IVideoSignal
{
    private readonly int[] _previous = new int[LumaPlane.HistogramBins];
    private bool _hasPrevious;

    public IReadOnlyList<string> Names { get; } = ["scene.cut"];

    public void Process(VideoFrame frame, LumaPlane luma, Span<float> output)
    {
        ArgumentNullException.ThrowIfNull(luma);
        ReadOnlySpan<int> hist = luma.Histogram;

        if (!_hasPrevious)
        {
            hist.CopyTo(_previous);
            _hasPrevious = true;
            output[0] = 0;
            return;
        }

        long l1 = 0;
        for (int i = 0; i < hist.Length; i++)
        {
            l1 += Math.Abs(hist[i] - _previous[i]);
        }

        hist.CopyTo(_previous);
        output[0] = (float)(l1 / (2.0 * luma.PixelCount));
    }

    public void Reset() => _hasPrevious = false;
}
