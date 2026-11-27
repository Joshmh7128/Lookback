using System.Text.Json.Nodes;
using Lookback.Calibration.Check;
using Lookback.Contracts.V1;
using Lookback.Core.Bus;
using Lookback.Core.Time;

namespace Lookback.Calibration.Tests;

/// <summary>
/// The clipper check: its arithmetic on hand-made candidates, and the whole
/// harness on a live official-player session (the real clipper on the
/// player's frames, judged through the panel's commands) with nothing written.
/// </summary>
public sealed class ClipperCheckTests : IDisposable
{
    private const long F = 10_000_000;
    private readonly string _dir = Directory.CreateTempSubdirectory("lookback-check-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static ClipCandidate C(string id, double start, double end, float score, params string[] reasons) => new()
    {
        CandidateId = id,
        StartQpc = (ulong)(start * F),
        EndQpc = (ulong)(end * F),
        PeakQpc = (ulong)((start + end) / 2 * F),
        HeuristicScore = score,
        Reasons = { reasons },
    };

    [Fact]
    public void Precision_recall_and_breakdowns_follow_the_judgments()
    {
        var check = new ClipperCheck(F);
        check.Add(C("c1", 10, 14, 0.8f, "impact ×1 (best confidence 0.70)", "excitement ×1 (peak 3.4: motion.energy 5.0σ)"), null);
        check.Add(C("c2", 20, 24, 0.5f, "excitement ×1 (peak 2.1: flash.brighten 4.0σ)"), null);
        check.Add(C("c3", 30, 34, 0.2f, "loud_moment ×1 (best confidence 0.40)"), new QaReport { Passed = false });
        check.Add(C("c4", 40, 44, 0.9f, "excitement ×1 (peak 2.0)"), null);
        check.Add(C("c1", 10, 14, 0.8f, "duplicate"), null); // ignored

        Assert.True(check.Judge("c1", 5));
        Assert.True(check.Judge("c2", 4));
        Assert.True(check.Judge("c3", 2));
        Assert.False(check.Judge("nope", 3));
        check.MarkMissed(21 * F); // inside a 4-star candidate
        check.MarkMissed(32 * F); // inside a 2-star candidate: the clipper found it
        check.MarkMissed(50 * F); // nowhere: missed

        CheckStats s = check.Stats();
        Assert.Equal(4, s.Proposed);
        Assert.Equal(3, s.Judged);
        Assert.Equal(2, s.Good);
        Assert.Equal(0, s.Fair);
        Assert.Equal(1, s.Poor);
        Assert.Equal(2.0 / 3, s.Precision!.Value, 6);
        Assert.Equal(3, s.MissedMarks);
        Assert.Equal(2, s.MarksFound);
        Assert.Equal(1, s.MarksNotFound);
        Assert.Equal(0.75, s.Recall!.Value, 6); // highlights: 2 good + 1 found + 1 missed; found: 3
        Assert.Equal(11.0 / 3, s.MeanStars!.Value, 6);

        ReasonScore excitement = s.ByReason.Single(r => r.Reason == "excitement");
        Assert.Equal(3, excitement.Candidates);
        Assert.Equal(2, excitement.Judged);
        Assert.Equal(4.5, excitement.MeanStars!.Value, 6);
        Assert.Equal(1.0, excitement.Precision!.Value, 6);
        Assert.Equal(1, s.ByReason.Single(r => r.Reason == "loud_moment").Candidates);

        Assert.Equal([1, 1, 2], s.ByScore.Select(b => b.Candidates));
        Assert.False(check.Recent(10).Single(c => c.Id == "c3").QaPassed);
        Assert.Equal("c4", check.Recent(1)[0].Id); // newest first
    }

    [Fact]
    public void Tolerance_clearing_and_reset()
    {
        var check = new ClipperCheck(F, TimeSpan.FromSeconds(2));
        check.Add(C("a", 10, 14, 0.5f, "excitement ×1"), null);
        check.MarkMissed((long)(15.5 * F)); // 1.5 s after the end: still covered
        Assert.Equal(0, check.Stats().MarksNotFound);

        check.Judge("a", 4);
        check.Judge("a", 0); // cleared
        Assert.Equal(0, check.Stats().Judged);
        Assert.Null(check.Stats().Precision);

        check.Reset();
        CheckStats s = check.Stats();
        Assert.Equal(0, s.Proposed);
        Assert.Equal(0, s.MissedMarks);
        Assert.Null(s.Recall);
    }

    [Fact]
    public async Task The_clipper_runs_on_player_frames_and_its_candidates_are_judged_in_memory_only()
    {
        using var bus = new InProcessEventBus();
        await using var host = new CalibrationHost(bus, new CalibrationHostOptions { ProfilesDirectory = _dir });
        string[] before = Directory.GetFileSystemEntries(_dir, "*", SearchOption.AllDirectories);
        OfficialPlayerSession session = await host.StartAsync("youtube", null); // no profile: the clipper runs on the generic curve
        session.SetOrigin(0);

        // 40 s of moving noise with three full-frame flashes: the clipper's excitement curve peaks at each.
        var rng = new Random(7);
        byte[] px = new byte[Synthetic.W * Synthetic.H * 4];
        for (int i = 0; i < 40 * 30; i++)
        {
            double t = i / 30.0;
            bool flash = (t is >= 12 and < 12.4) || (t is >= 22 and < 22.4) || (t is >= 32 and < 32.4);
            for (int p = 0; p < px.Length; p += 4)
            {
                byte v = flash ? (byte)250 : (byte)rng.Next(40, 120);
                px[p] = px[p + 1] = px[p + 2] = v;
                px[p + 3] = 255;
            }

            session.OnVideo(i * 33_333L, Synthetic.W, Synthetic.H, px);
            if (i == (int)(23 * 30))
            {
                await host.CommandAsync(JsonNode.Parse("""{"type":"missed"}"""));
            }
        }

        // Restarting the analysis flushes the clipper; the check outlives the restart.
        await session.StartAsync(null);
        CheckStats stats = session.Check.Stats();
        Assert.True(stats.Proposed >= 1, "the clipper proposed nothing on the flashes");
        CheckCandidate first = session.Check.Recent(50)[^1];

        string reply = await host.CommandAsync(JsonNode.Parse($$"""{"type":"judge","candidateId":"{{first.Id}}","stars":5}""")!);
        Assert.Contains("judge", reply, StringComparison.Ordinal);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => host.CommandAsync(JsonNode.Parse("""{"type":"judge","candidateId":"nope","stars":3}""")!));

        JsonNode state = JsonNode.Parse(host.State())!;
        JsonNode check = state["check"]!;
        Assert.Equal(1, (int)check["stats"]!["judged"]!);
        Assert.Equal(1.0, (double)check["stats"]!["precision"]!);
        Assert.Equal(1, (int)check["stats"]!["missedMarks"]!);
        Assert.NotEmpty(check["recent"]!.AsArray());
        Assert.Contains("Nothing here is stored", (string)check["note"]!, StringComparison.Ordinal);

        // Nothing was written: not the candidates, the judgment, the mark, nor any part of the video.
        Assert.Equal(before, Directory.GetFileSystemEntries(_dir, "*", SearchOption.AllDirectories));

        // Stopping the player forgets the check entirely.
        await host.StopAsync();
        Assert.Equal(0, session.Check.Stats().Proposed);
        Assert.Equal(0, session.Check.Stats().MissedMarks);
    }
}
