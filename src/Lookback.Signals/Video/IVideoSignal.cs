using Lookback.Media.Frames;

namespace Lookback.Signals.Video;

/// <summary>
/// A game-agnostic feature computed from each analysis frame. Implementations
/// keep whatever history they need, write exactly <see cref="Names"/>.Count
/// values per frame, and must not allocate in <see cref="Process"/>.
/// </summary>
public interface IVideoSignal
{
    IReadOnlyList<string> Names { get; }

    void Process(VideoFrame frame, LumaPlane luma, Span<float> output);

    /// <summary>Forget history, for example at a new session.</summary>
    void Reset();
}
