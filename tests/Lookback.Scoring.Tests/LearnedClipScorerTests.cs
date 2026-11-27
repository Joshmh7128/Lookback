using System.Text.Json;
using Lookback.Contracts.V1;
using Lookback.Scoring.Features;
using Lookback.Scoring.Model;

namespace Lookback.Scoring.Tests;

public sealed class LearnedClipScorerTests
{
    private static string ModelDir => Path.Combine(TestData.Fixtures, "model");

    private sealed record ExpectedCase(string Profile, IReadOnlyList<float?> Features, double? Raw, double? Calibrated);

    private sealed record Expected(double Tolerance, IReadOnlyList<ExpectedCase> Cases);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static Expected LoadExpected() =>
        JsonSerializer.Deserialize<Expected>(File.ReadAllText(Path.Combine(TestData.Fixtures, "expected.json")), Json)!;

    private static ClipCandidate CandidateFor(ExpectedCase c)
    {
        var candidate = new ClipCandidate { CandidateId = "x", GameProfile = c.Profile, HeuristicScore = 0.3f, HighlightProbability = 0.3f };
        ClipFeatureSchema.Attach(candidate, [.. c.Features.Select(v => v ?? float.NaN)]);
        return candidate;
    }

    [Fact]
    public void Inference_and_calibration_match_python()
    {
        Expected expected = LoadExpected();
        using LearnedClipScorer scorer = LearnedClipScorer.Load(ModelDir);
        Assert.Contains(expected.Cases, c => c.Raw is null);
        Assert.Contains(expected.Cases, c => c.Features.Any(v => v is null));

        foreach (ExpectedCase c in expected.Cases)
        {
            ClipCandidate candidate = CandidateFor(c);
            ScoreOutcome outcome = scorer.Score(candidate);
            if (c.Raw is null)
            {
                Assert.Equal(ScoreSource.Heuristic, outcome.Source);
                Assert.False(candidate.HasModelScore);
                Assert.Equal(0.3f, candidate.HighlightProbability);
                continue;
            }

            Assert.Equal(ScoreSource.Model, outcome.Source);
            Assert.Equal(c.Raw.Value, candidate.ModelScore, expected.Tolerance);
            Assert.Equal(c.Calibrated!.Value, candidate.HighlightProbability, expected.Tolerance);

            (double raw, double calibrated) = scorer.Predict([.. c.Features.Select(v => v ?? float.NaN)], c.Profile)!.Value;
            Assert.Equal(c.Raw.Value, raw, expected.Tolerance);
            Assert.Equal(c.Calibrated.Value, calibrated, 1e-9 + expected.Tolerance);
        }
    }

    [Fact]
    public void Calibration_lookup_is_deepest_profile_first()
    {
        ScorerManifest m = ScorerManifest.Load(Path.Combine(ModelDir, LearnedClipScorer.ManifestFileName));
        Assert.Same(m.Calibration.Profiles["synth-a.mode1"], m.CalibrationFor("synth-a.mode1.extra"));
        Assert.Same(m.Calibration.Profiles["synth-a"], m.CalibrationFor("synth-a.unlisted"));
        Assert.Same(m.Calibration.Profiles["synth-b"], m.CalibrationFor("synth-b"));
        Assert.Null(m.CalibrationFor("other-game"));
        Assert.Null(m.CalibrationFor(""));
    }

    [Fact]
    public void Platt_identity_and_clamping()
    {
        Assert.Equal(0.37, PlattCalibration.Identity.Apply(0.37), 12);
        Assert.InRange(new PlattCalibration(1, 0).Apply(0), 0, 1e-5);
        Assert.InRange(new PlattCalibration(1, 0).Apply(1), 1 - 1e-5, 1);
        Assert.Equal(0.5, new PlattCalibration(2, 0).Apply(0.5), 12);
    }

    [Fact]
    public void Heuristic_stands_without_a_model()
    {
        using LearnedClipScorer scorer = LearnedClipScorer.LoadOrHeuristic(Path.Combine(TestData.Fixtures, "no-such-dir"));
        ClipCandidate c = TestData.Candidate(profile: "synth-a");
        c.ModelScore = 0.99f; // stale from somewhere else

        ScoreOutcome outcome = scorer.Score(c);

        Assert.False(scorer.HasModel);
        Assert.Equal(ScoreSource.Heuristic, outcome.Source);
        Assert.False(c.HasModelScore);
        Assert.Equal(0.62f, c.HighlightProbability);
    }

    [Fact]
    public void Broken_model_is_reported_and_skipped()
    {
        string dir = TestData.TempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, LearnedClipScorer.ManifestFileName), File.ReadAllText(Path.Combine(ModelDir, LearnedClipScorer.ManifestFileName)));
            File.WriteAllBytes(Path.Combine(dir, LearnedClipScorer.ModelFileName), [1, 2, 3, 4]);
            var warnings = new List<string>();

            using LearnedClipScorer scorer = LearnedClipScorer.LoadOrHeuristic(dir, warnings.Add);

            Assert.False(scorer.HasModel);
            Assert.Single(warnings);
            Assert.Throws<InvalidDataException>(() => LearnedClipScorer.Load(dir).Dispose());

            File.WriteAllText(Path.Combine(dir, LearnedClipScorer.ManifestFileName), "{ \"format\": \"something-else/9\" }");
            Assert.Throws<InvalidDataException>(() => LearnedClipScorer.Load(dir).Dispose());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Wrong_schema_keeps_the_heuristic_but_floor_features_score()
    {
        using LearnedClipScorer scorer = LearnedClipScorer.Load(ModelDir);
        var c = new ClipCandidate { CandidateId = "x", GameProfile = "synth-a", HeuristicScore = 0.4f, HighlightProbability = 0.4f };
        c.Features.Add([1, 2, 3]);
        c.FeatureSchema = "clip-features/999";
        Assert.Equal(ScoreSource.Heuristic, scorer.Score(c, TestData.Frequency).Source);
        Assert.Equal(0.4f, c.HighlightProbability);

        ClipCandidate bare = TestData.Candidate(profile: "synth-a", withFeatures: false);
        Assert.Equal(ScoreSource.Heuristic, scorer.Score(bare).Source);           // no frequency, no features
        Assert.Equal(ScoreSource.Model, scorer.Score(bare, TestData.Frequency).Source);
        Assert.True(bare.HasModelScore);
        Assert.InRange(bare.HighlightProbability, 0, 1);
    }

    [Fact]
    public void Qa_report_gets_the_highlight_probability()
    {
        using LearnedClipScorer scorer = LearnedClipScorer.Load(ModelDir);
        ClipCandidate c = CandidateFor(LoadExpected().Cases.First(x => x.Raw is not null));
        scorer.Score(c);
        var qa = new QaReport { CandidateId = c.CandidateId };

        LearnedClipScorer.Annotate(qa, c);

        Assert.Equal(c.HighlightProbability, qa.HighlightProbability);
    }

    [Fact]
    public void Scoring_is_thread_safe()
    {
        using LearnedClipScorer scorer = LearnedClipScorer.Load(ModelDir);
        ExpectedCase c = LoadExpected().Cases.First(x => x.Raw is not null);
        Parallel.For(0, 64, _ =>
        {
            ClipCandidate candidate = CandidateFor(c);
            scorer.Score(candidate);
            Assert.Equal(c.Calibrated!.Value, candidate.HighlightProbability, 1e-5);
        });
    }
}
