using Lookback.Contracts.V1;
using Lookback.Profiles.Candidates;
using Lookback.Profiles.Detection;
using Lookback.Profiles.Model;

namespace Lookback.Profiles.Tests;

public class HierarchyTests
{
    private static readonly Condition Hot = new() { Signal = "v.a", Above = 0.5f };

    private static GameProfile Game(params EventRule[] events) => new()
    {
        Id = "game",
        DisplayName = "Game",
        Executables = ["Game.exe"],
        Regions = [new HudRegion { Name = "top", X = 0, Y = 0, W = 1, H = 0.1 }],
        Events = events,
        CapturePolicy = new CapturePolicy { AllowPresentHook = true, AllowMemoryReading = false },
    };

    private static GameProfile Sub(string name, IReadOnlyList<Condition>? visual = null, params EventRule[] events) => new()
    {
        Id = "game." + name,
        DisplayName = name,
        Parent = "game",
        Level = ProfileLevelKind.Subgame,
        Events = events,
        Detect = new DetectSpec
        {
            Hints = [new DetectHint { Source = HintSource.Log, Regex = @"place (\d+)", Values = [name == "red" ? "111" : "222"] }],
            Visual = visual ?? [],
            WindowSeconds = 1,
            HoldSeconds = 0.5,
            MinScore = 0.8,
        },
    };

    [Fact]
    public void ChildInheritsAddsReplacesAndDisables()
    {
        var shared = new EventRule { Name = "shared", When = [Hot] };
        var replaced = new EventRule { Name = "replaced", When = [Hot] };
        var dropped = new EventRule { Name = "dropped", When = [Hot] };
        GameProfile child = Sub("red") with
        {
            Events = [new EventRule { Name = "replaced", Weight = 3, When = [Hot] }, new EventRule { Name = "own", When = [Hot] }],
            Disable = ["dropped"],
            Regions = [new HudRegion { Name = "red_box", X = 0.5, Y = 0.5, W = 0.1, H = 0.1 }],
            CapturePolicy = new CapturePolicy { AllowPresentHook = true, AllowMemoryReading = true },
        };
        var catalog = new ProfileCatalog([Game(shared, replaced, dropped) with { Composites = [new CompositeRule { Name = "combo", Of = "dropped" }] }, child]);

        GameProfile r = catalog.Resolve("game.red");
        Assert.Equal(["shared", "replaced", "own"], r.Events.Select(e => e.Name));
        Assert.Equal(3, r.Events.Single(e => e.Name == "replaced").Weight);
        Assert.Empty(r.Composites); // its source event was disabled
        Assert.Equal(["top", "red_box"], r.Regions.Select(x => x.Name));
        Assert.Equal(["Game.exe"], r.Executables);
        Assert.True(r.CapturePolicy.AllowPresentHook);
        Assert.False(r.CapturePolicy.AllowMemoryReading); // a child can never allow what its game forbids
        Assert.Equal(["game", "game.red"], catalog.PathTo("game.red").Select(p => p.Id));
        Assert.Same(catalog.Get("game"), catalog.FindByExecutable(@"C:\Games\game.EXE"));
    }

    [Theory]
    [InlineData("missing-parent")]
    [InlineData("bad-prefix")]
    [InlineData("region-clash")]
    [InlineData("mode-under-mode")]
    public void InvalidTreesAreRejected(string problem)
    {
        GameProfile game = Game();
        GameProfile[] profiles = problem switch
        {
            "missing-parent" => [Sub("red")],
            "bad-prefix" => [game, Sub("red") with { Id = "other.red" }],
            "region-clash" => [game, Sub("red") with { Regions = [new HudRegion { Name = "box", X = 0, Y = 0.5, W = 0.1, H = 0.1 }] },
                Sub("blue") with { Regions = [new HudRegion { Name = "box", X = 0.5, Y = 0.5, W = 0.1, H = 0.1 }] }],
            _ => [game, new GameProfile { Id = "game.a", DisplayName = "A", Parent = "game", Level = ProfileLevelKind.Mode },
                new GameProfile { Id = "game.a.b", DisplayName = "B", Parent = "game.a", Level = ProfileLevelKind.Mode }],
        };

        Assert.ThrowsAny<Exception>(() =>
        {
            foreach (GameProfile p in profiles)
            {
                IReadOnlyList<string> problems = GameProfileLoader.Validate(p);
                if (problems.Count > 0)
                {
                    throw new InvalidDataException(string.Join("; ", problems));
                }
            }

            _ = new ProfileCatalog(profiles);
        });
    }

    [Fact]
    public void BuiltInTreeIsValid()
    {
        ProfileCatalog c = ProfileCatalog.BuiltIn;
        Assert.Equal(ProfileLevelKind.Subgame, c.Get("roblox.arsenal").Level);
        Assert.Contains(c.Children("roblox"), p => p.Id == "roblox.doors");
        Assert.All(c.Subtree("roblox"), p => Assert.False(c.Resolve(p.Id).CapturePolicy.AllowMemoryReading));
        Assert.Equal(ProfileLevelKind.Mode, c.Get("overwatch.push").Level);
    }

    [Fact]
    public void HintDecidesTheSubgameOutright()
    {
        var catalog = new ProfileCatalog([Game(), Sub("red"), Sub("blue")]);
        var tracker = new ProfileTracker(catalog, "game", 1000, DetectionMethod.Executable);
        Assert.Null(tracker.ApplyHints(new DetectionContext { LogLines = ["joining place 999"] }, 0));

        ProfileDetected d = tracker.ApplyHints(new DetectionContext { LogLines = ["noise", "! Joining game 'abc' place 222 at 1.2.3.4"] }, 5)!;
        Assert.Equal("game.blue", d.ProfileId);
        Assert.Equal(["game", "game.blue"], d.Path);
        Assert.Equal(DetectionMethod.Hint, d.Method);
        Assert.Equal(ProfileLevel.Subgame, d.Level);
        Assert.Contains("222", d.Evidence[0], StringComparison.Ordinal);
    }

    [Fact]
    public void FingerprintSwitchesOnlyAfterItHoldsForTheHoldTime()
    {
        var catalog = new ProfileCatalog([Game(), Sub("red", [new Condition { Signal = "red", Above = 0.5f }]), Sub("blue", [new Condition { Signal = "blue", Above = 0.5f }])]);
        var tracker = new ProfileTracker(catalog, "game", 1000, DetectionMethod.Executable);
        var values = new Dictionary<string, float>(StringComparer.Ordinal);
        var changes = new List<(long, string)>();

        // 0-4 s red, 4-8 s blue, with one stray blue frame at 2 s.
        for (long t = 0; t < 8000; t += 33)
        {
            bool blue = t >= 4000 || t == 1980;
            values["red"] = blue ? 0 : 1;
            values["blue"] = blue ? 1 : 0;
            ProfileDetected? d = tracker.OnVideoFrame(t, values);
            if (d is not null)
            {
                changes.Add((t, d.ProfileId));
                Assert.Equal(DetectionMethod.Visual, d.Method);
            }
        }

        Assert.Equal(2, changes.Count);
        Assert.Equal("game.red", changes[0].Item2);
        Assert.InRange(changes[0].Item1, 500, 1100); // ready after half a window, then held for 0.5 s
        Assert.Equal("game.blue", changes[1].Item2);
        Assert.InRange(changes[1].Item1, 4500, 5600); // once blue fills the window, after the hold
    }

    [Fact]
    public void OnlyTheActiveProfilesRulesFire()
    {
        var red = Sub("red", [new Condition { Signal = "red", Above = 0.5f }], new EventRule { Name = "red_only", CooldownMs = 100, When = [Hot] });
        var blue = Sub("blue", [new Condition { Signal = "blue", Above = 0.5f }], new EventRule { Name = "blue_only", CooldownMs = 100, When = [Hot] });
        var catalog = new ProfileCatalog([Game(new EventRule { Name = "everywhere", CooldownMs = 100, When = [Hot] }), red, blue]);
        var d = new ProfileEventDetector(catalog, "game", 1000, DetectionMethod.Executable);
        Assert.Equal(["rule.blue.blue_only.0", "rule.everywhere.0", "rule.red.red_only.0"], d.MarginNames.Order(StringComparer.Ordinal));

        d.OnSchema(new SignalSchema { SchemaId = 1, Family = SignalFamily.Video, Names = { "v.a", "red", "blue" } });
        var events = new List<GameEvent>();
        var detections = new List<ProfileDetected>();
        for (long t = 0; t < 6000; t += 33)
        {
            bool pulse = t % 1000 < 33;
            d.OnFrame(new SignalFrame { SchemaId = 1, WindowStartQpc = (ulong)t, Values = { pulse ? 1 : 0, 1, 0 } }, events, detections);
        }

        d.Flush(events);
        Assert.Equal("game", detections[0].ProfileId); // the starting profile is announced
        Assert.Equal("game.red", detections[^1].ProfileId);
        Assert.Equal("game.red", d.ActiveProfileId);
        Assert.DoesNotContain(events, e => e.Name == "blue_only");
        Assert.Contains(events, e => e.Name == "red_only" && e.GameProfile == "game.red");
        Assert.Contains(events, e => e.Name == "everywhere" && e.GameProfile == "game"); // before red was detected
    }

    [Fact]
    public void CandidatesMergeOverlappingEventsAndSplitDistantOnes()
    {
        var builder = new CandidateBuilder("s", 1000, gapMs: 1000, maxLengthMs: 30_000);
        var closed = new List<ClipCandidate>();
        GameEvent E(string name, long at, float weight) => new()
        {
            Name = name, GameProfile = "game", AtQpc = (ulong)at, WindowStartQpc = (ulong)(at - 5000), WindowEndQpc = (ulong)(at + 2000), Confidence = 0.5f, Weight = weight,
        };

        builder.Add(E("kill", 10_000, 1), closed);
        builder.Add(E("kill", 12_000, 1), closed);
        builder.Add(E("multikill", 12_000, 3), closed);
        Assert.Empty(closed);
        builder.Add(E("kill", 60_000, 1), closed); // far away: the first candidate closes
        builder.Flush(closed);

        Assert.Equal(2, closed.Count);
        ClipCandidate first = closed[0];
        Assert.Equal((5000UL, 14_000UL, 12_000UL), (first.StartQpc, first.EndQpc, first.PeakQpc));
        Assert.Equal(2.5f, first.HeuristicScore, 3); // 0.5 + 0.5 + 1.5
        Assert.InRange(first.HighlightProbability, 0.9f, 0.93f);
        Assert.Contains("kill ×2 (best confidence 0.50)", first.Reasons);
        Assert.Equal("s-0001", first.CandidateId);
    }

    [Fact]
    public void CandidatesNeverExceedTheMaximumLength()
    {
        var builder = new CandidateBuilder("s", 1000, gapMs: 1000, maxLengthMs: 10_000);
        var closed = new List<ClipCandidate>();
        for (long t = 5000; t < 40_000; t += 3000)
        {
            builder.Add(new GameEvent { Name = "x", AtQpc = (ulong)t, WindowStartQpc = (ulong)(t - 2000), WindowEndQpc = (ulong)(t + 1000), Confidence = 1, Weight = 1 }, closed);
        }

        builder.Flush(closed);
        Assert.True(closed.Count > 1);
        Assert.All(closed, c => Assert.True(c.EndQpc - c.StartQpc <= 10_000));
    }

    [Fact]
    public void GameIdHintsPickTheExperienceThatListsTheId()
    {
        GameProfile game = Game() with
        {
            IdHints = [new IdHint { Source = HintSource.Log, Regex = @"universeid:(\d+)", Kind = "roblox.universe" }],
        };
        GameProfile red = Sub("red") with { Detect = new DetectSpec(), Ids = new Dictionary<string, IReadOnlyList<string>> { ["roblox.universe"] = ["555"] } };
        var catalog = new ProfileCatalog([game, red, Sub("blue") with { Detect = new DetectSpec() }]);
        Assert.Same(red, catalog.FindById("roblox.universe", "555"));
        Assert.Null(catalog.FindById("roblox.universe", "556"));

        var tracker = new ProfileTracker(catalog, "game", 1000, DetectionMethod.Executable);
        ProfileDetected d = tracker.ApplyHints(new DetectionContext { LogLines = ["Report game_join_loadtime: placeid:1, universeid:555"] }, 0)!;
        Assert.Equal("game.red", d.ProfileId);
        Assert.Contains("555", d.Evidence[0], StringComparison.Ordinal);
    }

    [Fact]
    public void AnIdBelongsToOneProfile()
    {
        var ids = new Dictionary<string, IReadOnlyList<string>> { ["roblox.universe"] = ["1"] };
        Assert.Throws<InvalidDataException>(() => new ProfileCatalog([Game(), Sub("red") with { Ids = ids }, Sub("blue") with { Ids = ids }]));
    }
}
