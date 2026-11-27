namespace Lookback.Testbench.Browse;

/// <summary>Everything the dataset and profile browser shows, embedded in the page as JSON.</summary>
internal sealed record BrowserData(
    IReadOnlyList<string> Games,
    IReadOnlyList<ProfileView> Profiles,
    IReadOnlyList<DatasetView> Datasets,
    IReadOnlyList<SignalInfo> Signals,
    IReadOnlyList<string> Notes);

/// <summary>A signal the pipeline produces, with what it measures.</summary>
internal sealed record SignalInfo(string Name, string Family, string Meaning);

/// <summary>One profile, resolved (everything inherited applied), with where each part came from.</summary>
internal sealed record ProfileView
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required string Level { get; init; }
    public string? Parent { get; init; }
    public required IReadOnlyList<string> Path { get; init; }
    public required IReadOnlyList<string> Children { get; init; }
    public int Version { get; init; }
    public IReadOnlyList<string> Executables { get; init; } = [];
    public required CaptureView Capture { get; init; }
    public required ReferenceView Reference { get; init; }
    public required DetectView Detect { get; init; }
    public IReadOnlyList<RegionView> Regions { get; init; } = [];
    public IReadOnlyList<EventView> Events { get; init; } = [];
    public IReadOnlyList<CompositeView> Composites { get; init; } = [];
    public IReadOnlyList<string> Disabled { get; init; } = [];
    public IReadOnlyList<ComponentView> Components { get; init; } = [];
    public required CoverageView Coverage { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

internal sealed record CaptureView(bool AllowPresentHook, bool AllowMemoryReading, string Notes);

internal sealed record ReferenceView(string Aspect, bool Calibrated, string Notes);

internal sealed record DetectView(
    IReadOnlyList<HintView> Hints,
    IReadOnlyList<ConditionView> Visual,
    double WindowSeconds,
    double MinScore,
    double HoldSeconds,
    string Summary);

internal sealed record HintView(string Source, string Regex, IReadOnlyList<string> Values);

/// <summary>A condition, with a plain-language reading.</summary>
internal sealed record ConditionView(string Signal, string Test, double Value, string Text);

internal sealed record RegionView(string Name, double X, double Y, double W, double H, string Purpose, string From, IReadOnlyList<string> UsedBy);

internal sealed record BoostView(string Signal, double Above, int WithinMs, double Add, string Text);

/// <summary>
/// One recognised moment. <see cref="From"/> is the profile that defines it
/// (this one, or an ancestor); <see cref="Replaces"/> is set when this
/// profile redefines an inherited event of the same name.
/// </summary>
internal sealed record EventView(
    string Name,
    double Weight,
    double BaseConfidence,
    double MaxConfidence,
    int CooldownMs,
    IReadOnlyList<ConditionView> When,
    IReadOnlyList<ConditionView> WhenAny,
    IReadOnlyList<BoostView> Boosts,
    int PreMs,
    int PostMs,
    string From,
    string? Replaces,
    string Summary,
    IReadOnlyList<string> UsedByComposites);

internal sealed record CompositeView(string Name, string Of, int AtLeast, int WithinMs, double Weight, int PreMs, int PostMs, string From, string Summary);

/// <summary>
/// A signal the profile depends on, ranked by how much rides on it: the
/// summed weight of the events that trigger on it or are boosted by it, plus
/// its use in the detection fingerprint.
/// </summary>
internal sealed record ComponentView(
    string Signal,
    string Kind,
    string? Region,
    IReadOnlyList<string> Triggers,
    IReadOnlyList<string> Boosts,
    bool Fingerprint,
    double Influence,
    bool Known);

/// <summary>How well the datasets exercise this profile.</summary>
internal sealed record CoverageView(
    int Entries,
    IReadOnlyDictionary<string, int> LabelsByEvent,
    IReadOnlyList<string> Unlabelled,
    IReadOnlyList<string> UnknownLabels,
    IReadOnlyList<ScoreView> Scores);

internal sealed record ScoreView(string Event, int Tp, int Fp, int Fn);

internal sealed record DatasetView(
    string Game,
    string Manifest,
    bool Example,
    IReadOnlyList<EntryView> Entries,
    ResultsView? Results,
    IReadOnlyList<string> Problems);

internal sealed record EntryView
{
    public required int Index { get; init; }
    public required string File { get; init; }
    public string? Title { get; init; }
    public string Source { get; init; } = "";
    public required string Retention { get; init; }
    public string PermissionRef { get; init; } = "";
    public string? Profile { get; init; }
    public IReadOnlyList<LabelView> Labels { get; init; } = [];
    public IReadOnlyList<ClipView> Clips { get; init; } = [];
    public bool MediaFound { get; init; }
    public EntryResultView? Result { get; init; }
    public IReadOnlyList<string> Issues { get; init; } = [];
}

internal sealed record LabelView(string Event, double At);

internal sealed record ClipView(double Start, double End);

internal sealed record ResultsView(string Source, double Tolerance, IReadOnlyList<ScoreView> Overall, double? DetectionAccuracy, CandidateView? Candidates);

internal sealed record CandidateView(int Tp, int Fp, int Fn, double MeanIoU);

internal sealed record EntryResultView(
    int Events,
    string? Report,
    string? FinalProfile,
    double? DetectionAccuracy,
    IReadOnlyList<ScoreView> Scores,
    CandidateView? Candidates);
