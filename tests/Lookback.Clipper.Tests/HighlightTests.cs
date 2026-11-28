using Lookback.Clipper.Candidates;
using Lookback.Contracts.V1;
using Lookback.Profiles.Model;
using static Lookback.Clipper.Tests.Synth;

namespace Lookback.Clipper.Tests;

/// <summary>
/// Highlights: a candidate longer than its profile's longest highlight (20 s
/// by default) also yields its most exciting 10–20 s, each a candidate of its
/// own with the long one as its parent.
/// </summary>
public class HighlightTests
{
    /// <summary>A busy stretch: a flash with a burst every 5 s from 10 s to 40 s, which the clipper fuses into one long candidate.</summary>
    private static List<ClipperOutput> Busy(ClipperOptions? options = null) =>
        Run(new ClipperEngine("s", F, options), Session(60, t => t >= 10 && t <= 40.5 && t % 5 < 0.3));

    [Fact]
    public void A_long_candidate_yields_its_best_non_overlapping_ten_to_twenty_second_highlights()
    {
        List<ClipperOutput> output = Busy();
        ClipperOutput parent = output[0];
        double parentSeconds = Seconds(parent.Candidate.EndQpc - parent.Candidate.StartQpc);
        Assert.True(parentSeconds > 20, $"the busy stretch should fuse into one long candidate, got {parentSeconds:0.0} s");
        Assert.Equal("", parent.Candidate.ParentCandidateId);

        List<ClipperOutput> highlights = [.. output.Skip(1)];
        Assert.NotEmpty(highlights);
        Assert.True(highlights.Count <= (int)Math.Ceiling(parentSeconds / 20));
        for (int i = 0; i < highlights.Count; i++)
        {
            ClipCandidate h = highlights[i].Candidate;
            Assert.Equal(parent.Candidate.CandidateId, h.ParentCandidateId);
            Assert.Equal($"{parent.Candidate.CandidateId}-h{i + 1}", h.CandidateId);
            Assert.Equal(h.CandidateId, highlights[i].Qa.CandidateId);
            Assert.InRange(Seconds(h.EndQpc - h.StartQpc), 9.9, 20.1);
            Assert.InRange(h.StartQpc, parent.Candidate.StartQpc, parent.Candidate.EndQpc);
            Assert.InRange(h.EndQpc, parent.Candidate.StartQpc, parent.Candidate.EndQpc);
            Assert.InRange(h.PeakQpc, h.StartQpc, h.EndQpc);
            Assert.NotEmpty(h.Reasons);
            Assert.Equal(parent.Features.Names.Count, highlights[i].Features.Names.Count);
            Assert.InRange(h.HeuristicScore, 0.01f, parent.Candidate.HeuristicScore);
        }

        // In time order, and no two share more than a quarter of their length.
        for (int i = 1; i < highlights.Count; i++)
        {
            ClipCandidate a = highlights[i - 1].Candidate, b = highlights[i].Candidate;
            Assert.True(a.StartQpc <= b.StartQpc);
            Assert.True((long)Math.Min(a.EndQpc, b.EndQpc) - (long)Math.Max(a.StartQpc, b.StartQpc) <= (long)(b.EndQpc - b.StartQpc) / 4);
        }
    }

    [Fact]
    public void A_short_candidate_has_no_highlights()
    {
        ClipperOutput only = Assert.Single(Run(new ClipperEngine("s", F), Session(30, t => Between(t, 15, 15.3))));
        Assert.Equal("", only.Candidate.ParentCandidateId);
    }

    [Fact]
    public void The_hosts_ranking_picks_which_windows_become_highlights()
    {
        // Prefer the earliest window, then the latest: the first highlight moves from the start of the stretch to its end.
        List<ClipperOutput> early = Busy(new ClipperOptions { RankHighlight = (c, _) => -(double)c.StartQpc, HighlightKeepFraction = 0 });
        List<ClipperOutput> late = Busy(new ClipperOptions { RankHighlight = (c, _) => c.StartQpc, HighlightKeepFraction = 0 });
        ClipCandidate parent = early[0].Candidate;
        Assert.Equal(parent.StartQpc, early.Skip(1).Min(o => o.Candidate.StartQpc));
        Assert.Equal(parent.EndQpc, late.Skip(1).Max(o => o.Candidate.EndQpc));
    }

    [Fact]
    public void Windows_start_at_the_shortest_length_and_grow_toward_nearby_moments_within_the_longest()
    {
        static Contribution At(double s, double score) => new() { Start = Ticks(s - 6), End = Ticks(s + 3), At = Ticks(s), Score = score, Profile = "" };
        Contribution[] items = [At(20, 1), At(24, 0.5), At(45, 0.8)];
        List<(long Start, long End)> windows = HighlightPicker.Windows(items, Ticks(10), Ticks(50), Ticks(10), Ticks(20), F);

        // The strongest moment first; it takes in the one 4 s later, staying within 20 s.
        (long s0, long e0) = windows[0];
        Assert.InRange(Seconds((ulong)(e0 - s0)), 10, 20);
        Assert.True(s0 <= Ticks(20) && e0 >= Ticks(24));
        Assert.True(s0 < Ticks(20) - Ticks(4), "the moment sits about 60% of the way in");

        // A moment near the end is kept inside the candidate.
        Assert.All(windows, w => Assert.True(w.Start >= Ticks(10) && w.End <= Ticks(50)));
        Assert.Contains(windows, w => w.Start <= Ticks(45) && w.End >= Ticks(45));
    }

    [Fact]
    public void Profiles_set_highlight_lengths_and_the_loader_checks_them()
    {
        ClipperSpec spec = GameProfileLoader.ParseClipper("""{ "highlightMinMs": 8000, "highlightMaxMs": 15000 }""");
        ClipperSpec resolved = spec.Over(ClipperDefaults.Spec);
        Assert.Equal((8000, 15000), (resolved.HighlightMinMs, resolved.HighlightMaxMs));
        Assert.Equal((10000, 20000), (ClipperDefaults.Spec.HighlightMinMs, ClipperDefaults.Spec.HighlightMaxMs));
        Assert.Throws<InvalidDataException>(() => GameProfileLoader.ParseClipper("""{ "highlightMinMs": 20000, "highlightMaxMs": 10000 }"""));
        Assert.Throws<InvalidDataException>(() => GameProfileLoader.ParseClipper("""{ "highlightMinMs": 0 }"""));
    }
}
