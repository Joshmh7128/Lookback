using Lookback.Clipper.Curve;
using Lookback.Contracts.V1;

namespace Lookback.Clipper.Candidates;

/// <summary>One reason to clip: a game-profile event or a curve peak, with its window.</summary>
internal sealed class Contribution
{
    public required long Start { get; init; }

    public required long End { get; init; }

    public required long At { get; init; }

    /// <summary>What it adds to the candidate's raw score.</summary>
    public required double Score { get; init; }

    public required string Profile { get; init; }

    public GameEvent? Event { get; init; }

    public CurvePeak? Peak { get; init; }
}

/// <summary>Contributions whose windows overlap or nearly touch: one candidate.</summary>
internal sealed class CandidateGroup
{
    public CandidateGroup(Contribution first)
    {
        Start = first.Start;
        End = first.End;
        Items.Add(first);
    }

    /// <summary>A highlight window: [<paramref name="start"/>, <paramref name="end"/>] with the moments in it.</summary>
    public CandidateGroup(long start, long end, IEnumerable<Contribution> items)
    {
        Start = start;
        End = end;
        Items.AddRange(items);
    }

    public long Start { get; private set; }

    public long End { get; private set; }

    public List<Contribution> Items { get; } = [];

    public void Include(Contribution c)
    {
        Start = Math.Min(Start, c.Start);
        End = Math.Max(End, c.End);
        Items.Add(c);
    }

    public void Absorb(CandidateGroup other)
    {
        Start = Math.Min(Start, other.Start);
        End = Math.Max(End, other.End);
        Items.AddRange(other.Items);
    }

    /// <summary>Clamps the end (to the last time seen, at the end of a session).</summary>
    public void ClampEnd(long end) => End = Math.Max(Start, Math.Min(End, end));
}

/// <summary>
/// Fuses event windows and curve-peak windows into one deduplicated set of
/// candidates. A contribution joins every open group it overlaps or comes
/// within <c>gap</c> of, as long as the result stays within the maximum
/// length; otherwise it starts its own group. A group is closed once the
/// caller's <i>horizon</i>, the earliest start any contribution not yet seen
/// can have, has passed its end plus the gap: nothing can join it any more.
/// </summary>
internal sealed class CandidateFuser
{
    private readonly long _gap;
    private readonly long _maxLength;
    private readonly List<CandidateGroup> _open = [];
    private readonly List<CandidateGroup> _touching = [];

    public CandidateFuser(long gap, long maxLength)
    {
        _gap = gap;
        _maxLength = maxLength;
    }

    public int Open => _open.Count;

    public void Add(Contribution c)
    {
        ArgumentNullException.ThrowIfNull(c);
        _touching.Clear();
        long start = c.Start, end = c.End;
        foreach (CandidateGroup g in _open)
        {
            if (c.Start <= g.End + _gap && c.End >= g.Start - _gap)
            {
                _touching.Add(g);
                start = Math.Min(start, g.Start);
                end = Math.Max(end, g.End);
            }
        }

        if (_touching.Count == 0 || end - start > _maxLength)
        {
            _open.Add(new CandidateGroup(c));
            return;
        }

        CandidateGroup into = _touching[0];
        for (int i = 1; i < _touching.Count; i++)
        {
            into.Absorb(_touching[i]);
            _open.Remove(_touching[i]);
        }

        into.Include(c);
    }

    /// <summary>Moves every group nothing can join any more to <paramref name="closed"/>, earliest first.</summary>
    public void Close(long horizon, List<CandidateGroup> closed)
    {
        ArgumentNullException.ThrowIfNull(closed);
        int before = closed.Count;
        for (int i = 0; i < _open.Count; i++)
        {
            if (_open[i].End + _gap < horizon)
            {
                closed.Add(_open[i]);
                _open.RemoveAt(i--);
            }
        }

        if (closed.Count - before > 1)
        {
            closed.Sort(before, closed.Count - before, Comparer<CandidateGroup>.Create((a, b) => a.Start.CompareTo(b.Start)));
        }
    }

    public void CloseAll(List<CandidateGroup> closed) => Close(long.MaxValue, closed);
}
