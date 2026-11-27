using Lookback.Contracts.V1;
using Lookback.Scoring.Features;

namespace Lookback.Scoring.Tests;

public sealed class ClipperFeatureMapTests
{
    // The names Clipper v1 documents for clipper-features/1 with its default curve components.
    private static readonly string[] ClipperNames =
    [
        "score.heuristic", "score.raw", "window.seconds", "window.peak_position", "source.events", "source.curve",
        "events.count", "events.kinds", "events.score_sum", "events.score_max", "events.confidence_mean",
        "events.confidence_max", "events.weight_max", "curve.peaks", "curve.peak_max", "curve.score_sum",
        "curve.mean", "curve.max", "curve.above_high", "qa.black", "qa.frozen", "qa.silent", "qa.passed",
        .. new[] { "flash.brighten", "audio.rms_db", "spectral.flux", "motion.energy", "hud.activity", "flash.red_edge", "scene.cut" }
            .SelectMany(c => new[] { $"z_mean.{c}", $"z_max.{c}" }),
    ];

    private static Dictionary<string, float> Full() =>
        ClipperNames.Select((n, i) => (n, (float)(i + 1))).ToDictionary(x => x.n, x => x.Item2, StringComparer.Ordinal);

    [Fact]
    public void Every_mapping_reads_a_documented_clipper_name_into_a_schema_feature()
    {
        Assert.All(ClipperFeatureMap.Direct, kv =>
        {
            Assert.Contains(kv.Key, ClipFeatureSchema.Names);
            Assert.Contains(kv.Value, ClipperNames);
        });
    }

    [Fact]
    public void Full_clipper_features_fill_all_but_what_the_clipper_does_not_measure()
    {
        Dictionary<string, float> c = Full();
        float[] v = ClipperFeatureMap.FromClipper(c);

        Assert.Equal(ClipFeatureSchema.Count, v.Length);
        foreach ((string ours, string theirs) in ClipperFeatureMap.Direct)
        {
            Assert.Equal(c[theirs], v[ClipFeatureSchema.IndexOf(ours)]);
        }

        Assert.Equal(c["events.score_sum"] / c["events.count"], v[ClipFeatureSchema.IndexOf(ClipFeatureSchema.MeanContribution)]);
        Assert.Equal(c["events.count"] / c["window.seconds"], v[ClipFeatureSchema.IndexOf(ClipFeatureSchema.EventDensity)]);
        string[] missing = [.. ClipFeatureSchema.Names.Where((_, i) => float.IsNaN(v[i]))];
        Assert.Equal([ClipFeatureSchema.OnsetRate, ClipFeatureSchema.SceneCuts, ClipFeatureSchema.VideoCoverage, ClipFeatureSchema.AudioCoverage], missing);
    }

    [Fact]
    public void Curve_only_candidate_has_zero_mean_contribution_and_missing_names_stay_nan()
    {
        float[] v = ClipperFeatureMap.FromClipper(new Dictionary<string, float>
        {
            ["score.heuristic"] = 0.4f,
            ["window.seconds"] = 8,
            ["events.count"] = 0,
            ["z_mean.motion.energy"] = float.PositiveInfinity,
        });

        Assert.Equal(0.4f, v[ClipFeatureSchema.IndexOf(ClipFeatureSchema.HeuristicScore)]);
        Assert.Equal(0f, v[ClipFeatureSchema.IndexOf(ClipFeatureSchema.MeanContribution)]);
        Assert.Equal(0f, v[ClipFeatureSchema.IndexOf(ClipFeatureSchema.EventDensity)]);
        Assert.True(float.IsNaN(v[ClipFeatureSchema.IndexOf(ClipFeatureSchema.MotionZ)]));
        Assert.True(float.IsNaN(v[ClipFeatureSchema.IndexOf(ClipFeatureSchema.CurveMax)]));
    }

    [Fact]
    public void Mapped_features_score_through_the_fixture_model()
    {
        using LearnedClipScorer scorer = LearnedClipScorer.Load(Path.Combine(TestData.Fixtures, "model"));
        var candidate = new ClipCandidate { CandidateId = "x", GameProfile = "synth-a", HeuristicScore = 0.5f };
        ClipFeatureSchema.Attach(candidate, ClipperFeatureMap.FromClipper(Full()));

        Assert.Equal(ScoreSource.Model, scorer.Score(candidate).Source);
        Assert.InRange(candidate.HighlightProbability, 0, 1);
    }
}
