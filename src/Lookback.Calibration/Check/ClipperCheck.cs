using Lookback.Contracts.V1;
using Lookback.Core.Time;
using Lookback.Scoring.Ratings;

namespace Lookback.Calibration.Check;

/// <summary>A clipper candidate as the check shows it. <paramref name="Saved"/> is null until a rating was saved or refused.</summary>
public sealed record CheckCandidate(string Id, long StartQpc, long EndQpc, long PeakQpc, float Score, IReadOnlyList<string> Reasons, bool QaPassed, int Stars, bool? Saved = null);

/// <summary>One reason the clipper gave (an event name, or <c>excitement</c> for the curve), with how its candidates were judged.</summary>
public sealed record ReasonScore(string Reason, int Candidates, int Judged, double? MeanStars, double? Precision);

/// <summary>Candidates in one heuristic-score band, with how they were judged.</summary>
public sealed record ScoreBand(string Band, int Candidates, int Judged, double? MeanStars);

/// <summary>
/// How the clipper did on what was watched: <see cref="Precision"/> is the
/// share of judged candidates rated 4 or 5; <see cref="Recall"/> is the share
/// of highlights it found, where highlights are candidates rated 4 or 5 plus
/// moments marked as missed that no 4–5 candidate covers, and a marked moment
/// counts as found when any candidate covers it.
/// </summary>
public sealed record CheckStats(
    int Proposed, int Judged, int Good, int Fair, int Poor, double? Precision,
    int MissedMarks, int MarksFound, int MarksNotFound, double? Recall, double? MeanStars,
    IReadOnlyList<ReasonScore> ByReason, IReadOnlyList<ScoreBand> ByScore);

/// <summary>
/// The clipper check: rate the clipper's candidates on a video you are only
/// watching (an official YouTube or Twitch player) and see how accurate it
/// is. The check itself lives in memory and is dropped when the player stops
/// or the check is reset. What may leave it is your rating, through
/// <see cref="RatingFor"/> and <c>RatingStore.AppendCheck</c>: the stars and the
/// candidate's numbers, under a random run id, never a time or the video.
/// </summary>
public sealed class ClipperCheck
{
    public const int MaxCandidates = 2000;
    public const int MaxMarks = 2000;

    private readonly Lock _lock = new();
    private readonly Dictionary<string, Entry> _candidates = new(StringComparer.Ordinal);
    private readonly List<string> _order = [];
    private readonly List<long> _marks = [];
    private readonly long _tolerance;
    private int _next;

    /// <param name="frequency">QPC ticks per second of the session clock.</param>
    /// <param name="tolerance">How far outside a candidate's window a marked moment still counts as covered. 2 s by default.</param>
    public ClipperCheck(long frequency, TimeSpan? tolerance = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frequency);
        Frequency = frequency;
        _tolerance = QpcTime.FromTimeSpan(tolerance ?? TimeSpan.FromSeconds(2), frequency);
    }

    public long Frequency { get; }

    /// <summary>Random per run (<c>check-</c> and 32 hex digits); a reset starts a new run. Saved ratings carry it instead of anything from the video.</summary>
    public string RunId { get; private set; } = RatingStore.NewRunId();

    /// <summary>A candidate the clipper proposed, its QA report and the clipper's named features.</summary>
    public void Add(ClipCandidate candidate, QaReport? qa, IReadOnlyDictionary<string, float>? features = null)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        lock (_lock)
        {
            if (_candidates.ContainsKey(candidate.CandidateId) || _candidates.Count >= MaxCandidates)
            {
                return;
            }

            _candidates[candidate.CandidateId] = new Entry(candidate, qa?.Passed ?? true, _next++, features);
            _order.Add(candidate.CandidateId);
        }
    }

    /// <summary>
    /// What may be saved of a rating: the stars, the candidate's heuristic score
    /// and features, its place in this run and the run id. Null when the check
    /// has no such candidate. <paramref name="profile"/> applies when the
    /// candidate names none; <paramref name="videoOnly"/> stores the audio
    /// features as missing (the player's sound never reached the analysis).
    /// </summary>
    public CheckRatingInput? RatingFor(string candidateId, int stars, string platform, string? profile, bool videoOnly = true)
    {
        lock (_lock)
        {
            if (!_candidates.TryGetValue(candidateId, out Entry? e))
            {
                return null;
            }

            return new CheckRatingInput
            {
                RunId = RunId,
                Ordinal = e.Ordinal,
                Profile = e.Candidate.GameProfile is { Length: > 0 } p ? p : profile,
                Platform = platform,
                Stars = stars,
                HeuristicScore = e.Candidate.HeuristicScore,
                ClipperFeatures = e.Features,
                VideoOnly = videoOnly,
            };
        }
    }

    /// <summary>Records whether the candidate's latest rating was saved.</summary>
    public void MarkSaved(string candidateId, bool saved)
    {
        lock (_lock)
        {
            if (_candidates.TryGetValue(candidateId, out Entry? e))
            {
                e.Saved = saved;
            }
        }
    }

    /// <summary>Rates a candidate 1 to 5 (0 clears). False if the check has no such candidate.</summary>
    public bool Judge(string candidateId, int stars)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(stars, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(stars, 5);
        lock (_lock)
        {
            if (!_candidates.TryGetValue(candidateId, out Entry? e))
            {
                return false;
            }

            e.Stars = stars;
            return true;
        }
    }

    /// <summary>A moment the user saw as a highlight, at <paramref name="qpc"/> (to check whether the clipper finds it).</summary>
    public void MarkMissed(long qpc)
    {
        lock (_lock)
        {
            if (_marks.Count < MaxMarks)
            {
                _marks.Add(qpc);
            }
        }
    }

    /// <summary>Forgets everything and starts a new run. Ratings already saved stay saved.</summary>
    public void Reset()
    {
        lock (_lock)
        {
            _candidates.Clear();
            _order.Clear();
            _marks.Clear();
            _next = 0;
            RunId = RatingStore.NewRunId();
        }
    }

    /// <summary>Takes a candidate out of the check (a long one whose highlights stand in for it).</summary>
    public bool Remove(string candidateId)
    {
        lock (_lock)
        {
            if (!_candidates.Remove(candidateId, out Entry? gone))
            {
                return false;
            }

            _order.Remove(candidateId);
            if (gone.Ordinal == _next - 1)
            {
                _next--; // its highlights take its number: a run's saved ids stay c0, c1… in order
            }

            return true;
        }
    }

    /// <summary>One candidate as the check shows it, or null.</summary>
    public CheckCandidate? Get(string candidateId)
    {
        lock (_lock)
        {
            return _candidates.TryGetValue(candidateId, out Entry? e) ? e.View() : null;
        }
    }

    /// <summary>The newest <paramref name="count"/> candidates, newest first.</summary>
    public IReadOnlyList<CheckCandidate> Recent(int count)
    {
        lock (_lock)
        {
            return [.. Enumerable.Reverse(_order).Take(count).Select(id => _candidates[id].View())];
        }
    }

    public CheckStats Stats()
    {
        lock (_lock)
        {
            List<Entry> all = [.. _order.Select(id => _candidates[id])];
            List<Entry> judged = [.. all.Where(e => e.Stars > 0)];
            int good = judged.Count(e => e.Stars >= 4);
            int fair = judged.Count(e => e.Stars == 3);
            int poor = judged.Count(e => e.Stars <= 2);

            int inGood = 0, found = 0, notFound = 0;
            foreach (long m in _marks)
            {
                if (all.Any(e => e.Stars >= 4 && Covers(e, m)))
                {
                    inGood++; // already counted as a good candidate
                }
                else if (all.Any(e => Covers(e, m)))
                {
                    found++;
                }
                else
                {
                    notFound++;
                }
            }

            int highlights = good + found + notFound;
            var reasons = all
                .SelectMany(e => e.ReasonNames.Select(r => (Reason: r, Entry: e)))
                .GroupBy(x => x.Reason, StringComparer.Ordinal)
                .Select(g =>
                {
                    List<Entry> j = [.. g.Select(x => x.Entry).Where(e => e.Stars > 0)];
                    return new ReasonScore(g.Key, g.Count(), j.Count, Mean(j), j.Count == 0 ? null : (double)j.Count(e => e.Stars >= 4) / j.Count);
                })
                .OrderByDescending(r => r.Candidates)
                .ThenBy(r => r.Reason, StringComparer.Ordinal)
                .ToList();
            (string Name, float Lo, float Hi)[] bands = [("0.00–0.33", 0f, 0.33f), ("0.33–0.66", 0.33f, 0.66f), ("0.66–1.00", 0.66f, float.MaxValue)];
            var byScore = bands.Select(b =>
            {
                List<Entry> inBand = [.. all.Where(e => e.Candidate.HeuristicScore >= b.Lo && e.Candidate.HeuristicScore < b.Hi)];
                List<Entry> j = [.. inBand.Where(e => e.Stars > 0)];
                return new ScoreBand(b.Name, inBand.Count, j.Count, Mean(j));
            }).ToList();

            return new CheckStats(
                all.Count, judged.Count, good, fair, poor,
                judged.Count == 0 ? null : (double)good / judged.Count,
                _marks.Count, found + inGood, notFound,
                highlights == 0 ? null : (double)(good + found) / highlights,
                Mean(judged), reasons, byScore);
        }
    }

    private bool Covers(Entry e, long qpc) => qpc >= (long)e.Candidate.StartQpc - _tolerance && qpc <= (long)e.Candidate.EndQpc + _tolerance;

    private static double? Mean(List<Entry> judged) => judged.Count == 0 ? null : judged.Average(e => (double)e.Stars);

    private sealed class Entry(ClipCandidate candidate, bool qaPassed, int ordinal, IReadOnlyDictionary<string, float>? features)
    {
        public ClipCandidate Candidate { get; } = candidate;

        public bool QaPassed { get; } = qaPassed;

        public int Ordinal { get; } = ordinal;

        public IReadOnlyDictionary<string, float>? Features { get; } = features;

        public int Stars { get; set; }

        public bool? Saved { get; set; }

        /// <summary>"impact ×1 (best confidence 0.70)" → "impact"; "excitement ×1 (…)" → "excitement".</summary>
        public IEnumerable<string> ReasonNames => Candidate.Reasons
            .Select(r => r.Split(['×', '('], 2)[0].Trim())
            .Where(r => r.Length > 0)
            .Distinct(StringComparer.Ordinal);

        public CheckCandidate View() => new(Candidate.CandidateId, (long)Candidate.StartQpc, (long)Candidate.EndQpc, (long)Candidate.PeakQpc,
            Candidate.HeuristicScore, [.. Candidate.Reasons], QaPassed, Stars, Saved);
    }
}
