namespace Lookback.Clipper.Curve;

/// <summary>A peak of the excitement curve, with its clip window.</summary>
internal sealed class CurvePeak
{
    public required long At { get; set; }

    public required float Value { get; set; }

    public required long Start { get; set; }

    public required long End { get; set; }

    /// <summary>The profile active at the peak; its settings decide the window and the score.</summary>
    public required CurveProfile Profile { get; set; }

    /// <summary>Clamped z-score of each component at the peak.</summary>
    public required float[] Z { get; set; }

    /// <summary>Peaks folded into this one by the refractory period, itself included.</summary>
    public int Count { get; set; } = 1;

    /// <summary>The latest folded peak; the refractory period runs from here.</summary>
    public long LastAt { get; set; }
}

/// <summary>
/// Finds peaks of the smoothed curve with hysteresis: a peak starts when the
/// curve reaches <c>high</c> and ends when it falls below <c>low</c>; its time
/// is the maximum in between. A peak within the refractory period of the
/// previous one is folded into it (one moment, the stronger value, the
/// window stretched to cover both), so a burst of spikes is one peak. A peak
/// is handed on once its refractory period has passed with nothing new.
/// Allocates only when a peak ends.
/// </summary>
internal sealed class PeakDetector
{
    private readonly float[] _bestZ;
    private readonly long _maxLength;
    private bool _active;
    private long _activeStart;
    private float _best;
    private long _bestAt;
    private CurveProfile? _bestProfile;
    private CurvePeak? _pending;

    public PeakDetector(int components, long maxLength)
    {
        _bestZ = new float[components];
        _maxLength = maxLength;
    }

    /// <summary>True while the curve is above <c>low</c> after reaching <c>high</c>.</summary>
    public bool Active => _active;

    public long ActiveStart => _activeStart;

    /// <summary>The peak waiting out its refractory period, if any.</summary>
    public CurvePeak? Pending => _pending;

    /// <summary>One curve sample. Returns a peak that is finished (refractory period over), or null.</summary>
    public CurvePeak? Step(long t, float value, CurveProfile profile, ReadOnlySpan<float> z)
    {
        ArgumentNullException.ThrowIfNull(profile);
        CurvePeak? done = null;
        if (!_active)
        {
            if (value >= profile.High)
            {
                _active = true;
                _activeStart = t;
                Track(t, value, profile, z);
            }
        }
        else
        {
            if (value > _best)
            {
                Track(t, value, profile, z);
            }

            if (value < profile.Low)
            {
                _active = false;
                done = Complete();
            }
        }

        if (done is null && !_active && _pending is not null && t - _pending.LastAt > _pending.Profile.Refractory)
        {
            done = _pending;
            _pending = null;
        }

        return done;
    }

    /// <summary>Ends any peak in progress and hands on everything (end of a session).</summary>
    public void Flush(List<CurvePeak> output)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (_active)
        {
            _active = false;
            if (Complete() is { } previous)
            {
                output.Add(previous);
            }
        }

        if (_pending is not null)
        {
            output.Add(_pending);
            _pending = null;
        }
    }

    private void Track(long t, float value, CurveProfile profile, ReadOnlySpan<float> z)
    {
        _best = value;
        _bestAt = t;
        _bestProfile = profile;
        z.CopyTo(_bestZ);
    }

    /// <summary>Finishes the active peak: folds it into the pending one, or makes it pending and returns the old one.</summary>
    private CurvePeak? Complete()
    {
        CurveProfile p = _bestProfile!;
        long start = Math.Max(0, _bestAt - p.Pre), end = _bestAt + p.Post;
        CurvePeak? pending = _pending;
        if (pending is not null && _bestAt - pending.LastAt <= pending.Profile.Refractory
            && Math.Max(end, pending.End) - Math.Min(start, pending.Start) <= _maxLength)
        {
            pending.Start = Math.Min(start, pending.Start);
            pending.End = Math.Max(end, pending.End);
            pending.LastAt = _bestAt;
            pending.Count++;
            if (_best > pending.Value)
            {
                pending.Value = _best;
                pending.At = _bestAt;
                pending.Profile = p;
                pending.Z = _bestZ.ToArray();
            }

            return null;
        }

        _pending = new CurvePeak { At = _bestAt, Value = _best, Start = start, End = end, Profile = p, Z = _bestZ.ToArray(), LastAt = _bestAt };
        return pending;
    }
}
