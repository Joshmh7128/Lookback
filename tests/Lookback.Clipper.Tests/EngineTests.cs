using Lookback.Contracts.V1;
using Lookback.Profiles.Model;
using static Lookback.Clipper.Tests.Synth;

namespace Lookback.Clipper.Tests;

public class EngineTests
{
    [Fact]
    public void AFlashWithABurstIsOneCandidateWithThePeakWindow()
    {
        var engine = new ClipperEngine("s", F);
        List<ClipperOutput> output = Run(engine, Session(30, t => Between(t, 15, 15.3)));

        ClipperOutput o = Assert.Single(output);
        double peak = Seconds(o.Candidate.PeakQpc);
        Assert.InRange(peak, 15, 15.35);
        Assert.Equal(peak - 6, Seconds(o.Candidate.StartQpc), 3); // default pre-roll 6 s
        Assert.Equal(peak + 3, Seconds(o.Candidate.EndQpc), 3);   // default post-roll 3 s
        Assert.InRange(o.Candidate.HeuristicScore, 0.01f, 1f);
        Assert.Equal(o.Candidate.HeuristicScore, o.Candidate.HighlightProbability);
        string reason = Assert.Single(o.Candidate.Reasons);
        Assert.StartsWith("excitement ×1", reason, StringComparison.Ordinal);
        Assert.Contains("flash.brighten", reason, StringComparison.Ordinal);
        Assert.Contains("audio.rms_db", reason, StringComparison.Ordinal);

        Assert.Equal(o.Candidate.CandidateId, o.Qa.CandidateId);
        Assert.True(o.Qa.Passed, string.Join("; ", o.Qa.Checks.Select(c => $"{c.Name} {c.Detail}")));
        Assert.Equal(["length", "black", "frozen", "silence"], o.Qa.Checks.Select(c => c.Name));
    }

    [Fact]
    public void QuietFootageAndTheWarmUpProduceNothing()
    {
        // A spike inside the 5 s warm-up has no history to stand out against.
        Assert.Empty(Run(new ClipperEngine("s", F), Session(30, t => Between(t, 2, 2.3))));
        Assert.Empty(Run(new ClipperEngine("s", F), Session(30, _ => false)));
    }

    [Fact]
    public void OverlappingPeaksMergeAndDistantOnesDoNot()
    {
        // 4 s apart: two peaks (outside the 3 s refractory period) whose windows overlap.
        ClipperOutput merged = Assert.Single(Run(new ClipperEngine("s", F), Session(40, t => Between(t, 15, 15.3) || Between(t, 19, 19.3))));
        Assert.StartsWith("excitement ×2", merged.Candidate.Reasons[0], StringComparison.Ordinal);
        Assert.InRange(Seconds(merged.Candidate.StartQpc), 8.9, 9.4);
        Assert.InRange(Seconds(merged.Candidate.EndQpc), 22, 22.4);

        // 1.5 s apart: folded into one peak by the refractory period.
        ClipperOutput folded = Assert.Single(Run(new ClipperEngine("s", F), Session(40, t => Between(t, 15, 15.3) || Between(t, 16.5, 16.8))));
        Assert.StartsWith("excitement ×2", folded.Candidate.Reasons[0], StringComparison.Ordinal);

        // 20 s apart: two candidates, in time order.
        List<ClipperOutput> two = Run(new ClipperEngine("s", F), Session(50, t => Between(t, 15, 15.3) || Between(t, 35, 35.3)));
        Assert.Equal(2, two.Count);
        Assert.True(two[0].Candidate.EndQpc < two[1].Candidate.StartQpc);
        Assert.NotEqual(two[0].Candidate.CandidateId, two[1].Candidate.CandidateId);
    }

    [Fact]
    public void BlackOrSilentStretchesFailQa()
    {
        ClipperOutput black = Assert.Single(Run(new ClipperEngine("s", F), Session(30, t => Between(t, 15, 15.3), black: t => t >= 13)));
        Assert.False(black.Qa.Passed);
        Assert.False(black.Qa.Checks.Single(c => c.Name == "black").Passed);
        Assert.True(black.Qa.Checks.Single(c => c.Name == "silence").Passed);

        // Silent apart from the burst itself is not "silent throughout"; silent all along is.
        ClipperOutput quiet = Assert.Single(Run(new ClipperEngine("s", F), Session(30, t => Between(t, 15, 15.3), silent: t => t >= 8 && !Between(t, 15, 15.3))));
        Assert.True(quiet.Qa.Checks.Single(c => c.Name == "silence").Passed);
    }

    [Fact]
    public void FrozenFramesFailQa()
    {
        var engine = new ClipperEngine("s", F);
        var output = new List<ClipperOutput>();
        engine.OnSchema(Video);
        engine.OnSchema(Audio);
        foreach (SignalFrame f in Session(30, t => Between(t, 15, 15.3)))
        {
            double t = Seconds(f.WindowStartQpc);
            if (f.SchemaId == 1 && t > 12)
            {
                f.Values[1] = (float)(t - 12); // motion.frozen_s counting up from 12 s
            }

            engine.OnFrame(f, output);
        }

        engine.Flush(output);
        QaCheck frozen = Assert.Single(output).Qa.Checks.Single(c => c.Name == "frozen");
        Assert.False(frozen.Passed);
        Assert.InRange(frozen.Value, 0.6f, 0.75f);
    }

    [Fact]
    public void ShortWindowsFailTheLengthGate()
    {
        var engine = new ClipperEngine("s", F, new ClipperOptions { Qa = new QaOptions { MinLengthMs = 10_000 } });
        ClipperOutput o = Assert.Single(Run(engine, Session(30, t => Between(t, 15, 15.3))));
        Assert.False(o.Qa.Checks.Single(c => c.Name == "length").Passed);
    }

    [Fact]
    public void EventAndCurveWindowsDedupeIntoOneCandidate()
    {
        var engine = new ClipperEngine("s", F);
        engine.OnEvent(Event("elimination", 15.1, 0.9f, 2.0f, pre: 8, post: 3));
        engine.OnEvent(Event("elimination", 16.0, 0.6f, 2.0f, pre: 8, post: 3));
        engine.OnEvent(Event("round_result", 40, 0.8f, 0.8f, pre: 8, post: 3)); // far away: its own candidate
        List<ClipperOutput> output = Run(engine, Session(50, t => Between(t, 15, 15.3)));

        Assert.Equal(2, output.Count);
        ClipCandidate fused = output[0].Candidate;
        Assert.Equal(["elimination ×2 (best confidence 0.90)"], fused.Reasons.Where(r => r.StartsWith("elimination", StringComparison.Ordinal)));
        Assert.Contains(fused.Reasons, r => r.StartsWith("excitement ×1", StringComparison.Ordinal));
        Assert.Equal(7.1, Seconds(fused.StartQpc), 3); // the earliest event's pre-roll
        Assert.Equal(19, Seconds(fused.EndQpc), 3);
        Assert.Equal(15.1, Seconds(fused.PeakQpc), 3); // the strongest contribution: 2.0 × 0.9, above the curve peak
        Assert.Equal("overwatch", fused.GameProfile);

        ClipCandidate alone = output[1].Candidate;
        Assert.Equal(["round_result ×1 (best confidence 0.80)"], alone.Reasons);
        Assert.Equal(1 - Math.Exp(-0.64), alone.HeuristicScore, 4);
    }

    [Fact]
    public void FollowingAProfileWaitsForItsEventsSoArrivalTimeDoesNotMatter()
    {
        ProfileCatalog catalog = new([Game("""{ "high": 2.0 }""")]);

        // Events delivered up front, or only once the frames are well past them (as a slow
        // profile runner would), give the same candidates.
        GameEvent kill = Event("kill", 15.1, 0.9f, 1, pre: 8, post: 3, profile: "g");
        var early = new ClipperEngine(catalog, "g", "s", F);
        early.OnEvent(kill.Clone());
        List<ClipperOutput> a = Run(early, Session(40, t => Between(t, 15, 15.3)));

        var late = new ClipperEngine(catalog, "g", "s", F);
        bool sent = false;
        List<ClipperOutput> b = Run(late, Session(40, t => Between(t, 15, 15.3)), (t, e) =>
        {
            if (!sent && t >= Ticks(15.1 + 0.5))
            {
                e.OnEvent(kill.Clone()); // half a second later: within the profile runner's allowed lag
                sent = true;
            }
        });

        Assert.Equal(a.Select(o => o.Candidate.ToString()), b.Select(o => o.Candidate.ToString()));
        ClipperOutput only = Assert.Single(a);
        Assert.Contains(only.Candidate.Reasons, r => r.StartsWith("kill", StringComparison.Ordinal));
        Assert.Contains(only.Candidate.Reasons, r => r.StartsWith("excitement", StringComparison.Ordinal));
    }

    [Fact]
    public void WeightsAndThresholdsFollowTheActiveProfile()
    {
        // The child switches the curve off by raising its threshold out of reach.
        ProfileCatalog catalog = new([
            Game("""{ "components": [ { "signal": "scene.cut", "weight": 0 } ] }"""),
            GameProfileLoader.Parse("""{ "id": "g.calm", "displayName": "calm", "parent": "g", "level": "mode", "clipper": { "high": 50, "low": 10 } }"""),
        ]);

        List<ClipperOutput> loud = Run(new ClipperEngine(catalog, "g", "s", F), Session(40, t => Between(t, 15, 15.3)));
        Assert.Single(loud);

        var engine = new ClipperEngine(catalog, "g", "s", F);
        engine.OnProfile(new ProfileDetected { ProfileId = "g.calm", AtQpc = (ulong)Ticks(10) });
        Assert.Empty(Run(engine, Session(40, t => Between(t, 15, 15.3))));
        Assert.Equal("g.calm", engine.ActiveProfileId);
        Assert.Equal(50, engine.CurveValues[1]); // the curve publishes the active thresholds
        Assert.DoesNotContain("scene.cut", engine.Components); // weight 0 everywhere: not a component
    }

    [Fact]
    public void ProfilesCanAddComponentsFromExistingSignals()
    {
        ProfileCatalog catalog = new([Game("""{ "components": [ { "signal": "region.kill_feed.activity", "weight": 2 } ] }""")]);
        var engine = new ClipperEngine(catalog, "g", "s", F);
        Assert.Contains("region.kill_feed.activity", engine.Components);
        Assert.Contains("clipper.z.region.kill_feed.activity", engine.CurveNames);
    }

    [Fact]
    public void SteadyFramesAllocateNothing()
    {
        var engine = new ClipperEngine("s", F);
        var output = new List<ClipperOutput>(16);
        engine.OnSchema(Video);
        engine.OnSchema(Audio);
        SignalFrame[] frames = [.. Session(60, _ => false)];
        int half = frames.Length / 2;
        for (int i = 0; i < half; i++)
        {
            engine.OnFrame(frames[i], output);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = half; i < frames.Length; i++)
        {
            engine.OnFrame(frames[i], output);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Empty(output);
        Assert.Equal(0, allocated);
    }

    internal static GameEvent Event(string name, double at, float confidence, float weight, double pre, double post, string profile = "overwatch") => new()
    {
        Name = name,
        GameProfile = profile,
        AtQpc = (ulong)Ticks(at),
        WindowStartQpc = (ulong)Ticks(at - pre),
        WindowEndQpc = (ulong)Ticks(at + post),
        Confidence = confidence,
        Weight = weight,
    };

    internal static GameProfile Game(string clipper) => GameProfileLoader.Parse($$"""
        {
          "id": "g", "displayName": "g",
          "events": [ { "name": "kill", "when": [ { "signal": "flash.brighten", "above": 0.3 } ],
                        "boosts": [ { "signal": "spectral.onset", "above": 0.5, "withinMs": 250 } ] } ],
          "clipper": {{clipper}}
        }
        """);
}
