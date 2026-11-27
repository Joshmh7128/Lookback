using System.Text.Json;
using Lookback.Contracts.V1;
using Lookback.Scoring.Features;
using Lookback.Scoring.Ratings;
using Microsoft.Extensions.Time.Testing;

namespace Lookback.Scoring.Tests;

public sealed class RatingStoreTests : IDisposable
{
    private readonly string _root = TestData.TempDir();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 27, 12, 0, 0, TimeSpan.Zero));

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private RatingStore Store() => new(_root, _time);

    [Fact]
    public void Owned_session_rating_is_appended_with_features_and_label()
    {
        RatingWriteResult r = Store().Append(TestData.Session(), TestData.Rating(), TestData.Candidate(), "shell");

        Assert.True(r.Written, r.Reason);
        Assert.Equal(Path.Combine(_root, "overwatch", RatingStore.FileName), r.Path);
        RatingRecord row = Assert.Single(Store().Read("overwatch").Records);
        Assert.Equal(0.75, row.Label);
        Assert.Equal("overwatch.push", row.Profile);
        Assert.Equal(ClipFeatureSchema.Id, row.FeatureSchema);
        Assert.Equal(ClipFeatureSchema.Count, row.Features.Count);
        Assert.Null(row.Features[ClipFeatureSchema.IndexOf(ClipFeatureSchema.EventCount)]);
    }

    [Fact]
    public void Line_matches_the_sample_the_ml_tests_read()
    {
        RatingWriteResult r = Store().Append(TestData.Session(), TestData.Rating(), TestData.Candidate(), "shell");

        string written = File.ReadAllText(r.Path!);
        string sample = File.ReadAllText(Path.Combine(TestData.Fixtures, "rating_sample.jsonl")).ReplaceLineEndings("\n");
        Assert.Equal(sample, written);
    }

    public static TheoryData<Retention, SourceKind, string> Refused => new()
    {
        { Retention.Ephemeral, SourceKind.OfficialPlayer, "" },
        { Retention.Ephemeral, SourceKind.File, "" },
        { Retention.Ephemeral, SourceKind.LiveGame, "" },
        { Retention.Owned, SourceKind.OfficialPlayer, "" }, // invalid: official players are ephemeral only
        { Retention.Unspecified, SourceKind.File, "" },
        { Retention.Permissioned, SourceKind.File, "" },     // no permission reference
        { Retention.OpenLicense, SourceKind.File, " " },
    };

    [Theory]
    [MemberData(nameof(Refused))]
    public void Sessions_that_may_not_persist_are_refused_and_nothing_touches_disk(Retention retention, SourceKind kind, string permissionRef)
    {
        RatingWriteResult r = Store().Append(TestData.Session(retention, kind, permissionRef), TestData.Rating(), TestData.Candidate(), "shell");

        Assert.False(r.Written);
        Assert.NotNull(r.Reason);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_root));
    }

    [Theory]
    [InlineData(Retention.Permissioned)]
    [InlineData(Retention.OpenLicense)]
    public void Third_party_footage_with_a_permission_reference_is_stored(Retention retention)
    {
        RatingWriteResult r = Store().Append(TestData.Session(retention, permissionRef: "permissions/creator-email.txt"), TestData.Rating(), TestData.Candidate(), "shell");

        Assert.True(r.Written, r.Reason);
        Assert.Equal("permissions/creator-email.txt", r.Record!.PermissionRef);
        Assert.Equal(RatingStore.Kebab(retention.ToString()), r.Record.Retention);
    }

    [Fact]
    public void Unknown_session_is_refused()
    {
        RatingWriteResult r = Store().Append(null, TestData.Rating(), TestData.Candidate(), "shell");

        Assert.False(r.Written);
        Assert.Contains("unknown", r.Reason, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_root));
    }

    [Theory]
    [InlineData(6u, "s1", "s1-0001")]
    [InlineData(3u, "", "s1-0001")]
    [InlineData(3u, "s1", "")]
    [InlineData(3u, "s1", "other")] // not the candidate passed in
    public void Malformed_ratings_are_refused(uint stars, string session, string candidate)
    {
        RatingWriteResult r = Store().Append(TestData.Session(), TestData.Rating(stars, session, candidate), TestData.Candidate(), "shell");

        Assert.False(r.Written);
    }

    [Theory]
    [InlineData("")]
    [InlineData("../escape")]
    [InlineData("..")]
    [InlineData("Over Watch")]
    [InlineData("C:\\temp")]
    public void Game_ids_that_are_not_safe_folder_names_are_refused(string profile)
    {
        RatingWriteResult r = Store().Append(TestData.Session(game: profile), TestData.Rating(), TestData.Candidate(profile: profile), "shell");

        Assert.False(r.Written);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_root));
        Assert.Throws<ArgumentException>(() => Store().PathFor(profile));
    }

    [Fact]
    public void Game_comes_from_the_session_when_the_candidate_is_unknown()
    {
        RatingWriteResult r = Store().Append(TestData.Session(game: "valorant"), TestData.Rating(), null, "shell");

        Assert.True(r.Written, r.Reason);
        Assert.Equal("valorant", r.Record!.Game);
        Assert.Empty(r.Record.Features);
        Assert.Equal("", r.Record.FeatureSchema);
    }

    [Fact]
    public void Candidate_without_features_gets_the_floor_features()
    {
        RatingWriteResult r = Store().Append(TestData.Session(), TestData.Rating(), TestData.Candidate(withFeatures: false), "testbench");

        Assert.True(r.Written, r.Reason);
        Assert.Equal(ClipFeatureSchema.Id, r.Record!.FeatureSchema);
        Assert.Equal(12f, r.Record.Features[ClipFeatureSchema.IndexOf(ClipFeatureSchema.DurationSeconds)]);
        Assert.Equal(0.5f, r.Record.Features[ClipFeatureSchema.IndexOf(ClipFeatureSchema.PeakPosition)]);
    }

    [Fact]
    public void Last_line_wins_and_zero_stars_clears()
    {
        RatingStore store = Store();
        store.Append(TestData.Session(), TestData.Rating(2), TestData.Candidate(), "shell");
        store.Append(TestData.Session(), TestData.Rating(5), TestData.Candidate(), "shell");
        store.Append(TestData.Session(), TestData.Rating(3, candidate: "s1-0002"), TestData.Candidate("s1-0002"), "shell");
        store.Append(TestData.Session(), TestData.Rating(0, candidate: "s1-0002"), TestData.Candidate("s1-0002"), "shell");

        RatingReadResult all = store.Read("overwatch");
        Assert.Equal(4, all.Records.Count);
        RatingRecord current = Assert.Single(RatingStore.Current(all.Records));
        Assert.Equal(5, current.Stars);
        Assert.Null(all.Records[^1].Label);
    }

    [Fact]
    public void Torn_last_line_is_skipped_on_read()
    {
        RatingStore store = Store();
        RatingWriteResult r = store.Append(TestData.Session(), TestData.Rating(), TestData.Candidate(), "shell");
        File.AppendAllText(r.Path!, "{\"v\":\"lookback.rating/1\",\"ga");

        RatingReadResult read = store.Read("overwatch");
        Assert.Single(read.Records);
        Assert.Equal(1, read.SkippedLines);
        Assert.Empty(store.Read("no-such-game").Records);
    }

    [Fact]
    public void Concurrent_appends_each_land_on_their_own_line()
    {
        RatingStore store = Store();
        Parallel.For(0, 200, i =>
        {
            string id = $"s1-{i:0000}";
            Assert.True(store.Append(TestData.Session(), TestData.Rating(4, candidate: id), TestData.Candidate(id), "shell").Written);
        });

        RatingReadResult read = store.Read("overwatch");
        Assert.Equal(200, read.Records.Count);
        Assert.Equal(0, read.SkippedLines);
    }

    [Fact]
    public void Rows_hold_numbers_and_ids_only()
    {
        RatingWriteResult r = Store().Append(TestData.Session(), TestData.Rating(), TestData.Candidate(), "shell");
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(r.Path!));
        string[] keys = [.. doc.RootElement.EnumerateObject().Select(p => p.Name)];
        Assert.Equal(
            ["v", "game", "profile", "sessionId", "candidateId", "stars", "label", "retention", "sourceKind", "permissionRef",
             "startQpc", "endQpc", "peakQpc", "qpcFrequency", "heuristicScore", "highlightProbability", "featureSchema", "features", "ratedAt", "source"],
            keys);
    }

    [Theory]
    [InlineData("overwatch.push", "overwatch")]
    [InlineData("roblox.rivals.duel", "roblox")]
    [InlineData("gta-v", "gta-v")]
    [InlineData("", null)]
    [InlineData(".x", null)]
    public void Game_of_a_profile_is_its_root(string profile, string? game) => Assert.Equal(game, RatingStore.GameOf(profile));

    // ---- Clipper-check ratings: the user's own ratings of a watched video, numbers only ----

    private static SessionStarted Player() => new() { SourceKind = SourceKind.OfficialPlayer, Retention = Retention.Ephemeral, QpcFrequency = TestData.Frequency };

    /// <summary>The input behind ml/tests/data/check_rating_sample.jsonl.</summary>
    private static CheckRatingInput CheckInput(string runId = "check-0123456789abcdef0123456789abcdef", string platform = "youtube", int stars = 5, string? profile = "cs2.competitive") => new()
    {
        RunId = runId,
        Ordinal = 3,
        Profile = profile,
        Platform = platform,
        Stars = stars,
        HeuristicScore = 0.75f,
        ClipperFeatures = new Dictionary<string, float>
        {
            ["score.heuristic"] = 0.75f,
            ["window.seconds"] = 8,
            ["window.peak_position"] = 0.5f,
            ["events.count"] = 2,
            ["events.kinds"] = 1,
            ["events.score_sum"] = 1.5f,
            ["events.score_max"] = 1,
            ["z_mean.motion.energy"] = 2.5f,
            ["z_mean.audio.rms_db"] = 1.25f, // no audio stream: stored as missing
            ["qa.silent"] = 1,                // likewise
            ["curve.max"] = 3,
        },
    };

    [Fact]
    public void A_check_rating_keeps_the_stars_and_numbers_and_nothing_of_the_video()
    {
        RatingWriteResult r = Store().AppendCheck(Player(), CheckInput());

        Assert.True(r.Written, r.Reason);
        Assert.Equal(Path.Combine(_root, "cs2", RatingStore.CheckFileName), r.Path);
        Assert.False(File.Exists(Store().PathFor("cs2")), "check ratings never mix into ratings.jsonl");
        RatingRecord row = Assert.Single(Store().ReadChecks("cs2").Records);
        Assert.Equal("check-0123456789abcdef0123456789abcdef", row.SessionId);
        Assert.Equal("c3", row.CandidateId);
        Assert.Equal((0UL, 0UL, 0UL, 0UL), (row.StartQpc, row.EndQpc, row.PeakQpc, row.QpcFrequency));
        Assert.Equal(("ephemeral", "official-player", RatingStore.CheckDerivedWrite, "youtube"), (row.Retention, row.SourceKind, row.DerivedWrite, row.Platform));
        Assert.Equal(2.5f, row.Features[ClipFeatureSchema.IndexOf(ClipFeatureSchema.MotionZ)]);
        Assert.All(ClipFeatureSchema.AudioDerived, a => Assert.Null(row.Features[ClipFeatureSchema.IndexOf(a)]));

        // The exact line the ml tests read.
        string sample = File.ReadAllText(Path.Combine(TestData.Fixtures, "check_rating_sample.jsonl")).ReplaceLineEndings("\n");
        Assert.Equal(sample, File.ReadAllText(r.Path!));
    }

    [Fact]
    public void The_generic_curve_files_under_generic_and_a_clear_is_a_new_line()
    {
        RatingStore store = Store();
        Assert.True(store.AppendCheck(Player(), CheckInput(profile: null)).Written);
        Assert.True(store.AppendCheck(Player(), CheckInput(profile: null, stars: 0)).Written);

        IReadOnlyList<RatingRecord> lines = store.ReadChecks("generic").Records;
        Assert.Equal(2, lines.Count);
        Assert.Empty(RatingStore.Current(lines)); // the clear wins
    }

    public static TheoryData<string, string, int> BadCheckInputs => new()
    {
        { "official-youtube-1a2b3c", "youtube", 4 },                   // not a random run id
        { "check-ABCDEFGHIJKLMNOPQRSTUVWXYZ012345", "youtube", 4 },   // not hex: could carry a video id
        { "check-0123456789abcdef0123456789abcdef", "vimeo", 4 },
        { "check-0123456789abcdef0123456789abcdef", "youtube", 6 },
    };

    [Theory]
    [MemberData(nameof(BadCheckInputs))]
    public void Check_ratings_that_could_carry_more_are_refused(string runId, string platform, int stars)
    {
        RatingWriteResult r = Store().AppendCheck(Player(), CheckInput(runId, platform, stars));

        Assert.False(r.Written);
        Assert.NotNull(r.Reason);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_root));
    }

    [Fact]
    public void Check_ratings_need_a_valid_official_player_session()
    {
        Assert.False(Store().AppendCheck(null, CheckInput()).Written);
        Assert.False(Store().AppendCheck(TestData.Session(), CheckInput()).Written); // owned footage is rated with Append
        Assert.False(Store().AppendCheck(new SessionStarted { SourceKind = SourceKind.OfficialPlayer, Retention = Retention.Owned }, CheckInput()).Written);
        Assert.False(Store().Append(Player(), TestData.Rating(), TestData.Candidate(), "shell").Written); // the session itself still never persists
        Assert.Empty(Directory.EnumerateFileSystemEntries(_root));
    }
}
