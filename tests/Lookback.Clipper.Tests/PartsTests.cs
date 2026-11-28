using Lookback.Clipper.Candidates;
using Lookback.Clipper.Curve;
using Lookback.Clipper.Qa;
using Lookback.Profiles.Model;

namespace Lookback.Clipper.Tests;

public class PartsTests
{
    [Fact]
    public void SelectMatchesSortingOnRandomInput()
    {
        var random = new Random(7);
        for (int trial = 0; trial < 200; trial++)
        {
            float[] values = [.. Enumerable.Range(0, random.Next(1, 60)).Select(_ => (float)Math.Round(random.NextDouble() * 10, 1))];
            float[] sorted = [.. values.Order()];
            int k = random.Next(values.Length);
            Assert.Equal(sorted[k], RobustNormalizer.Select(values.ToArray(), k));
            float median = values.Length % 2 == 1 ? sorted[values.Length / 2] : (sorted[(values.Length / 2) - 1] + sorted[values.Length / 2]) / 2;
            Assert.Equal(median, RobustNormalizer.MedianOf(values.ToArray()), 5);
        }
    }

    [Fact]
    public void NormaliserUsesMedianAndMadAfterWarmUp()
    {
        var n = new RobustNormalizer(capacity: 8, warmupSamples: 4, interval: 10);
        float[] values = [1, 2, 3, 100]; // one outlier barely moves a median
        for (int i = 0; i < values.Length; i++)
        {
            Assert.False(n.Ready);
            n.Observe(i * 10, values[i]);
            n.Observe((i * 10) + 5, 999); // between samples: ignored
        }

        Assert.True(n.Ready);
        Assert.Equal(2.5f, n.Median);
        Assert.Equal(RobustNormalizer.MadToSigma * 1.0f, n.Spread, 5); // deviations 1.5, 0.5, 0.5, 97.5
        Assert.Equal((10 - 2.5f) / (RobustNormalizer.MadToSigma * 1.0f), n.Z(10, 0.01f), 4);
        Assert.Equal(7.5f / 5, n.Z(10, minSpread: 5), 5); // the floor applies when the spread is smaller

        for (int i = 4; i < 20; i++)
        {
            n.Observe(i * 10, 50); // the window rolls: the old values fall out
        }

        Assert.Equal(50, n.Median);
        Assert.Equal(0, n.Spread);
        Assert.Equal(8, n.Count);
    }

    [Fact]
    public void PeaksUseHysteresisAndTheRefractoryPeriod()
    {
        CurveProfile p = Profile(high: 2, low: 1, refractoryMs: 1000, preMs: 500, postMs: 200);
        var d = new PeakDetector(1, maxLength: 100_000);
        var peaks = new List<CurvePeak>();
        long T = 0;
        void Feed(params float[] values)
        {
            foreach (float v in values)
            {
                if (d.Step(T, v, p, [v]) is { } peak)
                {
                    peaks.Add(peak);
                }

                T += 100; // 100 ms per sample on a 1 kHz clock
            }
        }

        // Dips between low and high do not end the peak; its time is the maximum.
        Feed(0, 2.5f, 1.5f, 3f, 1.2f, 2.8f, 0.5f);
        Assert.Empty(peaks); // still inside the refractory period
        Assert.Equal(300, d.Pending!.At);
        Assert.Equal(3f, d.Pending.Value);
        Feed(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        CurvePeak first = Assert.Single(peaks);
        Assert.Equal((300L, 0L, 500L), (first.At, first.Start, first.End)); // 300 - 500 is clamped to the session start

        // Two crossings 600 ms apart are folded; the window covers both.
        peaks.Clear();
        long t0 = T;
        Feed(2.2f, 0, 0, 0, 0, 0, 4f, 0);
        Feed(new float[15]);
        CurvePeak folded = Assert.Single(peaks);
        Assert.Equal(2, folded.Count);
        Assert.Equal(t0 + 600, folded.At);
        Assert.Equal((t0 - 500, t0 + 600 + 200), (folded.Start, folded.End));
    }

    [Fact]
    public void FuserMergesTouchingWindowsUpToTheMaximumLength()
    {
        var f = new CandidateFuser(gap: 10, maxLength: 100);
        f.Add(C(0, 30));
        f.Add(C(35, 50));  // within the gap: joins
        f.Add(C(200, 230)); // far: its own
        f.Add(C(45, 120)); // would make 0-120 > 100: its own, even though it overlaps
        var closed = new List<CandidateGroup>();
        f.Close(horizon: 60, closed);
        Assert.Empty(closed); // 50 + gap is not yet before the horizon
        f.Close(horizon: 61, closed);
        CandidateGroup g = Assert.Single(closed);
        Assert.Equal((0L, 50L, 2), (g.Start, g.End, g.Items.Count));
        f.CloseAll(closed);
        Assert.Equal([0L, 45, 200], closed.Select(x => x.Start));

        // A window that bridges two groups joins them into one.
        var b = new CandidateFuser(gap: 0, maxLength: 1000);
        b.Add(C(0, 10));
        b.Add(C(20, 30));
        b.Add(C(5, 25));
        var all = new List<CandidateGroup>();
        b.CloseAll(all);
        Assert.Equal((0L, 30L, 3), (Assert.Single(all).Start, all[0].End, all[0].Items.Count));

        // Beside a full group, later windows join the newer group that still has room, instead of one group each.
        var full = new CandidateFuser(gap: 0, maxLength: 100);
        full.Add(C(0, 100));   // full
        full.Add(C(90, 120));  // touches it, cannot join: its own
        full.Add(C(95, 125));  // touches both: joins the newer one
        full.Add(C(98, 130));
        var split = new List<CandidateGroup>();
        full.CloseAll(split);
        Assert.Equal([(0L, 100L, 1), (90L, 130L, 3)], split.Select(x => (x.Start, x.End, x.Items.Count)));

        static Contribution C(long s, long e) => new() { Start = s, End = e, At = s, Score = 1, Profile = "" };
    }

    [Fact]
    public void FlagHistoryCountsAWindowAcrossTheRingWrap()
    {
        var h = new FlagHistory(capacity: 16);
        for (int i = 0; i < 40; i++)
        {
            h.Add(i * 10, a: i % 2 == 0, b: i >= 30);
        }

        Assert.Equal((10L, 5L, 5L), h.Count(250, 345)); // samples 25..34
        Assert.Equal((16L, 8L, 10L), h.Count(0, 1000)); // only the last 16 are held
        Assert.Equal((0L, 0L, 0L), h.Count(1000, 2000));
        Assert.Equal((1L, 1L, 1L), h.Count(300, 300));
    }

    [Fact]
    public void ProfileClipperBlocksInheritAndAreValidated()
    {
        var parent = new ClipperSpec { Components = [new CurveComponent { Signal = "a", Weight = 1, MinSpread = 0.5f }, new CurveComponent { Signal = "b" }], High = 3 };
        var child = new ClipperSpec { Components = [new CurveComponent { Signal = "a", Weight = 2 }, new CurveComponent { Signal = "c" }], Low = 0.5f };
        ClipperSpec merged = child.Over(parent);
        Assert.Equal(["a", "b", "c"], merged.Components.Select(c => c.Signal));
        Assert.Equal((2f, 0.5f), (merged.Components[0].Weight, merged.Components[0].MinSpread!.Value)); // spread inherited
        Assert.Equal((3f, 0.5f), (merged.High!.Value, merged.Low!.Value));

        Assert.Throws<InvalidDataException>(() => GameProfileLoader.Parse("""{ "id": "x", "displayName": "x", "clipper": { "high": 1, "low": 2 } }"""));
        Assert.Throws<InvalidDataException>(() => GameProfileLoader.Parse("""{ "id": "x", "displayName": "x", "clipper": { "components": [ { "signal": "a", "weight": -1 } ] } }"""));
        Assert.Throws<InvalidDataException>(() => GameProfileLoader.Parse("""{ "id": "x", "displayName": "x", "clipper": { "hihg": 3 } }""")); // typo
        GameProfile ok = GameProfileLoader.Parse("""{ "id": "x", "displayName": "x", "clipper": { "components": [ { "signal": "no.such.signal" } ] } }""");
        Assert.Equal(["no.such.signal"], GameProfileLoader.ValidateSignals(ok, new HashSet<string>(StringComparer.Ordinal)));

        // Through the tree: high from the game, low from the mode; a mode whose low ends up above the game's high is refused.
        ProfileCatalog catalog = new([
            GameProfileLoader.Parse("""{ "id": "g", "displayName": "g", "clipper": { "high": 3 } }"""),
            GameProfileLoader.Parse("""{ "id": "g.m", "displayName": "m", "parent": "g", "level": "mode", "clipper": { "low": 0.5 } }"""),
        ]);
        Assert.Equal((3f, 0.5f), (catalog.Resolve("g.m").Clipper.High!.Value, catalog.Resolve("g.m").Clipper.Low!.Value));
        Assert.Throws<InvalidDataException>(() => new ProfileCatalog([
            GameProfileLoader.Parse("""{ "id": "g", "displayName": "g", "clipper": { "high": 3 } }"""),
            GameProfileLoader.Parse("""{ "id": "g.m", "displayName": "m", "parent": "g", "level": "mode", "clipper": { "low": 4 } }"""),
        ]));
    }

    [Fact]
    public void DefaultsAreCompleteAndGameAgnostic()
    {
        ClipperSpec d = ClipperDefaults.Spec;
        Assert.NotEmpty(d.Components);
        Assert.All(d.Components, c => Assert.DoesNotContain("region.", c.Signal, StringComparison.Ordinal));
        Assert.True(d.PreMs > d.PostMs); // more lead-up than aftermath
        Assert.True(d.High > d.Low);
    }

    private static CurveProfile Profile(float high, float low, int refractoryMs, int preMs, int postMs) =>
        CurveProfile.Build([("", new ClipperSpec { High = high, Low = low, RefractoryMs = refractoryMs, PreMs = preMs, PostMs = postMs })], 1000).Profiles[0];
}
