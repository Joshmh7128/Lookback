using Lookback.Contracts.V1;
using Lookback.Scoring.Features;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lookback.Recorder.Tests;

/// <summary>
/// The recorder's clip annotation: every candidate leaves the clipper with its
/// <c>clip-features/1</c> vector (what a stored rating trains on), and a model
/// score when the learned scorer covers its game. The model is the committed
/// fixture the Python tests trained (games <c>synth-a</c> and <c>synth-b</c>).
/// </summary>
public sealed class ClipScoringTests
{
    private const long Freq = 10_000_000;

    private static string FixtureModel => Path.Combine(AppContext.BaseDirectory, "Fixtures", "model");

    /// <summary>Clipper features by name, as Clipper v1 reports them for a candidate.</summary>
    private static Dictionary<string, float> ClipperFeatures() => new()
    {
        ["score.heuristic"] = 0.7f,
        ["window.seconds"] = 9,
        ["window.peak_position"] = 0.4f,
        ["events.count"] = 3,
        ["events.kinds"] = 2,
        ["events.score_sum"] = 2.1f,
        ["events.score_max"] = 0.9f,
        ["events.confidence_mean"] = 0.8f,
        ["z_mean.motion.energy"] = 2.5f,
        ["z_mean.flash.brighten"] = 1.5f,
        ["z_mean.audio.rms_db"] = 1.2f,
        ["z_max.audio.rms_db"] = 4,
        ["z_max.motion.energy"] = 5,
        ["qa.black"] = 0,
        ["qa.frozen"] = 0,
        ["qa.silent"] = 0.1f,
        ["curve.mean"] = 1.1f,
        ["curve.max"] = 2.4f,
        ["curve.above_high"] = 0.3f,
        ["curve.peaks"] = 2,
        ["curve.peak_max"] = 2.4f,
    };

    private static ClipCandidate Candidate(string profile) => new()
    {
        CandidateId = "c1",
        StartQpc = 10 * Freq,
        EndQpc = 19 * Freq,
        PeakQpc = 13 * Freq,
        HeuristicScore = 0.7f,
        HighlightProbability = 0.7f,
        GameProfile = profile,
    };

    [Fact]
    public void Without_a_model_every_candidate_still_gets_its_features_and_keeps_its_heuristic()
    {
        using ClipScoring scoring = ClipScoring.Load(Path.Combine(Path.GetTempPath(), "lookback-no-model-" + Guid.NewGuid().ToString("N")), NullLogger.Instance);
        Assert.False(scoring.HasModel);
        ClipCandidate c = Candidate("overwatch");
        var qa = new QaReport();

        scoring.Annotate(c, qa, ClipperFeatures(), Freq);

        Assert.Equal(ClipFeatureSchema.Id, c.FeatureSchema);
        Assert.Equal(ClipFeatureSchema.Count, c.Features.Count);
        Assert.True(c.Features.Count(float.IsFinite) > 20, "a rating should be stored with the clipper's features, not just the three floor ones");
        Assert.Equal(2.5f, c.Features[ClipFeatureSchema.IndexOf(ClipFeatureSchema.MotionZ)]);
        Assert.Equal(0f, c.ModelScore);
        Assert.Equal(0.7f, c.HighlightProbability);
    }

    [Fact]
    public void With_a_model_a_candidate_of_a_game_it_knows_gets_a_model_score_and_others_keep_the_heuristic()
    {
        using ClipScoring scoring = ClipScoring.Load(FixtureModel, NullLogger.Instance);
        Assert.True(scoring.HasModel);

        ClipCandidate known = Candidate("synth-a.mode1");
        var qa = new QaReport();
        scoring.Annotate(known, qa, ClipperFeatures(), Freq);
        Assert.InRange(known.ModelScore, 0.0001f, 0.9999f);
        Assert.InRange(known.HighlightProbability, 0f, 1f);
        Assert.Equal(known.HighlightProbability, qa.HighlightProbability);

        ClipCandidate other = Candidate("overwatch");
        scoring.Annotate(other, new QaReport(), ClipperFeatures(), Freq);
        Assert.Equal(0f, other.ModelScore);
        Assert.Equal(0.7f, other.HighlightProbability);
        Assert.Equal(ClipFeatureSchema.Count, other.Features.Count); // features either way
    }

    [Fact]
    public void Clipper_options_carry_the_annotation()
    {
        using ClipScoring scoring = ClipScoring.HeuristicOnly();
        Assert.NotNull(scoring.Options(Freq).Annotate);
    }
}
