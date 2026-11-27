using System.Collections.Concurrent;
using Lookback.Contracts;
using Lookback.Contracts.V1;
using Lookback.Core.Bus;
using Lookback.Scoring.Ratings;

namespace Lookback.Scoring.Tests;

public sealed class RatingSinkTests : IDisposable
{
    private readonly string _root = TestData.TempDir();

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static void Publish(InProcessEventBus bus, string session, string topic, Action<Envelope> set)
    {
        var e = new Envelope { SessionId = session, Topic = topic, SourceId = "test" };
        set(e);
        bus.Publish(e);
    }

    private static void Rate(InProcessEventBus bus, string session, ClipRating rating) =>
        Publish(bus, session, Topics.ClipRating, e => e.ClipRating = rating);

    [Fact]
    public async Task Persists_a_rating_joined_to_its_candidate_and_session()
    {
        using var bus = new InProcessEventBus();
        var store = new RatingStore(_root);
        var outcomes = new ConcurrentQueue<RatingOutcome>();
        await using (var sink = new RatingSink(bus, store, new RatingSinkOptions { OnOutcome = outcomes.Enqueue }))
        {
            Publish(bus, "s1", Topics.Session, e => e.SessionStarted = TestData.Session());
            Publish(bus, "s1", Topics.ClipCandidate, e => e.ClipCandidate = TestData.Candidate());
            Rate(bus, "s1", TestData.Rating(5));
        }

        RatingOutcome outcome = Assert.Single(outcomes);
        Assert.True(outcome.Result.Written, outcome.Result.Reason);
        RatingRecord row = Assert.Single(store.Read("overwatch").Records);
        Assert.Equal(5, row.Stars);
        Assert.Equal("overwatch.push", row.Profile);
        Assert.Equal(0.62f, row.HeuristicScore);
        Assert.Equal("shell", row.Source);
    }

    [Fact]
    public async Task Official_player_session_ratings_are_refused_and_nothing_is_written()
    {
        using var bus = new InProcessEventBus();
        var store = new RatingStore(_root);
        RatingSink sink = new(bus, store);
        await using (sink)
        {
            Publish(bus, "yt", Topics.Session, e => e.SessionStarted = TestData.Session(Retention.Ephemeral, SourceKind.OfficialPlayer));
            Publish(bus, "yt", Topics.ClipCandidate, e => e.ClipCandidate = TestData.Candidate("yt-0001"));
            Rate(bus, "yt", TestData.Rating(5, "yt", "yt-0001"));
        }

        Assert.Equal(0, sink.Written);
        Assert.Equal(1, sink.Refused);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_root));
    }

    [Fact]
    public async Task Unknown_session_is_refused_unless_the_lookup_knows_it()
    {
        using var bus = new InProcessEventBus();
        var store = new RatingStore(_root);
        var seen = new ConcurrentQueue<RatingOutcome>();
        var options = new RatingSinkOptions
        {
            SessionLookup = id => id == "old" ? TestData.Session() : null,
            CandidateLookup = (s, c) => s == "old" ? TestData.Candidate(c) : null,
            OnOutcome = seen.Enqueue,
        };
        await using (new RatingSink(bus, store, options))
        {
            Rate(bus, "never-seen", TestData.Rating(3, "never-seen", "x-0001"));
            Rate(bus, "old", TestData.Rating(4, "old", "old-0007"));
        }

        Assert.Collection(
            seen,
            o => Assert.False(o.Result.Written),
            o => Assert.True(o.Result.Written, o.Result.Reason));
        RatingRecord row = Assert.Single(store.Read("overwatch").Records);
        Assert.Equal("old-0007", row.CandidateId);
        Assert.Equal(ClipFeatureSchemaId, row.FeatureSchema);
    }

    private const string ClipFeatureSchemaId = Features.ClipFeatureSchema.Id;

    [Fact]
    public async Task Rating_without_a_session_id_takes_the_envelopes()
    {
        using var bus = new InProcessEventBus();
        var store = new RatingStore(_root);
        await using (new RatingSink(bus, store))
        {
            Publish(bus, "s1", Topics.Session, e => e.SessionStarted = TestData.Session());
            Rate(bus, "s1", TestData.Rating(2, session: ""));
        }

        Assert.Equal("s1", Assert.Single(store.Read("overwatch").Records).SessionId);
    }

    [Fact]
    public async Task Republished_candidate_replaces_the_remembered_one()
    {
        using var bus = new InProcessEventBus();
        var store = new RatingStore(_root);
        await using (new RatingSink(bus, store))
        {
            Publish(bus, "s1", Topics.Session, e => e.SessionStarted = TestData.Session());
            Publish(bus, "s1", Topics.ClipCandidate, e => e.ClipCandidate = TestData.Candidate(withFeatures: false));
            ClipCandidate better = TestData.Candidate();
            better.HeuristicScore = 0.9f;
            Publish(bus, "s1", Topics.ClipCandidate, e => e.ClipCandidate = better);
            Rate(bus, "s1", TestData.Rating(4));
        }

        Assert.Equal(0.9f, Assert.Single(store.Read("overwatch").Records).HeuristicScore);
    }

    [Fact]
    public async Task Forgets_the_oldest_candidates_past_the_limit()
    {
        using var bus = new InProcessEventBus();
        var store = new RatingStore(_root);
        await using (new RatingSink(bus, store, new RatingSinkOptions { MaxCandidates = 2 }))
        {
            Publish(bus, "s1", Topics.Session, e => e.SessionStarted = TestData.Session(game: "overwatch"));
            foreach (string id in new[] { "s1-0001", "s1-0002", "s1-0003" })
            {
                Publish(bus, "s1", Topics.ClipCandidate, e => e.ClipCandidate = TestData.Candidate(id));
            }

            Rate(bus, "s1", TestData.Rating(4, candidate: "s1-0001"));
            Rate(bus, "s1", TestData.Rating(4, candidate: "s1-0003"));
        }

        IReadOnlyList<RatingRecord> rows = store.Read("overwatch").Records;
        Assert.Equal(2, rows.Count);
        Assert.Empty(rows[0].Features);          // forgotten: stored without features
        Assert.NotEmpty(rows[1].Features);
    }
}
