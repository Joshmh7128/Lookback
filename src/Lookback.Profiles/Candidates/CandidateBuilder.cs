using System.Globalization;
using Lookback.Contracts.V1;
using Lookback.Core.Time;

namespace Lookback.Profiles.Candidates;

/// <summary>
/// Turns a stream of game events into clip candidates. Events whose clip
/// windows overlap (or nearly touch) become one candidate covering all of
/// them, up to a maximum length, so a team fight with five eliminations is
/// one clip rather than five overlapping ones.
/// <list type="bullet">
/// <item><b>Score</b>: the sum of weight × confidence over the events inside.</item>
/// <item><b>Peak</b>: the event that contributed most.</item>
/// <item><b>Highlight probability</b>: 1 − e^(−score). A placeholder until
/// ratings calibrate it (M4); monotonic in the score and bounded to 0..1.</item>
/// </list>
/// </summary>
public sealed class CandidateBuilder
{
    private readonly string _sessionId;
    private readonly long _gap;
    private readonly long _maxLength;
    private Open? _open;
    private int _count;

    /// <param name="sessionId">Prefix for candidate ids.</param>
    /// <param name="frequency">Ticks per second of the timestamps.</param>
    /// <param name="gapMs">Windows closer than this are merged.</param>
    /// <param name="maxLengthMs">A candidate never grows past this.</param>
    public CandidateBuilder(string sessionId, long frequency, int gapMs = 1500, int maxLengthMs = 60_000)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frequency);
        _sessionId = sessionId;
        _gap = QpcTime.Rescale(gapMs, 1000, frequency);
        _maxLength = QpcTime.Rescale(maxLengthMs, 1000, frequency);
    }

    /// <summary>Adds an event. Candidates that can no longer grow are appended to <paramref name="closed"/>.</summary>
    public void Add(GameEvent e, List<ClipCandidate> closed)
    {
        ArgumentNullException.ThrowIfNull(e);
        ArgumentNullException.ThrowIfNull(closed);
        long start = (long)e.WindowStartQpc, end = (long)e.WindowEndQpc;
        if (_open is not null)
        {
            bool touches = start <= _open.End + _gap && end >= _open.Start - _gap;
            bool fits = Math.Max(end, _open.End) - Math.Min(start, _open.Start) <= _maxLength;
            if (touches && fits)
            {
                _open.Include(e);
                return;
            }

            closed.Add(Close());
        }

        _open = new Open(e);
    }

    /// <summary>Closes the candidate still open (end of a session).</summary>
    public void Flush(List<ClipCandidate> closed)
    {
        ArgumentNullException.ThrowIfNull(closed);
        if (_open is not null)
        {
            closed.Add(Close());
        }
    }

    private ClipCandidate Close()
    {
        Open o = _open!;
        _open = null;
        var c = new ClipCandidate
        {
            CandidateId = string.Create(CultureInfo.InvariantCulture, $"{_sessionId}-{++_count:0000}"),
            StartQpc = (ulong)o.Start,
            EndQpc = (ulong)o.End,
            PeakQpc = (ulong)o.PeakAt,
            HeuristicScore = (float)o.Score,
            HighlightProbability = (float)(1 - Math.Exp(-o.Score)),
            GameProfile = o.Profile,
        };
        c.Reasons.AddRange(o.Events
            .GroupBy(x => x.Name, StringComparer.Ordinal)
            .Select(g => string.Create(CultureInfo.InvariantCulture, $"{g.Key} ×{g.Count()} (best confidence {g.Max(x => x.Confidence):0.00})")));
        return c;
    }

    private sealed class Open
    {
        private double _peakValue = double.MinValue;

        public Open(GameEvent first)
        {
            Start = (long)first.WindowStartQpc;
            End = (long)first.WindowEndQpc;
            Profile = first.GameProfile;
            Include(first);
        }

        public long Start { get; private set; }

        public long End { get; private set; }

        public long PeakAt { get; private set; }

        public double Score { get; private set; }

        public string Profile { get; private set; }

        public List<GameEvent> Events { get; } = [];

        public void Include(GameEvent e)
        {
            Start = Math.Min(Start, (long)e.WindowStartQpc);
            End = Math.Max(End, (long)e.WindowEndQpc);
            double v = e.Weight * e.Confidence;
            Score += v;
            if (v > _peakValue)
            {
                _peakValue = v;
                PeakAt = (long)e.AtQpc;
                Profile = e.GameProfile; // the candidate belongs to the profile of its strongest moment
            }

            Events.Add(e);
        }
    }
}
