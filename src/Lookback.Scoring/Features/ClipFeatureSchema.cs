using Lookback.Contracts.V1;
using Lookback.Core.Time;

namespace Lookback.Scoring.Features;

/// <summary>
/// The game-agnostic feature vector of a clip candidate, version
/// <c>clip-features/1</c>. <see cref="ClipCandidate.Features"/> is positional
/// against <see cref="Names"/> and <see cref="ClipCandidate.FeatureSchema"/>
/// names the version. The same list is committed as <c>ml/feature_schema.json</c>
/// for training; a test keeps the two identical.
/// <para>
/// Never reorder or rename a feature: a model trained on one order would
/// silently read another. Add features by defining a new schema id.
/// </para>
/// </summary>
public static class ClipFeatureSchema
{
    public const string Id = "clip-features/1";

    public const string HeuristicScore = "heuristic_score";
    public const string DurationSeconds = "duration_s";
    public const string EventCount = "event_count";
    public const string EventKinds = "event_kinds";
    public const string MaxContribution = "max_contribution";
    public const string MeanContribution = "mean_contribution";
    public const string EventDensity = "event_density";
    public const string PeakPosition = "peak_position";
    public const string MotionZ = "motion_z";
    public const string FlashZ = "flash_z";
    public const string RedEdgeZ = "red_edge_z";
    public const string HudActivityZ = "hud_activity_z";
    public const string LoudnessZ = "loudness_z";
    public const string OnsetRate = "onset_rate";
    public const string SceneCuts = "scene_cuts";
    public const string BlackFraction = "black_fraction";
    public const string FrozenFraction = "frozen_fraction";
    public const string SilentFraction = "silent_fraction";
    public const string VideoCoverage = "video_coverage";
    public const string AudioCoverage = "audio_coverage";
    public const string CurveMean = "curve_mean";
    public const string CurveMax = "curve_max";
    public const string CurveAboveHigh = "curve_above_high";
    public const string CurvePeaks = "curve_peaks";
    public const string CurvePeakMax = "curve_peak_max";
    public const string EventConfidenceMean = "event_confidence_mean";
    public const string FluxZ = "flux_z";
    public const string SceneCutZ = "scene_cut_z";
    public const string LoudnessZMax = "loudness_z_max";
    public const string MotionZMax = "motion_z_max";

    /// <summary>Feature names in vector order.</summary>
    public static IReadOnlyList<string> Names { get; } =
    [
        HeuristicScore, DurationSeconds, EventCount, EventKinds, MaxContribution, MeanContribution,
        EventDensity, PeakPosition, MotionZ, FlashZ, RedEdgeZ, HudActivityZ, LoudnessZ, OnsetRate,
        SceneCuts, BlackFraction, FrozenFraction, SilentFraction, VideoCoverage, AudioCoverage,
        CurveMean, CurveMax, CurveAboveHigh, CurvePeaks, CurvePeakMax, EventConfidenceMean,
        FluxZ, SceneCutZ, LoudnessZMax, MotionZMax,
    ];

    public static int Count => Names.Count;

    private static readonly Dictionary<string, int> Index =
        Names.Select((n, i) => (n, i)).ToDictionary(x => x.n, x => x.i, StringComparer.Ordinal);

    /// <summary>The position of <paramref name="name"/>, or throws for an unknown name.</summary>
    public static int IndexOf(string name) =>
        Index.TryGetValue(name, out int i) ? i : throw new ArgumentException($"'{name}' is not a {Id} feature.", nameof(name));

    /// <summary>A vector of the right length with every feature missing (NaN).</summary>
    public static float[] Empty()
    {
        var v = new float[Count];
        Array.Fill(v, float.NaN);
        return v;
    }

    /// <summary>
    /// The features a candidate carries on its own (heuristic score, duration
    /// and peak position); everything that needs the session's signals is NaN.
    /// The clipper fills the full vector; this is the floor any candidate has.
    /// </summary>
    public static float[] FromCandidate(ClipCandidate candidate, long qpcFrequency)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(qpcFrequency);
        float[] v = Empty();
        v[IndexOf(HeuristicScore)] = candidate.HeuristicScore;
        long length = (long)candidate.EndQpc - (long)candidate.StartQpc;
        if (length > 0)
        {
            v[IndexOf(DurationSeconds)] = (float)QpcTime.ToTimeSpan(length, qpcFrequency).TotalSeconds;
            if (candidate.PeakQpc >= candidate.StartQpc && candidate.PeakQpc <= candidate.EndQpc)
            {
                v[IndexOf(PeakPosition)] = (float)(((long)candidate.PeakQpc - (long)candidate.StartQpc) / (double)length);
            }
        }

        return v;
    }

    /// <summary>Writes <paramref name="features"/> onto <paramref name="candidate"/> with this schema's id.</summary>
    public static void Attach(ClipCandidate candidate, ReadOnlySpan<float> features)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (features.Length != Count)
        {
            throw new ArgumentException($"{Id} has {Count} features, got {features.Length}.", nameof(features));
        }

        candidate.Features.Clear();
        candidate.Features.Add(features.ToArray());
        candidate.FeatureSchema = Id;
    }

    /// <summary>True when the candidate carries a complete vector of this schema.</summary>
    public static bool Carries(ClipCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        return candidate.FeatureSchema == Id && candidate.Features.Count == Count;
    }
}
