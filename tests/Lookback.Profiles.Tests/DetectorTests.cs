using Lookback.Contracts.V1;
using Lookback.Profiles.Detection;
using Lookback.Profiles.Model;

namespace Lookback.Profiles.Tests;

public class DetectorTests
{
    private const long Hz = 1000; // 1 tick = 1 ms keeps the arithmetic readable

    private static readonly string[] VideoNames = ["v.a", "v.b"];
    private static readonly string[] AudioNames = ["onset"];

    /// <summary>Feeds video frames every 33 ms with values from <paramref name="video"/>, plus optional audio onsets.</summary>
    private static List<GameEvent> Run(GameProfile profile, int frames, Func<int, (float A, float B)> video, Func<long, float>? onset = null, ProfileEventDetector? detector = null)
    {
        var d = detector ?? new ProfileEventDetector(profile, Hz);
        d.OnSchema(new SignalSchema { SchemaId = 1, Family = SignalFamily.Video, Names = { VideoNames } });
        d.OnSchema(new SignalSchema { SchemaId = 2, Family = SignalFamily.Audio, Names = { AudioNames } });
        var events = new List<GameEvent>();
        for (int i = 0; i < frames; i++)
        {
            long t = i * 33L;
            if (onset is not null)
            {
                for (long a = t; a < t + 33; a += 10)
                {
                    d.OnFrame(new SignalFrame { SchemaId = 2, WindowStartQpc = (ulong)a, Values = { onset(a) } }, events);
                }
            }

            (float va, float vb) = video(i);
            d.OnFrame(new SignalFrame { SchemaId = 1, WindowStartQpc = (ulong)t, Values = { va, vb } }, events);
        }

        d.Flush(events);
        return events;
    }

    private static GameProfile Profile(params EventRule[] rules) => new() { Id = "test", DisplayName = "Test", Events = rules };

    [Fact]
    public void AboveFiresOnceThenRespectsCooldown()
    {
        GameProfile p = Profile(new EventRule { Name = "hit", CooldownMs = 1000, When = [new Condition { Signal = "v.a", Above = 0.5f }] });

        // High for frames 10-50 (330 ms to 1650 ms): fires at 330, then not again until
        // 1 s later, which is the first frame at or after 1330 ms: frame 41 at 1353 ms.
        List<GameEvent> events = Run(p, 60, i => (i is >= 10 and <= 50 ? 1 : 0, 0));
        Assert.Equal([330UL, 1353UL], events.Select(e => e.AtQpc));
    }

    [Fact]
    public void BelowAndAllConditionsMustHoldTogether()
    {
        GameProfile p = Profile(new EventRule
        {
            Name = "death",
            When = [new Condition { Signal = "v.a", Above = 0.5f }, new Condition { Signal = "v.b", Below = 0.2f }],
        });

        // a is high at 10-12, b is low only at 20+: never both.
        Assert.Empty(Run(p, 30, i => (i is >= 10 and <= 12 ? 1 : 0, i >= 20 ? 0 : 1)));
        // Now they overlap at frame 21.
        Assert.Single(Run(p, 30, i => (i == 21 ? 1 : 0, i >= 20 ? 0 : 1)));
    }

    [Fact]
    public void RiseComparesWithTheBaselineNotAnAbsoluteLevel()
    {
        GameProfile p = Profile(new EventRule { Name = "change", When = [new Condition { Signal = "v.a", Rise = 0.1f, BaselineAlpha = 0.2f }] });

        // A constant 0.9 (bright game) never fires; a step from 0.3 to 0.5 does.
        Assert.Empty(Run(p, 40, _ => (0.9f, 0)));
        GameEvent e = Assert.Single(Run(p, 40, i => (i < 20 ? 0.3f : 0.5f, 0)));
        Assert.Equal(20UL * 33, e.AtQpc);
    }

    [Fact]
    public void AnyNeedsOnlyOneOfItsConditions()
    {
        GameProfile p = Profile(new EventRule
        {
            Name = "either",
            CooldownMs = 5000,
            WhenAny = [new Condition { Signal = "v.a", Above = 0.5f }, new Condition { Signal = "v.b", Above = 0.5f }],
        });
        Assert.Single(Run(p, 30, i => (0, i == 15 ? 1 : 0)));
    }

    [Fact]
    public void MissingSignalNeverHolds()
    {
        GameProfile p = Profile(new EventRule { Name = "ghost", When = [new Condition { Signal = "not.there", Below = 100 }] });
        var d = new ProfileEventDetector(p, Hz);
        Assert.Empty(Run(p, 10, _ => (0, 0), detector: d));
        Assert.Equal(float.NegativeInfinity, d.Margins[0]);
        Assert.Equal(["rule.ghost.0"], d.MarginNames);
    }

    [Fact]
    public void BoostsAddConfidenceFromEitherSideOfTheTrigger()
    {
        GameProfile p = Profile(new EventRule
        {
            Name = "kill",
            BaseConfidence = 0.5f,
            When = [new Condition { Signal = "v.a", Above = 0.5f }],
            Boosts = [new Boost { Signal = "onset", Above = 0.5f, WithinMs = 200, Add = 0.3f }],
        });

        // Visual trigger at frame 30 (990 ms). Onset 150 ms later, so the event must wait for it.
        GameEvent boosted = Assert.Single(Run(p, 60, i => (i == 30 ? 1 : 0, 0), t => t is >= 1135 and < 1145 ? 1 : 0));
        Assert.Equal(0.8f, boosted.Confidence, 3);
        Assert.Contains(boosted.Evidence, x => x.StartsWith("boost onset", StringComparison.Ordinal));

        // An onset 500 ms away does not count.
        GameEvent plain = Assert.Single(Run(p, 60, i => (i == 30 ? 1 : 0, 0), t => t is >= 1485 and < 1495 ? 1 : 0));
        Assert.Equal(0.5f, plain.Confidence, 3);
    }

    [Fact]
    public void ClipWindowSurroundsTheMoment()
    {
        GameProfile p = Profile(new EventRule { Name = "x", When = [new Condition { Signal = "v.a", Above = 0.5f }], Clip = new ClipWindow { PreMs = 300, PostMs = 200 } });
        GameEvent e = Assert.Single(Run(p, 30, i => (i == 20 ? 1 : 0, 0)));
        Assert.Equal(e.AtQpc - 300, e.WindowStartQpc);
        Assert.Equal(e.AtQpc + 200, e.WindowEndQpc);
        Assert.Equal("test", e.GameProfile);
    }

    [Fact]
    public void CompositeFiresOncePerStreakAndCoversIt()
    {
        GameProfile p = Profile(new EventRule { Name = "kill", CooldownMs = 100, When = [new Condition { Signal = "v.a", Above = 0.5f }] }) with
        {
            Composites = [new CompositeRule { Name = "multi", Of = "kill", AtLeast = 2, WithinMs = 1000, Clip = new ClipWindow { PreMs = 100 } }],
        };

        // Kills at frames 10, 20, 30 (330, 660, 990 ms) and a lone one at 100.
        List<GameEvent> events = Run(p, 120, i => (i is 10 or 20 or 30 or 100 ? 1 : 0, 0));
        Assert.Equal(4, events.Count(e => e.Name == "kill"));
        GameEvent multi = Assert.Single(events, e => e.Name == "multi"); // the third kill starts a new streak of one
        Assert.Equal(660UL, multi.AtQpc);
        Assert.Equal(330UL - 100, multi.WindowStartQpc); // from the first kill of the streak
    }
}
