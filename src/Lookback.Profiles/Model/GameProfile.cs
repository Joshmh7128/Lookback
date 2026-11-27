using System.Text.Json.Serialization;

namespace Lookback.Profiles.Model;

/// <summary>
/// Everything Lookback knows about one game, as data. A profile never adds a
/// new kind of measurement: it points the game-agnostic signals at the
/// screen regions that matter for this game and turns their output into named
/// moments with rules. Adding a game means writing a JSON file, not code.
/// </summary>
public sealed record GameProfile
{
    /// <summary>Stable id, lower case, used in events and file names (for example <c>overwatch</c>).</summary>
    public required string Id { get; init; }

    public required string DisplayName { get; init; }

    public int Version { get; init; } = 1;

    /// <summary>
    /// Id of the profile this one refines. Empty for a game. A sub-game (a
    /// Roblox experience) names its game; a mode names its game or sub-game.
    /// The id must start with the parent's id and a dot: <c>roblox.arsenal</c>.
    /// </summary>
    public string? Parent { get; init; }

    public ProfileLevelKind Level { get; init; } = ProfileLevelKind.Game;

    /// <summary>How to tell that this profile, rather than its siblings, is being played.</summary>
    public DetectSpec Detect { get; init; } = new();

    /// <summary>
    /// Platform ids for this profile, by kind: for a Roblox experience,
    /// <c>"roblox.universe": ["…"]</c> (one per experience, stable across its
    /// places) and <c>"roblox.place": ["…", "…"]</c> (lobby, worlds, matches).
    /// Used to look a profile up by id and to match the game's id hints.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Ids { get; init; } = new Dictionary<string, IReadOnlyList<string>>();

    /// <summary>Free-form categories for search and grouping, such as <c>shooter</c> or <c>roleplay</c>.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>
    /// Games only. Hints that read a platform id from outside the game process
    /// (for example the universe id in the Roblox client's log); the id picks
    /// whichever profile beneath the game lists it in <see cref="Ids"/>.
    /// </summary>
    public IReadOnlyList<IdHint> IdHints { get; init; } = [];

    /// <summary>Inherited events and composites this profile switches off.</summary>
    public IReadOnlyList<string> Disable { get; init; } = [];

    /// <summary>Executable names that identify the game during live detection. Games only.</summary>
    public IReadOnlyList<string> Executables { get; init; } = [];

    public CapturePolicy CapturePolicy { get; init; } = new();

    public ProfileReference Reference { get; init; } = new();

    /// <summary>HUD areas in normalised coordinates of a 16:9 frame.</summary>
    public IReadOnlyList<HudRegion> Regions { get; init; } = [];

    /// <summary>Moments recognised directly from signals.</summary>
    public IReadOnlyList<EventRule> Events { get; init; } = [];

    /// <summary>Moments recognised from patterns of other events.</summary>
    public IReadOnlyList<CompositeRule> Composites { get; init; } = [];

    /// <summary>
    /// Optional overrides of the clipper's excitement curve for this game:
    /// component weights and thresholds over existing signals. Inherits down
    /// the tree; anything left out comes from the parent, then from the
    /// clipper's defaults.
    /// </summary>
    public ClipperSpec Clipper { get; init; } = new();
}

/// <summary>
/// How the clipper's game-agnostic excitement curve is built and cut, as data.
/// The clipper ships a default (<c>clipper.defaults.json</c> in
/// <c>Lookback.Clipper</c>); a profile overrides parts of it and its children
/// inherit those overrides. Every field is optional: a component replaces the
/// inherited component of the same signal, and an unset number keeps the
/// inherited one. The curve only ever weighs signals that already exist.
/// </summary>
public sealed record ClipperSpec
{
    /// <summary>Signals combined into the curve, each with its weight. Weight 0 switches an inherited component off.</summary>
    public IReadOnlyList<CurveComponent> Components { get; init; } = [];

    /// <summary>The curve must rise to this (a weighted mean of robust z-scores) to start a peak.</summary>
    public float? High { get; init; }

    /// <summary>A peak ends when the curve falls below this; lower than <see cref="High"/> (hysteresis).</summary>
    public float? Low { get; init; }

    /// <summary>Footage kept before a curve peak.</summary>
    public int? PreMs { get; init; }

    /// <summary>Footage kept after a curve peak.</summary>
    public int? PostMs { get; init; }

    /// <summary>Peaks closer than this to the previous one are folded into it.</summary>
    public int? RefractoryMs { get; init; }

    /// <summary>How much a peak at exactly <see cref="High"/> adds to a candidate's raw score (an event adds weight × confidence).</summary>
    public float? PeakScore { get; init; }

    /// <summary>This spec over <paramref name="parent"/>: own components replace the parent's of the same signal; unset numbers inherit.</summary>
    public ClipperSpec Over(ClipperSpec parent)
    {
        ArgumentNullException.ThrowIfNull(parent);
        var own = Components.ToDictionary(c => c.Signal, StringComparer.Ordinal);
        var merged = new List<CurveComponent>();
        foreach (CurveComponent p in parent.Components)
        {
            merged.Add(own.Remove(p.Signal, out CurveComponent? c) ? c with { MinSpread = c.MinSpread ?? p.MinSpread } : p);
        }

        merged.AddRange(Components.Where(c => own.ContainsKey(c.Signal)));
        return new ClipperSpec
        {
            Components = merged,
            High = High ?? parent.High,
            Low = Low ?? parent.Low,
            PreMs = PreMs ?? parent.PreMs,
            PostMs = PostMs ?? parent.PostMs,
            RefractoryMs = RefractoryMs ?? parent.RefractoryMs,
            PeakScore = PeakScore ?? parent.PeakScore,
        };
    }
}

/// <summary>One signal in the excitement curve.</summary>
public sealed record CurveComponent
{
    /// <summary>An existing video or audio signal, such as <c>flash.brighten</c> or <c>audio.rms_db</c>.</summary>
    public required string Signal { get; init; }

    public float Weight { get; init; } = 1;

    /// <summary>
    /// The smallest spread (in the signal's own units) used to scale its
    /// z-score, so a signal that is nearly constant (MAD close to 0) does not
    /// turn noise into huge z-scores. Inherited when unset.
    /// </summary>
    public float? MinSpread { get; init; }
}

/// <summary>Where a profile sits in the tree.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ProfileLevelKind>))]
public enum ProfileLevelKind
{
    Game,
    Subgame,
    Mode,
}

/// <summary>
/// Detection for a sub-game or mode. <see cref="Hints"/> are exact facts the
/// platform exposes outside the game process (launch arguments, the window
/// title, the launcher's own log file); any one match decides. Otherwise the
/// <see cref="Visual"/> fingerprint is scored over a sliding window of frames.
/// </summary>
public sealed record DetectSpec
{
    public IReadOnlyList<DetectHint> Hints { get; init; } = [];

    /// <summary>Conditions (above or below) on video signals that hold while this profile is on screen.</summary>
    public IReadOnlyList<Condition> Visual { get; init; } = [];

    /// <summary>Length of the window the fingerprint is averaged over.</summary>
    public double WindowSeconds { get; init; } = 10;

    /// <summary>Average fraction of conditions that must hold, 0..1.</summary>
    public double MinScore { get; init; } = 0.8;

    /// <summary>How long a new fingerprint match must lead before the active profile switches.</summary>
    public double HoldSeconds { get; init; } = 5;
}

/// <summary>A hint whose first capture group is a platform id of the given kind.</summary>
public sealed record IdHint
{
    public required HintSource Source { get; init; }

    public required string Regex { get; init; }

    /// <summary>The id kind the capture is looked up in, such as <c>roblox.universe</c>.</summary>
    public required string Kind { get; init; }
}

/// <summary>Where a hint is read from. All are outside the game process.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<HintSource>))]
public enum HintSource
{
    LaunchArgs,
    WindowTitle,
    Log,
}

/// <summary>
/// A regular expression over a hint source. With <see cref="Values"/>, the
/// first capture group must equal one of the values (for example a Roblox
/// place id); without it, any match counts.
/// </summary>
public sealed record DetectHint
{
    public required HintSource Source { get; init; }

    public required string Regex { get; init; }

    public IReadOnlyList<string> Values { get; init; } = [];
}

/// <summary>
/// What Lookback may do to capture this game. Titles with anti-cheat get
/// external capture only: Windows.Graphics.Capture, no injected present hook,
/// no reading the game's memory.
/// </summary>
public sealed record CapturePolicy
{
    public bool AllowPresentHook { get; init; }

    public bool AllowMemoryReading { get; init; }

    public string Notes { get; init; } = "";
}

/// <summary>How the region positions were obtained.</summary>
public sealed record ProfileReference
{
    public string Aspect { get; init; } = "16:9";

    /// <summary>False until positions have been checked against real footage with the calibration view.</summary>
    public bool Calibrated { get; init; }

    public string Notes { get; init; } = "";
}

/// <summary>A rectangle of the screen, in 0..1 coordinates from the top left.</summary>
public sealed record HudRegion
{
    public required string Name { get; init; }

    public required double X { get; init; }

    public required double Y { get; init; }

    public required double W { get; init; }

    public required double H { get; init; }

    public string Purpose { get; init; } = "";
}

/// <summary>
/// One kind of moment. Fires on a frame where every <see cref="When"/>
/// condition holds and, if <see cref="WhenAny"/> is given, at least one of
/// those holds too. Boosts found within their window around the trigger add
/// confidence.
/// </summary>
public sealed record EventRule
{
    public required string Name { get; init; }

    public float Weight { get; init; } = 1;

    public float BaseConfidence { get; init; } = 0.5f;

    /// <summary>Minimum time between two firings of this rule.</summary>
    public int CooldownMs { get; init; } = 500;

    public IReadOnlyList<Condition> When { get; init; } = [];

    public IReadOnlyList<Condition> WhenAny { get; init; } = [];

    public IReadOnlyList<Boost> Boosts { get; init; } = [];

    public ClipWindow Clip { get; init; } = new();
}

/// <summary>
/// A test on one signal. Exactly one of <see cref="Above"/>, <see cref="Below"/>
/// or <see cref="Rise"/> is set. <see cref="Rise"/> compares the value with
/// its own slow-moving baseline, which makes it robust to a game's palette
/// and brightness.
/// </summary>
public sealed record Condition
{
    public required string Signal { get; init; }

    public float? Above { get; init; }

    public float? Below { get; init; }

    public float? Rise { get; init; }

    /// <summary>Smoothing of the baseline used by <see cref="Rise"/>; smaller is slower.</summary>
    public float BaselineAlpha { get; init; } = 0.05f;

    [JsonIgnore]
    public string Describe => Above is { } a ? $"{Signal} > {a}" : Below is { } b ? $"{Signal} < {b}" : $"{Signal} rises {Rise}";
}

/// <summary>Corroborating evidence near a trigger, such as an audio onset within 250 ms of a kill-feed change.</summary>
public sealed record Boost
{
    public required string Signal { get; init; }

    public float Above { get; init; }

    public int WithinMs { get; init; } = 250;

    public float Add { get; init; } = 0.1f;
}

/// <summary>How much footage to keep around a moment.</summary>
public sealed record ClipWindow
{
    public int PreMs { get; init; } = 8000;

    public int PostMs { get; init; } = 3000;
}

/// <summary>
/// A moment made of other moments: at least <see cref="AtLeast"/> <see cref="Of"/>
/// events within <see cref="WithinMs"/>, for example two eliminations in four
/// seconds.
/// </summary>
public sealed record CompositeRule
{
    public required string Name { get; init; }

    public required string Of { get; init; }

    public int AtLeast { get; init; } = 2;

    public int WithinMs { get; init; } = 4000;

    public float Weight { get; init; } = 2;

    public ClipWindow Clip { get; init; } = new();
}
