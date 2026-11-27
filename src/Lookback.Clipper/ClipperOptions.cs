using System.Reflection;
using Lookback.Profiles.Model;

namespace Lookback.Clipper;

/// <summary>
/// Engine settings that are the same for every game: how the normaliser,
/// smoothing, merging and QA work. What differs per game (component weights,
/// thresholds, windows) is data in a <see cref="ClipperSpec"/>.
/// </summary>
public sealed record ClipperOptions
{
    /// <summary>The rolling window the median and MAD of each signal are taken over.</summary>
    public double NormalisationWindowSeconds { get; init; } = 120;

    /// <summary>A signal contributes nothing until this much of its history has been seen.</summary>
    public double WarmupSeconds { get; init; } = 5;

    /// <summary>How often a sample is added to each signal's rolling window (and its median and MAD recomputed).</summary>
    public int StatsIntervalMs { get; init; } = 250;

    /// <summary>Robust z-scores are clamped to 0..this, so one wild signal cannot swamp the curve.</summary>
    public float ZCap { get; init; } = 6;

    /// <summary>Envelope follower on the curve: time constant while rising.</summary>
    public int AttackMs { get; init; } = 40;

    /// <summary>Envelope follower on the curve: time constant while falling.</summary>
    public int ReleaseMs { get; init; } = 400;

    /// <summary>Windows closer than this are merged into one candidate.</summary>
    public int MergeGapMs { get; init; } = 1500;

    /// <summary>A merged candidate never grows past this.</summary>
    public int MaxLengthMs { get; init; } = 60_000;

    /// <summary>How much QA history (black, frozen and silence flags) is kept; must cover a candidate's length plus the clipper's latency.</summary>
    public double HistorySeconds { get; init; } = 300;

    /// <summary>The runner publishes the curve on <c>signals.clipper</c> this often; 0 turns it off.</summary>
    public int CurvePublishIntervalMs { get; init; } = 100;

    public QaOptions Qa { get; init; } = new();

    /// <summary>
    /// Called by <see cref="ClipperRunner"/> for every finished candidate just
    /// <b>before</b> its <c>clip.candidate</c> and <c>qa.report</c> are
    /// published, so the host can put more on them, for example the learned
    /// scorer's features and score (M4), without this project referencing
    /// the scorer. Runs on the runner's pump thread under its lock: keep it
    /// quick. If it throws, the candidate is published as it was and
    /// <see cref="ClipperRunner.AnnotationFailures"/> counts it.
    /// </summary>
    public CandidateAnnotator? Annotate { get; init; }

    /// <summary>Checks that the options make sense.</summary>
    public void Validate()
    {
        if (NormalisationWindowSeconds <= 0 || WarmupSeconds < 0 || WarmupSeconds > NormalisationWindowSeconds || StatsIntervalMs <= 0)
        {
            throw new ArgumentException("Normalisation: window must be positive, warm-up between 0 and the window, interval positive.");
        }

        if (ZCap <= 0 || AttackMs <= 0 || ReleaseMs <= 0 || MergeGapMs < 0 || MaxLengthMs <= 0 || HistorySeconds <= 0 || CurvePublishIntervalMs < 0)
        {
            throw new ArgumentException("Clipper options must be positive (merge gap and curve interval may be 0).");
        }

        Qa.Validate();
    }
}

/// <summary>QA gates v1: each candidate is checked against existing signals only.</summary>
public sealed record QaOptions
{
    public int MinLengthMs { get; init; } = 4000;

    public int MaxLengthMs { get; init; } = 90_000;

    /// <summary>Fails when at least this fraction of the window's frames is black (<c>luma.black</c>).</summary>
    public double MaxBlackFraction { get; init; } = 0.5;

    /// <summary>Fails when at least this fraction of the window's frames is frozen (see <see cref="FrozenSeconds"/>).</summary>
    public double MaxFrozenFraction { get; init; } = 0.5;

    /// <summary>A frame counts as frozen once the picture has been static (<c>motion.frozen_s</c>) for this long.</summary>
    public float FrozenSeconds { get; init; } = 0.5f;

    /// <summary>Fails when at least this fraction of the window's audio hops is silent (<c>audio.silent</c>): silent throughout.</summary>
    public double MaxSilentFraction { get; init; } = 0.99;

    public void Validate()
    {
        if (MinLengthMs < 0 || MaxLengthMs <= MinLengthMs || FrozenSeconds <= 0
            || MaxBlackFraction is <= 0 or > 1 || MaxFrozenFraction is <= 0 or > 1 || MaxSilentFraction is <= 0 or > 1)
        {
            throw new ArgumentException("QA options: lengths must be ordered, fractions in (0, 1], frozen seconds positive.");
        }
    }
}

/// <summary>The default excitement curve, loaded from the embedded <c>clipper.defaults.json</c>.</summary>
public static class ClipperDefaults
{
    /// <summary>Fully specified: every component has a weight and spread, every number is set.</summary>
    public static ClipperSpec Spec { get; } = Load();

    private static ClipperSpec Load()
    {
        using System.IO.Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("clipper.defaults.json")
            ?? throw new InvalidOperationException("clipper.defaults.json is not embedded.");
        using var reader = new StreamReader(stream);
        ClipperSpec spec = GameProfileLoader.ParseClipper(reader.ReadToEnd(), "clipper.defaults.json");
        if (spec.High is null || spec.Low is null || spec.PreMs is null || spec.PostMs is null || spec.RefractoryMs is null || spec.PeakScore is null
            || spec.Components.Any(c => c.MinSpread is null))
        {
            throw new InvalidDataException("clipper.defaults.json must set every number and every component's minSpread.");
        }

        return spec;
    }
}
