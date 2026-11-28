using Lookback.Contracts.V1;
using static Lookback.Clipper.Tests.Synth;

namespace Lookback.Clipper.Tests;

/// <summary>
/// Probes: a window named up front is described like a candidate over exactly
/// its span, without being published. Backfill uses this to recompute the
/// features of a candidate rated before the clipper computed them.
/// </summary>
public class ProbeTests
{
    [Fact]
    public void A_probe_over_a_candidates_window_gets_the_candidates_features_and_is_not_published()
    {
        static IEnumerable<SignalFrame> Frames() => Session(40, t => Between(t, 15, 15.3));
        ClipperOutput candidate = Assert.Single(Run(new ClipperEngine("s", F), Frames()));

        var probed = new List<(ProbeWindow Window, ClipperOutput Output)>();
        var window = new ProbeWindow("old-c0001", (long)candidate.Candidate.StartQpc, (long)candidate.Candidate.EndQpc, (long)candidate.Candidate.PeakQpc);
        var quiet = new ProbeWindow("old-c0002", Ticks(28), Ticks(36), Ticks(32));
        var options = new ClipperOptions { Probes = [quiet, window], Probed = (w, o) => probed.Add((w, o)) };
        List<ClipperOutput> published = Run(new ClipperEngine("s", F, options), Frames());

        // Publishing is unchanged; the probes come out once each, in the order their windows end.
        ClipperOutput again = Assert.Single(published);
        Assert.Equal(candidate.Candidate.CandidateId, again.Candidate.CandidateId);
        Assert.Equal(["old-c0001", "old-c0002"], probed.Select(p => p.Window.Id));

        ClipperOutput same = probed[0].Output;
        Assert.Equal("old-c0001", same.Candidate.CandidateId);
        Assert.Equal(candidate.Candidate.HeuristicScore, same.Candidate.HeuristicScore);
        Assert.Equal(candidate.Features.Names, same.Features.Names);
        foreach (string name in candidate.Features.Names)
        {
            Assert.Equal(candidate.Features[name], same.Features[name], 5);
        }

        // Nothing happened in the quiet window: it is still described, with no score.
        ClipperOutput none = probed[1].Output;
        Assert.Equal(0, none.Candidate.HeuristicScore);
        Assert.Equal(8, none.Features["window.seconds"], 3);
        Assert.Equal((ulong)Ticks(32), none.Candidate.PeakQpc);
    }

    [Fact]
    public void A_probe_past_the_end_is_described_when_the_session_ends()
    {
        var probed = new List<string>();
        var options = new ClipperOptions { Probes = [new ProbeWindow("late", Ticks(25), Ticks(45), Ticks(35))], Probed = (w, _) => probed.Add(w.Id) };
        Run(new ClipperEngine("s", F, options), Session(30, _ => false));
        Assert.Equal(["late"], probed);
    }

    [Fact]
    public void Probes_need_a_callback_and_a_window()
    {
        Assert.Throws<ArgumentException>(() => new ClipperEngine("s", F, new ClipperOptions { Probes = [new ProbeWindow("a", 0, 10, 5)] }));
        Assert.Throws<ArgumentException>(() => new ClipperEngine("s", F, new ClipperOptions { Probes = [new ProbeWindow("a", 10, 10, 10)], Probed = (_, _) => { } }));
    }
}
