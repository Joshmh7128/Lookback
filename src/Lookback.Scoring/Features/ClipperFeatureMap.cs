namespace Lookback.Scoring.Features;

/// <summary>
/// Maps the clipper's named features (<c>Lookback.Clipper.CandidateFeatures</c>,
/// version <c>clipper-features/1</c>) onto <see cref="ClipFeatureSchema"/>.
/// Works on names, so neither project references the other: the clipper (or
/// whoever hosts it) passes its dictionary, attaches the result with
/// <see cref="ClipFeatureSchema.Attach"/>, scores, and only then publishes.
/// A name the clipper does not provide stays NaN, and so does a non-finite value.
/// </summary>
public static class ClipperFeatureMap
{
    /// <summary>The clipper feature version this map reads.</summary>
    public const string ClipperSchema = "clipper-features/1";

    /// <summary>Our feature ← the clipper's name, where one is a direct copy.</summary>
    public static IReadOnlyDictionary<string, string> Direct { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [ClipFeatureSchema.HeuristicScore] = "score.heuristic",
        [ClipFeatureSchema.DurationSeconds] = "window.seconds",
        [ClipFeatureSchema.PeakPosition] = "window.peak_position",
        [ClipFeatureSchema.EventCount] = "events.count",
        [ClipFeatureSchema.EventKinds] = "events.kinds",
        [ClipFeatureSchema.MaxContribution] = "events.score_max",
        [ClipFeatureSchema.EventConfidenceMean] = "events.confidence_mean",
        [ClipFeatureSchema.MotionZ] = "z_mean.motion.energy",
        [ClipFeatureSchema.FlashZ] = "z_mean.flash.brighten",
        [ClipFeatureSchema.RedEdgeZ] = "z_mean.flash.red_edge",
        [ClipFeatureSchema.HudActivityZ] = "z_mean.hud.activity",
        [ClipFeatureSchema.LoudnessZ] = "z_mean.audio.rms_db",
        [ClipFeatureSchema.FluxZ] = "z_mean.spectral.flux",
        [ClipFeatureSchema.SceneCutZ] = "z_mean.scene.cut",
        [ClipFeatureSchema.LoudnessZMax] = "z_max.audio.rms_db",
        [ClipFeatureSchema.MotionZMax] = "z_max.motion.energy",
        [ClipFeatureSchema.BlackFraction] = "qa.black",
        [ClipFeatureSchema.FrozenFraction] = "qa.frozen",
        [ClipFeatureSchema.SilentFraction] = "qa.silent",
        [ClipFeatureSchema.CurveMean] = "curve.mean",
        [ClipFeatureSchema.CurveMax] = "curve.max",
        [ClipFeatureSchema.CurveAboveHigh] = "curve.above_high",
        [ClipFeatureSchema.CurvePeaks] = "curve.peaks",
        [ClipFeatureSchema.CurvePeakMax] = "curve.peak_max",
    };

    /// <summary>
    /// A <see cref="ClipFeatureSchema"/> vector from the clipper's features.
    /// Derived: mean contribution (events.score_sum / events.count, 0 with no
    /// events) and event density (events.count / window.seconds). Not provided
    /// by the clipper, so NaN: onset rate, scene cuts per second and coverage.
    /// </summary>
    public static float[] FromClipper(IReadOnlyDictionary<string, float> clipper)
    {
        ArgumentNullException.ThrowIfNull(clipper);
        float[] v = ClipFeatureSchema.Empty();
        foreach ((string ours, string theirs) in Direct)
        {
            v[ClipFeatureSchema.IndexOf(ours)] = Get(clipper, theirs);
        }

        float count = Get(clipper, "events.count");
        float sum = Get(clipper, "events.score_sum");
        float seconds = Get(clipper, "window.seconds");
        if (float.IsFinite(count))
        {
            v[ClipFeatureSchema.IndexOf(ClipFeatureSchema.MeanContribution)] = count > 0 ? (float.IsFinite(sum) ? sum / count : float.NaN) : 0;
            if (float.IsFinite(seconds) && seconds > 0)
            {
                v[ClipFeatureSchema.IndexOf(ClipFeatureSchema.EventDensity)] = count / seconds;
            }
        }

        return v;
    }

    private static float Get(IReadOnlyDictionary<string, float> d, string name) =>
        d.TryGetValue(name, out float x) && float.IsFinite(x) ? x : float.NaN;
}
