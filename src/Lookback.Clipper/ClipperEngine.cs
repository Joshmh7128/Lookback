using System.Globalization;
using Lookback.Clipper.Candidates;
using Lookback.Clipper.Curve;
using Lookback.Clipper.Qa;
using Lookback.Contracts.V1;
using Lookback.Core.Time;
using Lookback.Profiles.Model;

namespace Lookback.Clipper;

/// <summary>A finished candidate, its QA report, and its features for the learned scorer (in memory only).</summary>
public readonly record struct ClipperOutput(ClipCandidate Candidate, QaReport Qa, CandidateFeatures Features);

/// <summary>
/// The clipper's deterministic core, with no threads and no bus: feed it
/// signal frames in time order (as <see cref="Profiles.Detection.TimeOrderedMerger{T}"/>
/// releases them), game events and profile changes, and it returns finished
/// candidates with their QA reports.
/// <list type="number">
/// <item><b>Normalise.</b> Each curve component (an existing video or audio
/// signal) becomes a rolling robust z-score against this session's own
/// recent past (<see cref="RobustNormalizer"/>), clamped to 0..<see cref="ClipperOptions.ZCap"/>.
/// Audio hops between two video frames contribute their maximum.</item>
/// <item><b>Curve.</b> On every video frame, the weighted mean of those
/// z-scores (weights from the active profile), smoothed by an envelope
/// follower (fast attack, slower release).</item>
/// <item><b>Peaks.</b> Hysteresis and a refractory period (<see cref="PeakDetector"/>),
/// each peak with a window [t − pre, t + post].</item>
/// <item><b>Fuse.</b> Event windows and peak windows that overlap merge into
/// one candidate (<see cref="CandidateFuser"/>).</item>
/// <item><b>QA.</b> Each candidate is checked over its window (<see cref="QaGates"/>).</item>
/// </list>
/// <para>
/// When it follows a profile, events are taken into the fusion in time order,
/// only once no earlier event can still arrive (the caller guarantees that
/// every event finalised before the current frame has been delivered, which
/// <see cref="ClipperRunner"/> does by gating on the profile runner's
/// <c>signals.rules</c> frames). A candidate is closed once nothing not yet
/// seen could still overlap it. So the output depends only on timestamps.
/// </para>
/// Per frame it allocates nothing; allocation happens only when a peak ends,
/// an event arrives or a candidate is finished.
/// </summary>
public sealed class ClipperEngine
{
    private const int VideoSamplesPerSecond = 64; // the analysis format is 30 fps; room for 60
    private const int AudioSamplesPerSecond = 128; // 10 ms hops

    private readonly string _sessionId;
    private readonly long _frequency;
    private readonly ClipperOptions _o;
    private readonly string[] _components;
    private readonly Dictionary<string, CurveProfile> _profiles = new(StringComparer.Ordinal);
    private readonly RobustNormalizer[] _norm;
    private readonly float[] _video;
    private readonly float[] _audioMax;
    private readonly float[] _audioLast;
    private readonly bool[] _audioHas;
    private readonly bool[] _present;
    private readonly bool[] _isAudio;
    private readonly float[] _z;
    private readonly float[] _curve;
    private readonly Dictionary<uint, SchemaMap> _schemas = [];
    private readonly PeakDetector _peaks;
    private readonly CandidateFuser _fuser;
    private readonly FlagHistory _videoFlags;
    private readonly FlagHistory _audioFlags;
    private readonly CurveHistory _history;
    private readonly FeatureNames _featureNames;
    private readonly float[] _zMean;
    private readonly float[] _zMax;
    private readonly List<GameEvent> _events = [];
    private readonly List<ProfileDetected> _profileChanges = [];
    private readonly List<CandidateGroup> _closed = [];
    private readonly List<CurvePeak> _flushed = [];
    private readonly bool _following;
    private readonly long _eventLag;
    private readonly long _eventReach;
    private readonly long _curveReach;
    private readonly long _attack;
    private readonly long _release;
    private CurveProfile _active;
    private float _smooth;
    private long _lastTick = long.MinValue;
    private long _lastSeen;
    private int _count;

    /// <summary>Curve only, with the default settings (optionally overridden by <paramref name="spec"/>); events are fused if given, but not waited for.</summary>
    public ClipperEngine(string sessionId, long frequency, ClipperOptions? options = null, ClipperSpec? spec = null)
        : this(sessionId, frequency, options, [("", spec ?? new ClipperSpec())], "", following: false, eventLag: 0, eventReach: 0)
    {
    }

    /// <summary>
    /// Follows a game profile tree: weights and thresholds come from the
    /// active profile (resolved through the tree over the defaults), and the
    /// tree's events are fused with the curve. <c>startId</c> is the profile the session starts in: a game, or a
    /// sub-game or mode chosen by hand.
    /// </summary>
    public ClipperEngine(ProfileCatalog catalog, string startId, string sessionId, long frequency, ClipperOptions? options = null)
        : this(sessionId, frequency, options, Tree(catalog, startId, out long lag, out long reach), startId, following: true, eventLag: lag, eventReach: reach)
    {
    }

    private ClipperEngine(string sessionId, long frequency, ClipperOptions? options, IReadOnlyList<(string Id, ClipperSpec Spec)> specs, string startId, bool following, long eventLag, long eventReach)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frequency);
        _o = options ?? new ClipperOptions();
        _o.Validate();
        _sessionId = sessionId;
        _frequency = frequency;
        _following = following;

        (IReadOnlyList<string> names, IReadOnlyList<CurveProfile> profiles) = CurveProfile.Build(specs, frequency);
        _components = [.. names];
        foreach (CurveProfile p in profiles)
        {
            _profiles[p.Id] = p;
        }

        _active = _profiles[startId];
        int n = _components.Length;
        long interval = Ms(_o.StatsIntervalMs);
        int capacity = Math.Max(1, (int)Math.Ceiling(_o.NormalisationWindowSeconds * 1000 / _o.StatsIntervalMs));
        int warmup = (int)Math.Ceiling(_o.WarmupSeconds * 1000 / _o.StatsIntervalMs);
        _norm = new RobustNormalizer[n];
        for (int i = 0; i < n; i++)
        {
            _norm[i] = new RobustNormalizer(capacity, warmup, interval);
        }

        _video = new float[n];
        _audioMax = new float[n];
        _audioLast = new float[n];
        Array.Fill(_video, float.NaN);
        Array.Fill(_audioLast, float.NaN);
        _audioHas = new bool[n];
        _present = new bool[n];
        _isAudio = new bool[n];
        _z = new float[n];
        _curve = new float[3 + n];
        CurveNames = ["clipper.excitement", "clipper.high", "clipper.low", .. _components.Select(c => "clipper.z." + c)];

        _peaks = new PeakDetector(n, Ms(_o.MaxLengthMs));
        _fuser = new CandidateFuser(Ms(_o.MergeGapMs), Ms(_o.MaxLengthMs));
        _videoFlags = new FlagHistory((int)Math.Min(int.MaxValue, _o.HistorySeconds * VideoSamplesPerSecond));
        _audioFlags = new FlagHistory((int)Math.Min(int.MaxValue, _o.HistorySeconds * AudioSamplesPerSecond));
        _history = new CurveHistory((int)Math.Min(int.MaxValue / (n + 2), _o.HistorySeconds * VideoSamplesPerSecond), n);
        _featureNames = new FeatureNames(_components);
        _zMean = new float[n];
        _zMax = new float[n];
        _eventLag = Ms(eventLag);
        _eventReach = Ms(eventReach);
        _curveReach = profiles.Max(p => p.Pre);
        _attack = Ms(_o.AttackMs);
        _release = Ms(_o.ReleaseMs);
    }

    /// <summary>Names of every candidate's <see cref="CandidateFeatures"/>, in order.</summary>
    public IReadOnlyList<string> FeatureNames => _featureNames.Names;

    /// <summary>The signals the curve combines, in component order.</summary>
    public IReadOnlyList<string> Components => _components;

    /// <summary>Names of <see cref="CurveValues"/>: the curve, its active thresholds, then each component's clamped z-score.</summary>
    public IReadOnlyList<string> CurveNames { get; }

    /// <summary>The curve after the last video frame, laid out as <see cref="CurveNames"/>.</summary>
    public ReadOnlySpan<float> CurveValues => _curve;

    /// <summary>The profile whose weights and thresholds are in use; empty when not following a profile.</summary>
    public string ActiveProfileId => _active.Id;

    /// <summary>True when the engine fuses a profile tree's events and waits for them.</summary>
    public bool FollowsProfile => _following;

    public int CandidatesEmitted => _count;

    public void OnSchema(SignalSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        if (schema.Family is not (SignalFamily.Video or SignalFamily.Audio))
        {
            return;
        }

        bool audio = schema.Family == SignalFamily.Audio;
        var values = new List<int>();
        var components = new List<int>();
        var map = new SchemaMap { Family = schema.Family };
        for (int k = 0; k < schema.Names.Count; k++)
        {
            string name = schema.Names[k];
            int ci = Array.IndexOf(_components, name);
            if (ci >= 0)
            {
                values.Add(k);
                components.Add(ci);
                _present[ci] = true;
                _isAudio[ci] = audio;
            }

            switch (name)
            {
                case "luma.black": map.Black = k; break;
                case "motion.frozen_s": map.Frozen = k; break;
                case "audio.silent": map.Silent = k; break;
            }
        }

        map.Values = [.. values];
        map.Components = [.. components];
        _schemas[schema.SchemaId] = map;
    }

    /// <summary>A game event. Taken into the fusion in time order once no earlier one can still arrive.</summary>
    public void OnEvent(GameEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        int i = _events.Count;
        while (i > 0 && Compare(_events[i - 1], e) > 0)
        {
            i--;
        }

        _events.Insert(i, e);

        static int Compare(GameEvent a, GameEvent b)
        {
            int c = a.AtQpc.CompareTo(b.AtQpc);
            return c != 0 ? c : string.CompareOrdinal(a.Name, b.Name);
        }
    }

    /// <summary>A change of active profile; applied from its own time on.</summary>
    public void OnProfile(ProfileDetected d)
    {
        ArgumentNullException.ThrowIfNull(d);
        int i = _profileChanges.Count;
        while (i > 0 && _profileChanges[i - 1].AtQpc > d.AtQpc)
        {
            i--;
        }

        _profileChanges.Insert(i, d);
    }

    /// <summary>
    /// One signal frame, in time order. Returns true when it was a video frame
    /// (the curve moved on). Finished candidates are appended to <paramref name="output"/>.
    /// </summary>
    public bool OnFrame(SignalFrame frame, List<ClipperOutput> output)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(output);
        if (!_schemas.TryGetValue(frame.SchemaId, out SchemaMap? map))
        {
            return false;
        }

        long t = (long)frame.WindowStartQpc;
        _lastSeen = Math.Max(_lastSeen, (long)frame.WindowEndQpc);
        int count = frame.Values.Count;
        if (map.Family == SignalFamily.Audio)
        {
            for (int k = 0; k < map.Values.Length; k++)
            {
                int vi = map.Values[k], ci = map.Components[k];
                if (vi < count && float.IsFinite(frame.Values[vi]))
                {
                    float v = frame.Values[vi];
                    _audioMax[ci] = _audioHas[ci] ? Math.Max(_audioMax[ci], v) : v;
                    _audioHas[ci] = true;
                }
            }

            if (map.Silent >= 0 && map.Silent < count)
            {
                _audioFlags.Add(t, frame.Values[map.Silent] > 0.5f, false);
            }

            return false;
        }

        for (int k = 0; k < map.Values.Length; k++)
        {
            if (map.Values[k] < count)
            {
                _video[map.Components[k]] = frame.Values[map.Values[k]];
            }
        }

        bool black = map.Black >= 0 && map.Black < count && frame.Values[map.Black] > 0.5f;
        bool frozen = map.Frozen >= 0 && map.Frozen < count && frame.Values[map.Frozen] >= _o.Qa.FrozenSeconds;
        _videoFlags.Add(t, black, frozen);
        Tick(t, output);
        return true;
    }

    /// <summary>End of a session: takes in every event, ends any peak, and finishes every candidate.</summary>
    public void Flush(List<ClipperOutput> output)
    {
        ArgumentNullException.ThrowIfNull(output);
        TakeEvents(long.MaxValue);
        _flushed.Clear();
        _peaks.Flush(_flushed);
        foreach (CurvePeak p in _flushed)
        {
            AddPeak(p);
        }

        _fuser.CloseAll(_closed);
        foreach (CandidateGroup g in _closed)
        {
            g.ClampEnd(_lastSeen);
        }

        Emit(output);
    }

    private void Tick(long t, List<ClipperOutput> output)
    {
        ApplyProfileChanges(t);
        CurveProfile p = _active;
        float sum = 0, weights = 0;
        for (int i = 0; i < _components.Length; i++)
        {
            float x;
            if (_isAudio[i])
            {
                if (_audioHas[i])
                {
                    _audioLast[i] = _audioMax[i];
                    _audioHas[i] = false;
                }

                x = _audioLast[i]; // no hop since the last frame: hold the last value
            }
            else
            {
                x = _video[i];
            }

            float z = 0;
            if (_present[i] && float.IsFinite(x))
            {
                RobustNormalizer norm = _norm[i];
                if (norm.Ready)
                {
                    z = Math.Clamp(norm.Z(x, p.MinSpread[i]), 0, _o.ZCap);
                    weights += p.Weights[i];
                    sum += p.Weights[i] * z;
                }

                norm.Observe(t, x); // after scoring, so a spike is judged against the past
            }

            _z[i] = z;
        }

        float raw = weights > 0 ? sum / weights : 0;
        long dt = _lastTick == long.MinValue ? 0 : t - _lastTick;
        double alpha = dt <= 0 ? 1 : 1 - Math.Exp(-dt / (double)(raw > _smooth ? _attack : _release));
        _smooth += (float)(alpha * (raw - _smooth));
        _lastTick = t;

        _curve[0] = _smooth;
        _curve[1] = p.High;
        _curve[2] = p.Low;
        _z.CopyTo(_curve.AsSpan(3));
        _history.Add(t, _smooth, p.High, _z);

        TakeEvents(EventSafe(t));
        if (_peaks.Step(t, _smooth, p, _z) is { } peak)
        {
            AddPeak(peak);
        }

        _fuser.Close(Horizon(t), _closed);
        Emit(output);
    }

    /// <summary>Every event at or before this time has been delivered.</summary>
    private long EventSafe(long t) => _following ? t - _eventLag : long.MaxValue;

    /// <summary>The earliest start any contribution not yet in the fusion can have.</summary>
    private long Horizon(long t)
    {
        long curve = Math.Min(_peaks.Pending?.Start ?? long.MaxValue, (_peaks.Active ? _peaks.ActiveStart : t) - _curveReach);
        long events = _following ? EventSafe(t) - _eventReach : long.MaxValue;
        return Math.Min(curve, events);
    }

    private void ApplyProfileChanges(long t)
    {
        int applied = 0;
        while (applied < _profileChanges.Count && (long)_profileChanges[applied].AtQpc <= t)
        {
            if (_profiles.TryGetValue(_profileChanges[applied].ProfileId, out CurveProfile? p))
            {
                _active = p;
            }

            applied++;
        }

        if (applied > 0)
        {
            _profileChanges.RemoveRange(0, applied);
        }
    }

    private void TakeEvents(long safe)
    {
        int taken = 0;
        while (taken < _events.Count && (long)_events[taken].AtQpc <= safe)
        {
            GameEvent e = _events[taken++];
            _fuser.Add(new Contribution
            {
                Start = (long)e.WindowStartQpc,
                End = (long)e.WindowEndQpc,
                At = (long)e.AtQpc,
                Score = e.Weight * e.Confidence,
                Profile = e.GameProfile,
                Event = e,
            });
        }

        if (taken > 0)
        {
            _events.RemoveRange(0, taken);
        }
    }

    private void AddPeak(CurvePeak peak) => _fuser.Add(new Contribution
    {
        Start = peak.Start,
        End = peak.End,
        At = peak.At,
        Score = peak.Profile.PeakScore * Math.Min(3, peak.Value / peak.Profile.High),
        Profile = peak.Profile.Id,
        Peak = peak,
    });

    private void Emit(List<ClipperOutput> output)
    {
        foreach (CandidateGroup g in _closed)
        {
            ClipperOutput parent = Finish(g);
            output.Add(parent);
            Highlights(g, parent, output);
        }

        _closed.Clear();
    }

    private ClipperOutput Finish(CandidateGroup g) =>
        Build(g, string.Create(CultureInfo.InvariantCulture, $"{_sessionId}-c{++_count:0000}"), null);

    /// <summary>
    /// The highlights of a candidate longer than its profile's longest
    /// highlight: windows around its strongest moments, each built as a
    /// candidate of its own, ranked (by the host's ranking, else the heuristic),
    /// and the best that do not overlap kept, in time order, as
    /// <c>&lt;parent&gt;-h1</c>, <c>-h2</c>….
    /// </summary>
    private void Highlights(CandidateGroup g, ClipperOutput parent, List<ClipperOutput> output)
    {
        CurveProfile p = _profiles.TryGetValue(parent.Candidate.GameProfile, out CurveProfile? own) ? own : _active;
        long length = g.End - g.Start;
        if (length <= p.HighlightMax)
        {
            return;
        }

        string parentId = parent.Candidate.CandidateId;
        var ranked = new List<(ClipperOutput Output, double Rank)>();
        foreach ((long a, long b) in HighlightPicker.Windows(g.Items, g.Start, g.End, p.HighlightMin, p.HighlightMax, _frequency))
        {
            var window = new CandidateGroup(a, b, g.Items.Where(c => c.At >= a && c.At <= b));
            if (window.Items.Count == 0)
            {
                continue;
            }

            ClipperOutput o = Build(window, parentId + "-h", parentId);
            ranked.Add((o, _o.RankHighlight?.Invoke(o.Candidate, o.Features) ?? o.Candidate.HeuristicScore));
        }

        if (ranked.Count == 0)
        {
            return;
        }

        int most = Math.Max(1, (int)Math.Ceiling(length / (double)p.HighlightMax));
        double best = ranked.Max(r => r.Rank);
        var kept = new List<ClipperOutput>();
        foreach ((ClipperOutput o, double rank) in ranked.OrderByDescending(r => r.Rank).ThenBy(r => r.Output.Candidate.StartQpc))
        {
            if (kept.Count >= most || (kept.Count > 0 && rank < _o.HighlightKeepFraction * best))
            {
                break;
            }

            long s = (long)o.Candidate.StartQpc, e = (long)o.Candidate.EndQpc;
            if (kept.Any(k => Math.Min(e, (long)k.Candidate.EndQpc) - Math.Max(s, (long)k.Candidate.StartQpc) > (e - s) / 4))
            {
                continue; // overlaps a better highlight
            }

            kept.Add(o);
        }

        int n = 0;
        foreach (ClipperOutput o in kept.OrderBy(k => k.Candidate.StartQpc))
        {
            string id = string.Create(CultureInfo.InvariantCulture, $"{parentId}-h{++n}");
            o.Candidate.CandidateId = id;
            o.Qa.CandidateId = id;
            output.Add(o);
        }
    }

    private ClipperOutput Build(CandidateGroup g, string id, string? parentId)
    {
        Contribution best = g.Items[0];
        double raw = 0;
        foreach (Contribution c in g.Items)
        {
            raw += c.Score;
            if (c.Score > best.Score || (c.Score == best.Score && c.At < best.At))
            {
                best = c;
            }
        }

        double score = 1 - Math.Exp(-raw);
        var candidate = new ClipCandidate
        {
            CandidateId = id,
            StartQpc = (ulong)g.Start,
            EndQpc = (ulong)g.End,
            PeakQpc = (ulong)best.At,
            HeuristicScore = (float)score,
            HighlightProbability = (float)score, // placeholder until ratings calibrate a model (M4)
            GameProfile = best.Profile,
            ParentCandidateId = parentId ?? "",
        };
        candidate.Reasons.AddRange(Reasons(g));
        QaReport qa = QaGates.Evaluate(id, g.Start, g.End, _videoFlags, _audioFlags, _o.Qa, _frequency);
        return new ClipperOutput(candidate, qa, Features(g, candidate, qa, raw, best));
    }

    /// <summary>The numbers behind a candidate, for the learned scorer (M4). See <see cref="CandidateFeatures"/>.</summary>
    private CandidateFeatures Features(CandidateGroup g, ClipCandidate c, QaReport qa, double raw, Contribution best)
    {
        float[] v = new float[_featureNames.Names.Length];
        int i = 0;
        double seconds = QpcTime.ToTimeSpan(g.End - g.Start, _frequency).TotalSeconds;
        GameEvent[] events = [.. g.Items.Where(x => x.Event is not null).Select(x => x.Event!)];
        Contribution[] peaks = [.. g.Items.Where(x => x.Peak is not null)];

        v[i++] = c.HeuristicScore;
        v[i++] = (float)raw;
        v[i++] = (float)seconds;
        v[i++] = g.End > g.Start ? (float)((best.At - g.Start) / (double)(g.End - g.Start)) : 0;
        v[i++] = events.Length > 0 ? 1 : 0;
        v[i++] = peaks.Length > 0 ? 1 : 0;

        v[i++] = events.Length;
        v[i++] = events.Select(e => e.Name).Distinct(StringComparer.Ordinal).Count();
        v[i++] = events.Sum(e => e.Weight * e.Confidence);
        v[i++] = events.Select(e => e.Weight * e.Confidence).DefaultIfEmpty(0).Max();
        v[i++] = events.Length > 0 ? events.Average(e => e.Confidence) : 0;
        v[i++] = events.Select(e => e.Confidence).DefaultIfEmpty(0).Max();
        v[i++] = events.Select(e => e.Weight).DefaultIfEmpty(0).Max();

        v[i++] = peaks.Sum(p => p.Peak!.Count);
        v[i++] = peaks.Select(p => p.Peak!.Value).DefaultIfEmpty(0).Max();
        v[i++] = (float)peaks.Sum(p => p.Score);

        _history.Summarise(g.Start, g.End, out float mean, out float max, out float above, _zMean, _zMax);
        v[i++] = mean;
        v[i++] = max;
        v[i++] = above;

        v[i++] = qa.Checks.FirstOrDefault(k => k.Name == QaGates.Black)?.Value ?? 1;
        v[i++] = qa.Checks.FirstOrDefault(k => k.Name == QaGates.Frozen)?.Value ?? 1;
        v[i++] = qa.Checks.FirstOrDefault(k => k.Name == QaGates.Silence)?.Value ?? 1;
        v[i++] = qa.Passed ? 1 : 0;

        _zMean.CopyTo(v.AsSpan(i));
        i += _zMean.Length;
        _zMax.CopyTo(v.AsSpan(i));
        return new CandidateFeatures(_featureNames, v);
    }

    private IEnumerable<string> Reasons(CandidateGroup g)
    {
        foreach (IGrouping<string, GameEvent> events in g.Items.Where(c => c.Event is not null).Select(c => c.Event!)
            .OrderBy(e => e.AtQpc).GroupBy(e => e.Name, StringComparer.Ordinal))
        {
            yield return string.Create(CultureInfo.InvariantCulture, $"{events.Key} ×{events.Count()} (best confidence {events.Max(x => x.Confidence):0.00})");
        }

        CurvePeak[] peaks = [.. g.Items.Where(c => c.Peak is not null).Select(c => c.Peak!)];
        if (peaks.Length > 0)
        {
            CurvePeak top = peaks.MaxBy(p => p.Value)!;
            float[] w = top.Profile.Weights;
            string drivers = string.Join(", ", Enumerable.Range(0, top.Z.Length)
                .Where(i => w[i] > 0 && top.Z[i] >= 1)
                .OrderByDescending(i => w[i] * top.Z[i])
                .Take(3)
                .Select(i => string.Create(CultureInfo.InvariantCulture, $"{_components[i]} {top.Z[i]:0.0}σ")));
            yield return string.Create(CultureInfo.InvariantCulture, $"excitement ×{peaks.Sum(p => p.Count)} (peak {top.Value:0.00}{(drivers.Length > 0 ? ": " + drivers : "")})");
        }
    }

    private long Ms(long ms) => QpcTime.Rescale(ms, 1000, _frequency);

    private static List<(string Id, ClipperSpec Spec)> Tree(ProfileCatalog catalog, string startId, out long eventLagMs, out long eventReachMs)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentException.ThrowIfNullOrEmpty(startId);
        string root = catalog.PathTo(startId)[0].Id;
        List<GameProfile> tree = [.. catalog.Subtree(root).Select(p => catalog.Resolve(p.Id))];

        // An event reaches the bus once stream time passes its trigger plus its widest boost
        // window (plus up to a frame); composites carry the time of the event that completes them.
        int widestBoost = tree.SelectMany(p => p.Events).SelectMany(e => e.Boosts).Select(b => b.WithinMs).DefaultIfEmpty(0).Max();
        eventLagMs = widestBoost + 100;

        // How far before its time an event's window can start: its pre-roll, and for a
        // composite also the length of the streak it covers.
        int pre = tree.SelectMany(p => p.Events.Select(e => e.Clip.PreMs).Concat(p.Composites.Select(c => c.Clip.PreMs))).DefaultIfEmpty(0).Max();
        int streak = tree.SelectMany(p => p.Composites).Select(c => c.WithinMs).DefaultIfEmpty(0).Max();
        eventReachMs = pre + streak;
        return [.. tree.Select(p => (p.Id, p.Clipper))];
    }

    private sealed class SchemaMap
    {
        public SignalFamily Family { get; init; }

        /// <summary>Positions in the frame's values of the curve components…</summary>
        public int[] Values { get; set; } = [];

        /// <summary>…and which component each one is.</summary>
        public int[] Components { get; set; } = [];

        public int Black { get; set; } = -1;

        public int Frozen { get; set; } = -1;

        public int Silent { get; set; } = -1;
    }
}
