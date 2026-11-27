using System.Text.Json;
using Lookback.Contracts.V1;
using Lookback.Scoring.Features;

namespace Lookback.Scoring.Tests;

public sealed class ClipFeatureSchemaTests
{
    [Fact]
    public void Matches_ml_feature_schema_json()
    {
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(TestData.Fixtures, "feature_schema.json")));
        Assert.Equal(ClipFeatureSchema.Id, doc.RootElement.GetProperty("id").GetString());
        string[] names = [.. doc.RootElement.GetProperty("features").EnumerateArray().Select(f => f.GetProperty("name").GetString()!)];
        Assert.Equal(names, ClipFeatureSchema.Names);
    }

    [Fact]
    public void Committed_files_are_the_ones_the_tests_copy()
    {
        string root = TestData.RepoRoot();
        Assert.Equal(
            File.ReadAllText(Path.Combine(root, "ml", "feature_schema.json")),
            File.ReadAllText(Path.Combine(TestData.Fixtures, "feature_schema.json")));
    }

    [Fact]
    public void Floor_features_come_from_the_candidate_alone()
    {
        ClipCandidate c = TestData.Candidate(withFeatures: false);
        float[] v = ClipFeatureSchema.FromCandidate(c, TestData.Frequency);

        Assert.Equal(ClipFeatureSchema.Count, v.Length);
        Assert.Equal(0.62f, v[ClipFeatureSchema.IndexOf(ClipFeatureSchema.HeuristicScore)]);
        Assert.Equal(12f, v[ClipFeatureSchema.IndexOf(ClipFeatureSchema.DurationSeconds)]);
        Assert.Equal(0.5f, v[ClipFeatureSchema.IndexOf(ClipFeatureSchema.PeakPosition)]);
        Assert.Equal(ClipFeatureSchema.Count - 3, v.Count(float.IsNaN));
    }

    [Fact]
    public void Attach_sets_the_schema_and_rejects_the_wrong_length()
    {
        var c = new ClipCandidate();
        Assert.False(ClipFeatureSchema.Carries(c));
        ClipFeatureSchema.Attach(c, ClipFeatureSchema.Empty());
        Assert.True(ClipFeatureSchema.Carries(c));
        Assert.Equal(ClipFeatureSchema.Id, c.FeatureSchema);
        Assert.Throws<ArgumentException>(() => ClipFeatureSchema.Attach(c, new float[3]));
        Assert.Throws<ArgumentException>(() => ClipFeatureSchema.IndexOf("nope"));
    }

    [Fact]
    public void New_fields_survive_a_round_trip()
    {
        ClipCandidate c = TestData.Candidate();
        var e = new Envelope
        {
            ClipRating = new ClipRating { CandidateId = "a", SessionId = "s", Stars = 5 },
        };
        Assert.Equal(e, Envelope.Parser.ParseFrom(Google.Protobuf.MessageExtensions.ToByteArray(e)));
        Assert.Equal(c, ClipCandidate.Parser.ParseFrom(Google.Protobuf.MessageExtensions.ToByteArray(c)));
        var qa = new QaReport { CandidateId = "a", HighlightProbability = 0.7f };
        Assert.Equal(0.7f, QaReport.Parser.ParseFrom(Google.Protobuf.MessageExtensions.ToByteArray(qa)).HighlightProbability);
    }
}
