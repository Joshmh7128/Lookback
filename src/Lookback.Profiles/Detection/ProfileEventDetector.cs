using System.Globalization;
using Lookback.Contracts.V1;
using Lookback.Core.Time;
using Lookback.Profiles.Model;

namespace Lookback.Profiles.Detection;

/// <summary>
/// Evaluates a profile's rules over signal frames delivered in time order
/// (see <see cref="TimeOrderedMerger{T}"/>).
/// <list type="number">
/// <item>Each frame updates the latest value of every named signal.</item>
/// <item>On each video frame every rule's conditions are checked. A rule
/// that holds and is out of cooldown opens a <i>pending</i> event.</item>
/// <item>A pending event collects boosts from signals within the boost
/// window on both sides of the trigger, so it is finalised only once time has
/// moved past its widest boost window.</item>
/// <item>Finalised events feed composite rules (for example a multikill).</item>
/// </list>
/// It also reports each condition's margin (value minus threshold) so a viewer
/// can show why a rule did or did not fire.
/// </summary>
public sealed class ProfileEventDetector
{
    private const int HistoryMs = 2000;

    private readonly GameProfile _profile;
    private readonly long _frequency;
    private readonly Dictionary<string, float> _values = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Queue<(long Qpc, float Value)>> _history = new(StringComparer.Ordinal);
    private readonly Dictionary<uint, (SignalFamily Family, string[] Names)> _schemas = [];
    private readonly RuleState[] _rules;
    private readonly List<Pending> _pending = [];
    private readonly Dictionary<string, Queue<long>> _recent = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _compositeLast = new(StringComparer.Ordinal);
    private readonly float[] _margins;
    private long _now;

    public ProfileEventDetector(GameProfile profile, long frequency)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frequency);
        _profile = profile;
        _frequency = frequency;
        _rules = profile.Events.Select(e => new RuleState(e)).ToArray();

        foreach (string s in profile.Events.SelectMany(e => e.Boosts).Select(b => b.Signal))
        {
            _history.TryAdd(s, new Queue<(long, float)>());
        }

        var names = new List<string>();
        foreach (EventRule e in profile.Events)
        {
            int i = 0;
            foreach (Condition c in e.When.Concat(e.WhenAny))
            {
                names.Add(string.Create(CultureInfo.InvariantCulture, $"rule.{e.Name}.{i++}"));
            }
        }

        MarginNames = names;
        _margins = new float[names.Count];
    }

    /// <summary>One name per condition, in rule order: <c>rule.&lt;event&gt;.&lt;n&gt;</c>.</summary>
    public IReadOnlyList<string> MarginNames { get; }

    /// <summary>Margins after the last video frame. Positive means the condition held.</summary>
    public ReadOnlySpan<float> Margins => _margins;

    /// <summary>Signals the rules use that no schema has provided.</summary>
    public IEnumerable<string> MissingSignals(IEnumerable<string> available) =>
        GameProfileLoader.ValidateSignals(_profile, available.ToHashSet(StringComparer.Ordinal));

    public void OnSchema(SignalSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        _schemas[schema.SchemaId] = (schema.Family, [.. schema.Names]);
    }

    /// <summary>
    /// Consumes one frame. Returns true when it was a video frame and the
    /// margins were refreshed. Finalised events are appended to <paramref name="events"/>.
    /// </summary>
    public bool OnFrame(SignalFrame frame, List<GameEvent> events)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (!_schemas.TryGetValue(frame.SchemaId, out (SignalFamily Family, string[] Names) schema))
        {
            return false;
        }

        long t = (long)frame.WindowStartQpc;
        _now = Math.Max(_now, t);
        for (int i = 0; i < schema.Names.Length && i < frame.Values.Count; i++)
        {
            string name = schema.Names[i];
            float v = frame.Values[i];
            _values[name] = v;
            if (_history.TryGetValue(name, out Queue<(long Qpc, float Value)>? h))
            {
                h.Enqueue((t, v));
                long horizon = t - Ms(HistoryMs);
                while (h.Count > 0 && h.Peek().Qpc < horizon)
                {
                    h.Dequeue();
                }
            }
        }

        bool video = schema.Family == SignalFamily.Video;
        if (video)
        {
            Evaluate(t);
        }

        FinalisePending(_now, events, force: false);
        return video;
    }

    /// <summary>Finalises everything still pending (end of a session).</summary>
    public void Flush(List<GameEvent> events) => FinalisePending(long.MaxValue, events, force: true);

    private void Evaluate(long t)
    {
        int m = 0;
        foreach (RuleState r in _rules)
        {
            bool all = true;
            foreach (Condition c in r.Rule.When)
            {
                float margin = r.Margin(c, Value(c.Signal));
                _margins[m++] = margin;
                all &= margin > 0;
            }

            bool any = r.Rule.WhenAny.Count == 0;
            foreach (Condition c in r.Rule.WhenAny)
            {
                float margin = r.Margin(c, Value(c.Signal));
                _margins[m++] = margin;
                any |= margin > 0;
            }

            if (all && any && t - r.LastFired >= Ms(r.Rule.CooldownMs))
            {
                r.LastFired = t;
                var evidence = new List<string>();
                foreach (Condition c in r.Rule.When.Concat(r.Rule.WhenAny))
                {
                    evidence.Add(string.Create(CultureInfo.InvariantCulture, $"{c.Describe} (value {Value(c.Signal):0.####})"));
                }

                int widest = r.Rule.Boosts.Count == 0 ? 0 : r.Rule.Boosts.Max(b => b.WithinMs);
                _pending.Add(new Pending(r.Rule, t, t + Ms(widest), evidence));
            }

            r.UpdateBaselines(this);
        }
    }

    private void FinalisePending(long now, List<GameEvent> events, bool force)
    {
        for (int i = 0; i < _pending.Count; i++)
        {
            Pending p = _pending[i];
            if (!force && now < p.Due)
            {
                continue;
            }

            float confidence = p.Rule.BaseConfidence;
            foreach (Boost b in p.Rule.Boosts)
            {
                if (_history.TryGetValue(b.Signal, out Queue<(long Qpc, float Value)>? h)
                    && h.Any(x => Math.Abs(x.Qpc - p.At) <= Ms(b.WithinMs) && x.Value > b.Above))
                {
                    confidence += b.Add;
                    p.Evidence.Add(string.Create(CultureInfo.InvariantCulture, $"boost {b.Signal} > {b.Above} within {b.WithinMs} ms"));
                }
            }

            GameEvent e = Make(p.Rule.Name, p.At, p.Rule.Clip, Math.Min(1, confidence), p.Rule.Weight, p.Evidence);
            events.Add(e);
            Composite(e, events);
            _pending.RemoveAt(i--);
        }
    }

    private void Composite(GameEvent e, List<GameEvent> events)
    {
        foreach (CompositeRule c in _profile.Composites)
        {
            if (c.Of != e.Name)
            {
                continue;
            }

            if (!_recent.TryGetValue(c.Name, out Queue<long>? q))
            {
                q = new Queue<long>();
                _recent[c.Name] = q;
            }

            long at = (long)e.AtQpc;
            q.Enqueue(at);
            while (q.Count > 0 && at - q.Peek() > Ms(c.WithinMs))
            {
                q.Dequeue();
            }

            if (q.Count >= c.AtLeast)
            {
                long first = q.Peek();
                q.Clear(); // one composite per streak; the next one needs a fresh set
                var evidence = new List<string> { string.Create(CultureInfo.InvariantCulture, $"{c.AtLeast}+ {c.Of} within {c.WithinMs} ms") };
                GameEvent composite = Make(c.Name, at, c.Clip, e.Confidence, c.Weight, evidence);
                composite.WindowStartQpc = (ulong)Math.Max(0, first - Ms(c.Clip.PreMs)); // cover the whole streak
                events.Add(composite);
                _compositeLast[c.Name] = at;
            }
        }
    }

    private GameEvent Make(string name, long at, ClipWindow clip, float confidence, float weight, List<string> evidence) => new()
    {
        Name = name,
        GameProfile = _profile.Id,
        AtQpc = (ulong)at,
        WindowStartQpc = (ulong)Math.Max(0, at - Ms(clip.PreMs)),
        WindowEndQpc = (ulong)(at + Ms(clip.PostMs)),
        Confidence = confidence,
        Weight = weight,
        Evidence = { evidence },
    };

    private float Value(string signal) => _values.TryGetValue(signal, out float v) ? v : float.NaN;

    private long Ms(int ms) => QpcTime.Rescale(ms, 1000, _frequency);

    private sealed record Pending(EventRule Rule, long At, long Due, List<string> Evidence);

    private sealed class RuleState(EventRule rule)
    {
        private readonly Dictionary<Condition, float> _baseline = [];

        public EventRule Rule { get; } = rule;

        public long LastFired { get; set; } = long.MinValue / 2;

        /// <summary>Value minus threshold; positive when the condition holds. NaN (missing signal) never holds.</summary>
        public float Margin(Condition c, float value)
        {
            if (float.IsNaN(value))
            {
                return float.NegativeInfinity;
            }

            if (c.Above is { } above)
            {
                return value - above;
            }

            if (c.Below is { } below)
            {
                return below - value;
            }

            float baseline = _baseline.TryGetValue(c, out float b) ? b : value;
            return value - baseline - c.Rise!.Value;
        }

        /// <summary>Moves each rise baseline toward the current value after evaluation, so a spike is judged against the past.</summary>
        public void UpdateBaselines(ProfileEventDetector d)
        {
            foreach (Condition c in Rule.When.Concat(Rule.WhenAny))
            {
                if (c.Rise is null)
                {
                    continue;
                }

                float v = d.Value(c.Signal);
                if (float.IsNaN(v))
                {
                    continue;
                }

                _baseline[c] = _baseline.TryGetValue(c, out float b) ? b + (c.BaselineAlpha * (v - b)) : v;
            }
        }
    }
}
