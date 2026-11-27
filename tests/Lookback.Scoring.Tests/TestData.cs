using Lookback.Contracts.V1;
using Lookback.Scoring.Features;

namespace Lookback.Scoring.Tests;

internal static class TestData
{
    public const long Frequency = 10_000_000;

    public static string Fixtures => Path.Combine(AppContext.BaseDirectory, "Fixtures");

    public static SessionStarted Session(
        Retention retention = Retention.Owned,
        SourceKind kind = SourceKind.File,
        string permissionRef = "",
        string game = "overwatch") => new()
        {
            SourceKind = kind,
            Retention = retention,
            PermissionRef = permissionRef,
            QpcFrequency = Frequency,
            GameProfile = game,
        };

    /// <summary>The candidate behind ml/tests/data/rating_sample.jsonl.</summary>
    public static ClipCandidate Candidate(string id = "s1-0001", string profile = "overwatch.push", bool withFeatures = true)
    {
        var c = new ClipCandidate
        {
            CandidateId = id,
            StartQpc = 1 * Frequency,
            EndQpc = 13 * Frequency,
            PeakQpc = 7 * Frequency,
            HeuristicScore = 0.62f,
            HighlightProbability = 0.62f,
            GameProfile = profile,
        };
        if (withFeatures)
        {
            float[] f = ClipFeatureSchema.Empty();
            f[ClipFeatureSchema.IndexOf(ClipFeatureSchema.HeuristicScore)] = 0.62f;
            f[ClipFeatureSchema.IndexOf(ClipFeatureSchema.DurationSeconds)] = 12;
            f[ClipFeatureSchema.IndexOf(ClipFeatureSchema.EventKinds)] = 2;
            ClipFeatureSchema.Attach(c, f);
        }

        return c;
    }

    public static ClipRating Rating(uint stars = 4, string session = "s1", string candidate = "s1-0001") =>
        new() { SessionId = session, CandidateId = candidate, Stars = stars };

    public static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "lookback-scoring-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>The repository root (the folder with Lookback.slnx), for files the tests share with ml/.</summary>
    public static string RepoRoot()
    {
        for (DirectoryInfo? d = new(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            if (File.Exists(Path.Combine(d.FullName, "Lookback.slnx")))
            {
                return d.FullName;
            }
        }

        throw new DirectoryNotFoundException("Lookback.slnx not found above the test binaries.");
    }
}
