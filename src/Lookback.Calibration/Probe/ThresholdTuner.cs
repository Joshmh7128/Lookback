using Lookback.Core.Time;
using Lookback.Profiles.Model;

namespace Lookback.Calibration.Probe;

/// <summary>Identifies one condition of one rule: the profile that defines the rule, the event, and the condition's index in <c>when</c> then <c>whenAny</c>.</summary>
public sealed record ConditionKey(string Owner, string Event, int Index);

/// <summary>A proposed threshold for one condition, and how it would have done on this session.</summary>
public sealed record ThresholdProposal(ConditionKey Key, string Signal, string Kind, float Current, float Proposed, int Marks, int Caught, double FalsePerMinute, double CurrentFalsePerMinute, int CurrentCaught);

/// <summary>
/// Keeps, in memory, the statistic each video condition of the active
/// profile is judged on (the value for <c>above</c>, minus the value for
/// <c>below</c>, the value over its slow baseline for <c>rise</c>, exactly as
/// the detector computes it), for up to 30 minutes. Given the user's marks of
/// an event it searches for the threshold that catches the marked moments
/// with the fewest firings elsewhere. Allocation-free per frame.
/// </summary>
public sealed class ThresholdTuner
{
    private const int MaxFrames = 30 * 60 * 30;

    private readonly long _frequency;
    private readonly Tracked[] _tracked;
    private readonly long[] _qpc = new long[MaxFrames];
    private readonly Lock _gate = new();
    private readonly List<(string Event, long Qpc)> _marks = [];
    private int _head;
    private int _count;

    private sealed class Tracked(ConditionKey key, Condition condition, int signalIndex, int cooldownMs)
    {
        public ConditionKey Key { get; } = key;

        public Condition Condition { get; } = condition;

        public int SignalIndex { get; } = signalIndex;

        public int CooldownMs { get; } = cooldownMs;

        public float[] Stat { get; } = new float[MaxFrames];

        public float Baseline { get; set; } = float.NaN;
    }

    /// <param name="rules">The rules to track, each with the profile that defines it.</param>
    /// <param name="signalNames">Names of the per-frame video values, in order.</param>
    /// <param name="frequency">Ticks per second of the timestamps.</param>
    public ThresholdTuner(IEnumerable<(string Owner, EventRule Rule)> rules, IReadOnlyList<string> signalNames, long frequency)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(signalNames);
        _frequency = frequency;
        var index = signalNames.Select((n, i) => (n, i)).ToDictionary(x => x.n, x => x.i, StringComparer.Ordinal);
        var tracked = new List<Tracked>();
        foreach ((string owner, EventRule rule) in rules)
        {
            int i = 0;
            foreach (Condition c in rule.When.Concat(rule.WhenAny))
            {
                if (index.TryGetValue(c.Signal, out int s))
                {
                    tracked.Add(new Tracked(new ConditionKey(owner, rule.Name, i), c, s, rule.CooldownMs));
                }

                i++;
            }
        }

        _tracked = [.. tracked];
    }

    public IEnumerable<ConditionKey> Conditions => _tracked.Select(t => t.Key);

    /// <summary>One video frame's values.</summary>
    public void OnFrame(long qpc, ReadOnlySpan<float> values)
    {
        lock (_gate)
        {
            foreach (Tracked t in _tracked)
            {
                float v = values[t.SignalIndex];
                Condition c = t.Condition;
                float stat;
                if (c.Rise is not null)
                {
                    float b = float.IsNaN(t.Baseline) ? v : t.Baseline;
                    stat = v - b;
                    t.Baseline = b + (c.BaselineAlpha * (v - b));
                }
                else
                {
                    stat = c.Below is not null ? -v : v;
                }

                t.Stat[_head] = stat;
            }

            _qpc[_head] = qpc;
            _head = (_head + 1) % MaxFrames;
            _count = Math.Min(_count + 1, MaxFrames);
        }
    }

    public void Mark(string eventName, long qpc)
    {
        lock (_gate)
        {
            _marks.Add((eventName, qpc));
        }
    }

    /// <summary>Proposals for every tracked condition of <paramref name="eventName"/>; empty with fewer than <paramref name="minMarks"/> marks.</summary>
    public IReadOnlyList<ThresholdProposal> Propose(string eventName, int minMarks = 3)
    {
        lock (_gate)
        {
            long lead = QpcTime.FromTimeSpan(CalibrationProbe.MarkLead, _frequency), lag = QpcTime.FromTimeSpan(CalibrationProbe.MarkLag, _frequency);
            long newest = _count == 0 ? 0 : _qpc[(_head - 1 + MaxFrames) % MaxFrames];
            long[] marks = [.. _marks.Where(m => m.Event == eventName && m.Qpc + lag <= newest).Select(m => m.Qpc)];
            if (marks.Length < minMarks || _count < 2)
            {
                return [];
            }

            int start = (_head - _count + MaxFrames) % MaxFrames;
            var near = new bool[_count];
            for (int k = 0; k < _count; k++)
            {
                long q = _qpc[(start + k) % MaxFrames];
                near[k] = marks.Any(m => q >= m - lead && q <= m + lag);
            }

            double backgroundMinutes = near.Count(n => !n) / 30.0 / 60.0; // frames at the analysis rate
            var result = new List<ThresholdProposal>();
            foreach (Tracked t in _tracked.Where(t => t.Key.Event == eventName))
            {
                float[] peaks = new float[marks.Length];
                for (int m = 0; m < marks.Length; m++)
                {
                    float peak = float.NegativeInfinity;
                    for (int k = 0; k < _count; k++)
                    {
                        long q = _qpc[(start + k) % MaxFrames];
                        if (q >= marks[m] - lead && q <= marks[m] + lag)
                        {
                            peak = Math.Max(peak, t.Stat[(start + k) % MaxFrames]);
                        }
                    }

                    peaks[m] = peak;
                }

                Condition c = t.Condition;
                float current = c.Rise ?? c.Above ?? -c.Below!.Value;
                (int bestCaught, double bestFalse, float best) = (0, double.MaxValue, current);
                double bestScore = -1;
                foreach (float p in peaks.Where(float.IsFinite).Distinct())
                {
                    float candidate = p > 0 ? p * 0.9f : p * 1.1f; // a little below the peak, so it still holds there
                    int caught = peaks.Count(x => x > candidate);
                    int falses = Upcrossings(t, start, near, candidate);
                    double precision = caught + falses == 0 ? 0 : (double)caught / (caught + falses);
                    double recall = (double)caught / marks.Length;
                    double f1 = precision + recall == 0 ? 0 : 2 * precision * recall / (precision + recall);
                    if (f1 > bestScore + 1e-9 || (Math.Abs(f1 - bestScore) <= 1e-9 && candidate > best))
                    {
                        (bestScore, bestCaught, bestFalse, best) = (f1, caught, falses, candidate);
                    }
                }

                int curCaught = peaks.Count(x => x > current);
                int curFalse = Upcrossings(t, start, near, current);
                string kind = c.Rise is not null ? "rise" : c.Above is not null ? "above" : "below";
                float shown = kind == "below" ? -best : best;
                float shownCurrent = kind == "below" ? -current : current;
                result.Add(new ThresholdProposal(t.Key, c.Signal, kind, shownCurrent, (float)Math.Round(shown, 4), marks.Length, bestCaught,
                    PerMinute(bestFalse, backgroundMinutes), PerMinute(curFalse, backgroundMinutes), curCaught));
            }

            return result;
        }
    }

    private static double PerMinute(double count, double minutes) => minutes <= 0 ? 0 : Math.Round(count / minutes, 2);

    // Times the statistic rises past the threshold away from the marks, with the rule's cooldown.
    private int Upcrossings(Tracked t, int start, bool[] near, float threshold)
    {
        long cooldown = QpcTime.Rescale(t.CooldownMs, 1000, _frequency);
        long last = long.MinValue / 2;
        bool above = false;
        int n = 0;
        for (int k = 0; k < near.Length; k++)
        {
            int i = (start + k) % MaxFrames;
            bool now = t.Stat[i] > threshold;
            if (now && !above && !near[k] && _qpc[i] - last >= cooldown)
            {
                n++;
                last = _qpc[i];
            }

            above = now;
        }

        return n;
    }
}
