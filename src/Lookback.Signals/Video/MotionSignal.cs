using Lookback.Media.Frames;

namespace Lookback.Signals.Video;

/// <summary>
/// Frame-to-frame change. <c>motion.energy</c> is the mean absolute luma
/// difference in [0, 1]; <c>motion.coverage</c> is the fraction of pixels
/// that changed noticeably, which separates a camera whip (high coverage)
/// from a small muzzle flash (low coverage, possibly high energy).
/// <c>motion.frozen_s</c> counts how long the picture has been effectively
/// static: frozen video is a capture or encode fault, not a highlight.
/// </summary>
public sealed class MotionSignal : IVideoSignal
{
    public const int ChangedPixelThreshold = 12; // ~5% of full scale, above compression noise
    public const float FrozenEnergyThreshold = 0.002f;

    private readonly float _secondsPerFrame;
    private byte[]? _previous;
    private int _frozenFrames;

    public MotionSignal(int framesPerSecond)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(framesPerSecond);
        _secondsPerFrame = 1f / framesPerSecond;
    }

    public IReadOnlyList<string> Names { get; } = ["motion.energy", "motion.coverage", "motion.frozen_s"];

    public void Process(VideoFrame frame, LumaPlane luma, Span<float> output)
    {
        ArgumentNullException.ThrowIfNull(luma);
        ReadOnlySpan<byte> current = luma.Values;

        if (_previous is null)
        {
            _previous = current.ToArray(); // first frame only
            output[0] = output[1] = output[2] = 0;
            return;
        }

        long diffSum = 0;
        int changed = 0;
        for (int i = 0; i < current.Length; i++)
        {
            int d = Math.Abs(current[i] - _previous[i]);
            diffSum += d;
            if (d > ChangedPixelThreshold)
            {
                changed++;
            }
        }

        current.CopyTo(_previous);
        float energy = (float)(diffSum / (255.0 * current.Length));
        _frozenFrames = energy < FrozenEnergyThreshold ? _frozenFrames + 1 : 0;

        output[0] = energy;
        output[1] = (float)changed / current.Length;
        output[2] = _frozenFrames * _secondsPerFrame;
    }

    public void Reset()
    {
        _previous = null;
        _frozenFrames = 0;
    }
}
