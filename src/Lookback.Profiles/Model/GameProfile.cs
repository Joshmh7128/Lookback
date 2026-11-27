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
