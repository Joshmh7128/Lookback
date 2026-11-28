using System.Diagnostics;
using System.Text.Json;
using Lookback.Contracts;
using Lookback.Contracts.V1;
using Lookback.Core.Bus;
using Lookback.Core.Time;
using Lookback.Media.Ffmpeg;
using Lookback.Scoring.Ratings;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lookback.Recorder.Tests;

/// <summary>
/// The shell's control commands as the recorder handles them: ratings are
/// stored only for sessions that may persist and only for candidates the
/// recorder has seen; footage analysis publishes a file session whose
/// candidates can then be rated, with no signal frames on the shell's bus.
/// </summary>
public sealed class ControlServiceTests : IAsyncDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("lookback-recorder-").FullName;
    private readonly InProcessEventBus _bus = new();
    private readonly SessionDirectory _sessions;
    private readonly FootageSessions _footage;
    private readonly ControlService _control;

    public ControlServiceTests()
    {
        _sessions = new SessionDirectory(_bus);
        _footage = new FootageSessions(_bus, NullLogger.Instance, ClipScoring.HeuristicOnly());
        _control = new ControlService(_bus, _sessions, new RatingStore(Path.Combine(_dir, "datasets")), _footage, NullLogger.Instance);
    }

    public async ValueTask DisposeAsync()
    {
        await _footage.DisposeAsync();
        await _sessions.DisposeAsync();
        _bus.Dispose();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private async Task<JsonElement> SendAsync(object command)
    {
        string? reply = await _control.HandleAsync(JsonSerializer.Serialize(command), TestContext.Current.CancellationToken);
        Assert.NotNull(reply);
        using JsonDocument doc = JsonDocument.Parse(reply);
        JsonElement root = doc.RootElement.Clone();
        Assert.Equal("ack", root.GetProperty("type").GetString());
        return root;
    }

    private static string? Error(JsonElement ack) => ack.TryGetProperty("error", out JsonElement e) ? e.GetString() : null;

    private void Known(string session, SourceKind kind, Retention retention, string profile, params string[] candidates)
    {
        var f = new EnvelopeFactory(new ManualClock(), session, "test");
        Envelope s = f.Create(Topics.Session);
        s.SessionStarted = new SessionStarted { SourceKind = kind, Retention = retention, QpcFrequency = 10_000_000, GameProfile = profile, Title = "t" };
        _sessions.Apply(s);
        foreach (string c in candidates)
        {
            Envelope e = f.Create(Topics.ClipCandidate);
            e.ClipCandidate = new ClipCandidate { CandidateId = c, StartQpc = 10, EndQpc = 20, PeakQpc = 15, HeuristicScore = 0.7f, GameProfile = profile };
            _sessions.Apply(e);
        }
    }

    private string[] RatingFiles() => Directory.Exists(Path.Combine(_dir, "datasets"))
        ? Directory.GetFiles(Path.Combine(_dir, "datasets"), "*", SearchOption.AllDirectories)
        : [];

    [Fact]
    public async Task A_rating_of_an_owned_session_is_stored_and_published()
    {
        Known("s1", SourceKind.File, Retention.Owned, "overwatch", "c1");
        using IEventSubscription ratings = _bus.Subscribe(Topics.ClipRating);

        JsonElement ack = await SendAsync(new { type = "rate", id = "r1", sessionId = "s1", candidateId = "c1", stars = 5 });

        Assert.True(ack.GetProperty("ok").GetBoolean(), ack.ToString());
        Assert.Equal("r1", ack.GetProperty("id").GetString());
        Assert.True(ack.GetProperty("stored").GetBoolean());
        string file = Assert.Single(RatingFiles());
        Assert.EndsWith(Path.Combine("overwatch", "ratings.jsonl"), file, StringComparison.Ordinal);
        Assert.Contains("\"stars\":5", File.ReadAllText(file), StringComparison.Ordinal);
        Assert.True(ratings.TryRead(out Envelope? e));
        Assert.Equal(5u, e!.ClipRating.Stars);
        Assert.Equal("c1", e.ClipRating.CandidateId);
    }

    [Fact]
    public async Task Official_player_ratings_are_refused_and_nothing_is_written()
    {
        Known("op", SourceKind.OfficialPlayer, Retention.Ephemeral, "overwatch", "c1");
        JsonElement ack = await SendAsync(new { type = "rate", id = "r", sessionId = "op", candidateId = "c1", stars = 4 });
        Assert.False(ack.GetProperty("ok").GetBoolean());
        Assert.Equal("ephemeral", Error(ack));
        Assert.Empty(RatingFiles());
    }

    [Fact]
    public async Task Unknown_sessions_and_candidates_are_refused()
    {
        Known("s1", SourceKind.File, Retention.Owned, "overwatch", "c1");
        Assert.Equal("unknown-session", Error(await SendAsync(new { type = "rate", id = "a", sessionId = "nope", candidateId = "c1", stars = 3 })));
        Assert.Equal("unknown-candidate", Error(await SendAsync(new { type = "rate", id = "b", sessionId = "s1", candidateId = "nope", stars = 3 })));
        Assert.Empty(RatingFiles());
    }

    [Theory]
    [InlineData("{\"type\":\"rate\",\"id\":\"x\",\"sessionId\":\"s1\",\"candidateId\":\"c1\",\"stars\":7}")]
    [InlineData("{\"type\":\"rate\",\"id\":\"x\",\"sessionId\":\"s1\"}")]
    [InlineData("{\"type\":\"launch-missiles\",\"id\":\"x\"}")]
    [InlineData("{\"id\":\"x\"}")]
    [InlineData("{\"type\":\"rate\"}")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    public async Task Malformed_commands_get_bad_request(string text)
    {
        string? reply = await _control.HandleAsync(text, TestContext.Current.CancellationToken);
        using JsonDocument doc = JsonDocument.Parse(reply!);
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("bad-request", doc.RootElement.GetProperty("error").GetString());
        Assert.Empty(RatingFiles());
    }

    [Fact]
    public async Task Save_is_refused_until_live_capture_lands()
    {
        Assert.Equal("unsupported", Error(await SendAsync(new { type = "save", id = "s", sessionId = "s1", candidateId = "c1" })));
    }

    [Fact]
    public async Task Storage_commands_need_live_capture_and_history_is_empty_without_it()
    {
        JsonElement history = await SendAsync(new { type = "history", id = "h" });
        Assert.True(history.GetProperty("ok").GetBoolean());
        Assert.Equal(0, history.GetProperty("data").GetArrayLength());
        Assert.Equal("unsupported", Error(await SendAsync(new { type = "storage", id = "s" })));
        Assert.Equal("unsupported", Error(await SendAsync(new { type = "storage-delete", id = "d", kind = "cache" })));
        Assert.Equal("unsupported", Error(await SendAsync(new { type = "storage-keep", id = "k", sessionId = "s1", keep = true })));
    }

    [Fact]
    public async Task Preview_needs_a_candidate_and_live_capture()
    {
        Assert.Equal("bad-request", Error(await SendAsync(new { type = "preview", id = "p", sessionId = "s1" })));
        Assert.Equal("unsupported", Error(await SendAsync(new { type = "preview", id = "p", sessionId = "s1", candidateId = "c1" })));
    }

    [Fact]
    public async Task Analyze_refuses_what_it_cannot_run()
    {
        string clip = Path.Combine(_dir, "x.mp4");
        await File.WriteAllBytesAsync(clip, [0], TestContext.Current.CancellationToken);
        Assert.Equal("bad-request", Error(await SendAsync(new { type = "analyze", id = "1", path = "relative.mp4" })));
        Assert.Equal("bad-request", Error(await SendAsync(new { type = "analyze", id = "2", path = Path.Combine(_dir, "missing.mp4") })));
        Assert.Equal("bad-request", Error(await SendAsync(new { type = "analyze", id = "3", path = clip, profile = "no-such-game" })));
        Assert.Equal("bad-request", Error(await SendAsync(new { type = "analyze", id = "4", path = clip, retention = "permissioned" })));
        Assert.Equal("bad-request", Error(await SendAsync(new { type = "analyze", id = "5", path = clip, retention = "whatever" })));
    }

    [Fact]
    public async Task Footage_is_analysed_into_a_file_session_whose_candidates_can_be_rated()
    {
        Assert.SkipWhen(FfmpegLocator.FindFfmpeg() is null, "ffmpeg not installed");
        CancellationToken ct = TestContext.Current.CancellationToken;
        string clip = MakeClip(Path.Combine(_dir, "flashes.mp4"), seconds: 10, moments: [3, 7]);
        using IEventSubscription all = _bus.Subscribe("*", capacity: 1 << 16);

        JsonElement ack = await SendAsync(new { type = "analyze", id = "a1", path = clip, profile = "generic" });
        Assert.True(ack.GetProperty("ok").GetBoolean(), ack.ToString());
        string sessionId = ack.GetProperty("sessionId").GetString()!;

        List<Envelope> seen = [];
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        await foreach (Envelope e in all.ReadAllAsync(timeout.Token))
        {
            seen.Add(e);
            if (e.SessionId == sessionId && e.PayloadCase == Envelope.PayloadOneofCase.SessionEnded)
            {
                break;
            }
        }

        List<Envelope> mine = [.. seen.Where(e => e.SessionId == sessionId)];
        Envelope header = mine.First(e => e.PayloadCase == Envelope.PayloadOneofCase.SessionStarted);
        Assert.Equal(clip, header.SessionStarted.Title); // the shell links previews by this exact path
        Assert.Equal(SourceKind.File, header.SessionStarted.SourceKind);
        Assert.Equal(FootageDigest.Of(clip), header.SessionStarted.SourceDigest); // so its ratings can find this file again
        Assert.Equal("end of file", mine[^1].SessionEnded.Reason);
        Assert.DoesNotContain(mine, e => e.Topic.StartsWith("signals.", StringComparison.Ordinal));
        List<ClipCandidate> candidates = [.. mine.Where(e => e.Topic == Topics.ClipCandidate).Select(e => e.ClipCandidate)];
        Assert.NotEmpty(candidates);

        // The directory learned the candidates from the bus, so the shell can rate them.
        string candidateId = candidates[0].CandidateId;
        var wait = Stopwatch.StartNew();
        while (_sessions.Candidate(sessionId, candidateId) is null && wait.Elapsed < TimeSpan.FromSeconds(5))
        {
            await Task.Delay(20, ct);
        }

        JsonElement rated = await SendAsync(new { type = "rate", id = "r", sessionId, candidateId, stars = 4 });
        Assert.True(rated.GetProperty("ok").GetBoolean(), rated.ToString());
        Assert.Single(RatingFiles());

        // The candidate left the clipper with its features, so the rating is stored with all of them,
        // not only the three a bare candidate carries (heuristic score, length, peak position).
        Assert.Equal(Lookback.Scoring.Features.ClipFeatureSchema.Count, candidates[0].Features.Count);
        using JsonDocument line = JsonDocument.Parse(File.ReadAllLines(RatingFiles()[0])[^1]);
        int known = line.RootElement.GetProperty("features").EnumerateArray().Count(v => v.ValueKind == JsonValueKind.Number);
        Assert.True(known > 10, $"only {known} features stored with the rating"); 
    }

    [Fact]
    public async Task Ratings_without_features_are_backfilled_from_their_footage_and_a_wrong_file_is_refused()
    {
        Assert.SkipWhen(FfmpegLocator.FindFfmpeg() is null, "ffmpeg not installed");
        CancellationToken ct = TestContext.Current.CancellationToken;
        string clip = MakeClip(Path.Combine(_dir, "flashes.mp4"), seconds: 10, moments: [3, 7]);
        var store = new RatingStore(Path.Combine(_dir, "datasets"));
        await using var backfill = new RatingBackfill(store, NullLogger.Instance);
        var control = new ControlService(_bus, _sessions, store, _footage, NullLogger.Instance, backfill: backfill);

        // Two ratings stored before the clipper's features: one session without a fingerprint, one with another file's.
        SessionStarted Header(string digest) => new() { SourceKind = SourceKind.File, Retention = Retention.Owned, QpcFrequency = FootageSessions.Frequency, GameProfile = "generic", SourceDigest = digest };
        ClipCandidate Bare(string id) => new() { CandidateId = id, StartQpc = 0, EndQpc = 5 * (ulong)FootageSessions.Frequency, PeakQpc = 3 * (ulong)FootageSessions.Frequency, HeuristicScore = 0.6f, GameProfile = "generic" };
        Assert.True(store.Append(Header(""), new ClipRating { SessionId = "old", CandidateId = "old-c0001", Stars = 5 }, Bare("old-c0001"), "shell").Written);
        Assert.True(store.Append(Header("00ff"), new ClipRating { SessionId = "other", CandidateId = "other-c0001", Stars = 2 }, Bare("other-c0001"), "shell").Written);
        Assert.All(store.Read("generic").Records, r => Assert.True(RatingBackfill.NeedsFeatures(r)));

        async Task<JsonElement> Send(object command)
        {
            string? reply = await control.HandleAsync(JsonSerializer.Serialize(command), ct);
            using JsonDocument doc = JsonDocument.Parse(reply!);
            return doc.RootElement.Clone();
        }

        async Task<JsonElement> Finished(string sessionId)
        {
            var wait = Stopwatch.StartNew();
            while (wait.Elapsed < TimeSpan.FromSeconds(90))
            {
                JsonElement plan = (await Send(new { type = "backfill-plan", id = "p" })).GetProperty("data");
                JsonElement row = plan.GetProperty("sessions").EnumerateArray().Single(x => x.GetProperty("sessionId").GetString() == sessionId);
                if (row.TryGetProperty("job", out JsonElement job) && job.GetProperty("state").GetString() is "done" or "failed")
                {
                    return job;
                }

                await Task.Delay(100, ct);
            }

            throw new TimeoutException();
        }

        JsonElement listed = (await Send(new { type = "backfill-plan", id = "p0" })).GetProperty("data").GetProperty("sessions");
        Assert.Equal(["old", "other"], listed.EnumerateArray().Select(x => x.GetProperty("sessionId").GetString()));
        Assert.Equal("bad-request", Error(await Send(new { type = "backfill", id = "b0", sessionId = "old" }))); // no file chosen

        // The right footage: the rating gets the full vector, keeping its stars.
        Assert.True((await Send(new { type = "backfill", id = "b1", sessionId = "old", path = clip })).GetProperty("ok").GetBoolean());
        JsonElement done = await Finished("old");
        Assert.Equal("done", done.GetProperty("state").GetString());
        RatingRecord now = RatingStore.Current(store.Read("generic").Records).Single(r => r.SessionId == "old");
        Assert.Equal(5, now.Stars);
        Assert.NotNull(now.BackfilledAt);
        Assert.False(RatingBackfill.NeedsFeatures(now));
        Assert.True(now.Features.Count(f => f is not null) > 10);
        Assert.Equal(FootageDigest.Of(clip), now.SourceDigest);

        // A file whose fingerprint is not the one on the ratings is refused, and nothing is written.
        int lines = store.Read("generic").Records.Count;
        Assert.True((await Send(new { type = "backfill", id = "b2", sessionId = "other", path = clip })).GetProperty("ok").GetBoolean());
        JsonElement refused = await Finished("other");
        Assert.Equal("failed", refused.GetProperty("state").GetString());
        Assert.Contains("fingerprint", refused.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Equal(lines, store.Read("generic").Records.Count);
    }

    /// <summary>A test pattern with a 0.2 s flash and a white-noise burst at each moment (as the clipper's footage tests make).</summary>
    private static string MakeClip(string path, int seconds, double[] moments)
    {
        string at(double len) => string.Join("+", moments.Select(m => $"between(t,{m},{m + len})"));
        var psi = new ProcessStartInfo(FfmpegLocator.RequireFfmpeg()) { RedirectStandardError = true, UseShellExecute = false };
        foreach (string a in (string[])[
            "-y", "-loglevel", "error",
            "-f", "lavfi", "-i", $"testsrc2=size=320x180:rate=30:duration={seconds}",
            "-f", "lavfi", "-i", $"sine=frequency=440:sample_rate=48000:duration={seconds},volume=0.05",
            "-f", "lavfi", "-i", $"anoisesrc=d={seconds}:c=white:a=0.8:r=48000",
            "-filter_complex", $"[0:v]eq=brightness='if({at(0.2)},0.6,0)':eval=frame[v];[2:a]volume='if({at(0.3)},1,0)':eval=frame[n];[1:a][n]amix=inputs=2:normalize=0[a]",
            "-map", "[v]", "-map", "[a]", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-c:a", "aac", "-t", $"{seconds}", path])
        {
            psi.ArgumentList.Add(a);
        }

        using Process p = Process.Start(psi)!;
        string err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, err);
        return path;
    }
}
