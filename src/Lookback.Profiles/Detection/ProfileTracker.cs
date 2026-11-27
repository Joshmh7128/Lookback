using System.Globalization;
using System.Text.RegularExpressions;
using Lookback.Contracts.V1;
using Lookback.Core.Time;
using Lookback.Profiles.Model;

namespace Lookback.Profiles.Detection;

/// <summary>Facts about the running game that the platform exposes outside the game process. Live capture only.</summary>
public sealed record DetectionContext
{
    public string LaunchArgs { get; init; } = "";

    public string WindowTitle { get; init; } = "";

    /// <summary>New lines from the launcher's own log file (for example Roblox's log folder).</summary>
    public IReadOnlyList<string> LogLines { get; init; } = [];
}

/// <summary>
/// Decides which profile in a game's tree is being played: the game, a
/// sub-game beneath it (a Roblox experience), a mode beneath either.
/// <list type="number">
/// <item>The <b>floor</b> is where detection starts and never rises above:
/// the game (found by executable), a profile picked by hand, or a profile a
/// hint proved.</item>
/// <item><b>Hints</b> are exact: a Roblox place id in the launch arguments
/// or the client's log decides the experience outright and becomes the floor.</item>
/// <item><b>Fingerprints</b> are scored on every frame: the fraction of a
/// profile's visual conditions that hold, averaged over its window. From the
/// floor down, at each level the child with the best average above its
/// <c>minScore</c> wins; the walk stops where no child qualifies.</item>
/// <item>A different answer must persist for <c>holdSeconds</c> before the
/// active profile switches, so one odd frame never flips it. Stepping back up
/// to a parent waits a full window, so a switch between siblings does not
/// pass through the parent.</item>
/// </list>
/// </summary>
public sealed class ProfileTracker
{
    private readonly ProfileCatalog _catalog;
    private readonly long _frequency;
    private readonly Dictionary<string, Fingerprint> _fingerprints = new(StringComparer.Ordinal);
    private string _floor;
    private string? _pendingTarget;
    private long _pendingSince;
    private long _start = long.MinValue;

    public ProfileTracker(ProfileCatalog catalog, string floorId, long frequency, DetectionMethod method)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frequency);
        _catalog = catalog;
        _frequency = frequency;
        _floor = catalog.Get(floorId).Id;
        ActiveId = _floor;
        RootId = catalog.PathTo(floorId)[0].Id;
        InitialMethod = method;
        foreach (GameProfile p in catalog.Subtree(RootId).Where(p => p.Detect.Visual.Count > 0))
        {
            _fingerprints[p.Id] = new Fingerprint(p, Ms((int)(p.Detect.WindowSeconds * 1000)));
        }
    }

    public string RootId { get; }

    public string ActiveId { get; private set; }

    public DetectionMethod InitialMethod { get; }

    /// <summary>The announcement of the starting profile.</summary>
    public ProfileDetected Initial(long at) =>
        Make(ActiveId, InitialMethod, 1, at, [InitialMethod == DetectionMethod.Manual ? $"chosen: {ActiveId}" : $"executable matched {RootId}"]);

    /// <summary>Checks hints under the floor. A match moves the floor (and the active profile) to the deepest profile it proves.</summary>
    public ProfileDetected? ApplyHints(DetectionContext context, long at)
    {
        ArgumentNullException.ThrowIfNull(context);
        GameProfile? best = null;
        string? evidence = null;
        foreach (GameProfile p in _catalog.Subtree(_floor).Skip(1))
        {
            foreach (DetectHint h in p.Detect.Hints)
            {
                string? hit = Match(h, context);
                if (hit is not null && (best is null || Depth(p.Id) > Depth(best.Id)))
                {
                    best = p;
                    evidence = $"{h.Source} matched {hit}";
                }
            }
        }

        // The game's id hints: an id read from outside the process picks the profile that lists it.
        foreach (IdHint h in _catalog.Get(RootId).IdHints)
        {
            foreach (string id in Captures(h.Source, h.Regex, context))
            {
                GameProfile? p = _catalog.FindById(h.Kind, id);
                if (p is not null && p.Id != _floor && _catalog.PathTo(p.Id).Any(x => x.Id == _floor)
                    && (best is null || Depth(p.Id) > Depth(best.Id)))
                {
                    best = p;
                    evidence = $"{h.Source} {h.Kind} {id}";
                }
            }
        }

        if (best is null)
        {
            return null;
        }

        _floor = best.Id;
        _pendingTarget = null;
        ActiveId = best.Id;
        return Make(best.Id, DetectionMethod.Hint, 1, at, [evidence!]);
    }

    /// <summary>Scores fingerprints for one video frame and returns a detection if the active profile changed.</summary>
    public ProfileDetected? OnVideoFrame(long at, IReadOnlyDictionary<string, float> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (_start == long.MinValue)
        {
            _start = at;
        }

        foreach (Fingerprint f in _fingerprints.Values)
        {
            f.Add(at, values);
        }

        (string target, double score, Fingerprint? deciding) = Walk(at);
        if (target == ActiveId)
        {
            _pendingTarget = null;
            return null;
        }

        if (_pendingTarget != target)
        {
            _pendingTarget = target;
            _pendingSince = at;
        }

        // Stepping back up (no longer in this mode or experience) waits a full window of the
        // active profile, so a switch between siblings never detours through the parent.
        bool up = _catalog.PathTo(ActiveId).Any(p => p.Id == target);
        GameProfile active = _catalog.Get(ActiveId);
        double hold = up
            ? Math.Max(active.Detect.HoldSeconds, active.Detect.WindowSeconds)
            : deciding?.Profile.Detect.HoldSeconds ?? active.Detect.HoldSeconds;
        if (at - _pendingSince < Ms((int)(hold * 1000)))
        {
            return null;
        }

        ActiveId = target;
        _pendingTarget = null;
        var evidence = new List<string>();
        if (deciding is not null)
        {
            evidence.Add(string.Create(CultureInfo.InvariantCulture, $"fingerprint {score:P0} over {deciding.Profile.Detect.WindowSeconds:0.#} s"));
            evidence.AddRange(deciding.Profile.Detect.Visual.Select(c => c.Describe));
        }
        else
        {
            evidence.Add("no fingerprint below this level holds any more");
        }

        return Make(target, DetectionMethod.Visual, (float)(deciding is null ? 1 : score), at, evidence);
    }

    private (string Target, double Score, Fingerprint? Deciding) Walk(long at)
    {
        string node = _floor;
        double score = 1;
        Fingerprint? deciding = null;
        while (true)
        {
            Fingerprint? best = null;
            double bestScore = 0;
            foreach (GameProfile child in _catalog.Children(node))
            {
                if (_fingerprints.TryGetValue(child.Id, out Fingerprint? f) && f.Ready(at, _start))
                {
                    double s = f.Average;
                    if (s >= child.Detect.MinScore && s > bestScore)
                    {
                        best = f;
                        bestScore = s;
                    }
                }
            }

            if (best is null)
            {
                return (node, score, deciding);
            }

            node = best.Profile.Id;
            score = bestScore;
            deciding = best;
        }
    }

    private static string? Match(DetectHint h, DetectionContext context)
    {
        IEnumerable<string> texts = h.Source switch
        {
            HintSource.LaunchArgs => [context.LaunchArgs],
            HintSource.WindowTitle => [context.WindowTitle],
            _ => context.LogLines,
        };

        foreach (string text in texts)
        {
            Match m = Regex.Match(text, h.Regex, RegexOptions.None, TimeSpan.FromMilliseconds(100));
            if (!m.Success)
            {
                continue;
            }

            if (h.Values.Count == 0)
            {
                return m.Value;
            }

            if (m.Groups.Count > 1 && h.Values.Contains(m.Groups[1].Value, StringComparer.Ordinal))
            {
                return m.Groups[1].Value;
            }
        }

        return null;
    }

    private static IEnumerable<string> Captures(HintSource source, string regex, DetectionContext context)
    {
        IEnumerable<string> texts = source switch
        {
            HintSource.LaunchArgs => [context.LaunchArgs],
            HintSource.WindowTitle => [context.WindowTitle],
            _ => context.LogLines,
        };

        foreach (string text in texts)
        {
            foreach (Match m in Regex.Matches(text, regex, RegexOptions.None, TimeSpan.FromMilliseconds(100)))
            {
                if (m.Groups.Count > 1 && m.Groups[1].Success)
                {
                    yield return m.Groups[1].Value;
                }
            }
        }
    }

    private int Depth(string id) => _catalog.PathTo(id).Count;

    private ProfileDetected Make(string id, DetectionMethod method, float confidence, long at, IEnumerable<string> evidence)
    {
        GameProfile p = _catalog.Get(id);
        return new ProfileDetected
        {
            ProfileId = id,
            Path = { _catalog.PathTo(id).Select(x => x.Id) },
            Level = p.Level switch { ProfileLevelKind.Subgame => ProfileLevel.Subgame, ProfileLevelKind.Mode => ProfileLevel.Mode, _ => ProfileLevel.Game },
            Method = method,
            Confidence = confidence,
            AtQpc = (ulong)at,
            Evidence = { evidence },
        };
    }

    private long Ms(int ms) => QpcTime.Rescale(ms, 1000, _frequency);

    /// <summary>Per-frame fraction of conditions holding, averaged over a sliding window.</summary>
    private sealed class Fingerprint(GameProfile profile, long window)
    {
        private readonly Queue<(long At, double Score)> _scores = new();
        private double _sum;

        public GameProfile Profile { get; } = profile;

        public double Average => _scores.Count == 0 ? 0 : _sum / _scores.Count;

        /// <summary>Half a window of evidence before it may decide anything.</summary>
        public bool Ready(long at, long start) => at - start >= window / 2;

        public void Add(long at, IReadOnlyDictionary<string, float> values)
        {
            int held = 0;
            foreach (Condition c in Profile.Detect.Visual)
            {
                if (values.TryGetValue(c.Signal, out float v)
                    && ((c.Above is { } a && v > a) || (c.Below is { } b && v < b)))
                {
                    held++;
                }
            }

            double s = (double)held / Profile.Detect.Visual.Count;
            _scores.Enqueue((at, s));
            _sum += s;
            while (_scores.Count > 0 && at - _scores.Peek().At > window)
            {
                _sum -= _scores.Dequeue().Score;
            }
        }
    }
}
