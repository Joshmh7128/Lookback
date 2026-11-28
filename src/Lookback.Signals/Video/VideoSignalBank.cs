using Lookback.Media.Frames;

namespace Lookback.Signals.Video;

/// <summary>
/// Runs every video signal on each analysis frame and lays their outputs
/// side by side in one vector, so one frame produces one
/// <c>SignalFrame</c>. Allocation-free per frame.
/// </summary>
public sealed class VideoSignalBank
{
    private readonly IVideoSignal[] _signals;
    private readonly int[] _offsets;
    private readonly LumaPlane _luma;
    private readonly float[] _values;

    public VideoSignalBank(AnalysisFormat format, IEnumerable<IVideoSignal> signals)
    {
        ArgumentNullException.ThrowIfNull(format);
        ArgumentNullException.ThrowIfNull(signals);
        _signals = [.. signals];
        _luma = new LumaPlane(format.Width, format.Height);
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

    /// <summary>
    /// The standard set (luma, motion, flash, scene cut, HUD with a 10 s
    /// warm-up, colour grid), followed by any <paramref name="extra"/> signals such as a
    /// game profile's regions.
    /// </summary>
    public static VideoSignalBank CreateDefault(AnalysisFormat format, IEnumerable<IVideoSignal>? extra = null)
    {
        ArgumentNullException.ThrowIfNull(format);
        IVideoSignal[] standard =
        [
            new LumaStatsSignal(),
            new MotionSignal(format.FramesPerSecond),
            new FlashSignal(),
            new SceneCutSignal(),
            new HudSignal(format.Width * format.Height, warmupFrames: format.FramesPerSecond * 10),
            new ColorGridSignal(format.Width, format.Height),
        ];
        return new VideoSignalBank(format, extra is null ? standard : [.. standard, .. extra]);
    }

    /// <summary>The signals in this bank, in output order.</summary>
    public IReadOnlyList<IVideoSignal> Signals => _signals;

    /// <summary>Signal names, in output order.</summary>
    public IReadOnlyList<string> Names { get; }

    /// <summary>Computes every signal for <paramref name="frame"/>. Valid until the next call.</summary>
    public ReadOnlySpan<float> Process(VideoFrame frame)
    {
        _luma.Compute(frame);
        for (int i = 0; i < _signals.Length; i++)
        {
            _signals[i].Process(frame, _luma, _values.AsSpan(_offsets[i], _signals[i].Names.Count));
        }

        return _values;
    }

    public void Reset()
    {
        foreach (IVideoSignal s in _signals)
        {
            s.Reset();
        }
    }
}
