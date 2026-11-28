using Lookback.Contracts.V1;
using Lookback.Profiles.Detection;
using Lookback.Profiles.Model;
using Lookback.Signals.Video;

namespace Lookback.Profiles.Tests;

public class MenuStateTests
{
    private const long F = 10_000_000;
    private const long Frame = F / 30;

    private static IReadOnlyList<string?> All(string color) => [.. Enumerable.Repeat<string?>(color, ColorGridSignal.Cells)];

    private static MenuState State(string name, string color, float max = 0.05f) => new() { Name = name, Colors = All(color), MaxDistance = max };

    private static float[] Grid(string color)
    {
        float[] g = new float[ColorGridSignal.Length];
        MenuSignature.TryParse(All(color), g, new bool[ColorGridSignal.Cells], out _);
        return g;
    }

    [Fact]
    public void MenusLoadFromJsonAndValidate()
    {
        string colors = string.Join(", ", Enumerable.Range(0, ColorGridSignal.Cells).Select(i => i == 3 ? "null" : "\"#0a1b2c\""));
        GameProfile p = GameProfileLoader.Parse($$"""
            { "id": "g", "displayName": "g",
              "menus": { "enterMs": 300, "states": [ { "name": "pause", "colors": [ {{colors}} ], "maxDistance": 0.07, "notes": "ESC menu" } ] } }
            """);
        MenuState s = Assert.Single(p.Menus.States);
        Assert.Equal(("pause", 0.07f, 300), (s.Name, s.MaxDistance, p.Menus.EnterMs!.Value));
        Assert.Null(s.Colors[3]);

        float[] values = new float[ColorGridSignal.Length];
        bool[] compared = new bool[ColorGridSignal.Cells];
        Assert.True(MenuSignature.TryParse(s.Colors, values, compared, out _));
        Assert.False(compared[3]);
        Assert.Equal(0x0a / 255f, values[0], 5);
        Assert.Equal(0x2c / 255f, values[2], 5);
        Assert.Equal(s.Colors, MenuSignature.Format(values, compared)); // round trip

        // Menus need the colour grid, which every analysis produces.
        Assert.Equal(ColorGridSignal.AllNames, GameProfileLoader.ValidateSignals(p, new HashSet<string>()));
    }

    [Theory]
    [InlineData("""{ "name": "Pause", "colors": "GRID" }""", "lower-case")]
    [InlineData("""{ "name": "play", "colors": "GRID" }""", "'play'")]
    [InlineData("""{ "name": "pause", "colors": [ "#000000" ] }""", "needs 24")]
    [InlineData("""{ "name": "pause", "colors": "BADHEX" }""", "#rrggbb")]
    [InlineData("""{ "name": "pause", "colors": "NULLS" }""", "compares no cell")]
    [InlineData("""{ "name": "pause", "colors": "GRID", "maxDistance": 0 }""", "maxDistance")]
    [InlineData("""{ "name": "pause", "colors": "GRID" }, { "name": "pause", "colors": "GRID" }""", "defined twice")]
    public void BadMenusAreRejected(string states, string problem)
    {
        string Grid(string c) => "[ " + string.Join(", ", Enumerable.Repeat(c, ColorGridSignal.Cells)) + " ]";
        states = states.Replace("\"GRID\"", Grid("\"#102030\""), StringComparison.Ordinal)
            .Replace("\"BADHEX\"", Grid("\"#10203\""), StringComparison.Ordinal)
            .Replace("\"NULLS\"", Grid("null"), StringComparison.Ordinal);
        var ex = Assert.Throws<InvalidDataException>(() => GameProfileLoader.Parse($$"""{ "id": "g", "displayName": "g", "menus": { "states": [ {{states}} ] } }"""));
        Assert.Contains(problem, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ChildrenInheritReplaceAndDisableMenuStates()
    {
        var game = new GameProfile
        {
            Id = "game",
            DisplayName = "Game",
            Menus = new MenuSpec { EnterMs = 250, States = [State("pause", "#000080"), State("loading", "#000000"), State("lobby", "#808080")] },
        };
        var mode = new GameProfile
        {
            Id = "game.mode",
            DisplayName = "Mode",
            Parent = "game",
            Level = ProfileLevelKind.Mode,
            Disable = ["lobby"],
            Menus = new MenuSpec { ExitMs = 900, States = [State("loading", "#202020"), State("scoreboard", "#ffffff")] },
        };
        var catalog = new ProfileCatalog([game, mode]);

        MenuSpec m = catalog.Resolve("game.mode").Menus;
        Assert.Equal(["pause", "loading", "scoreboard"], m.States.Select(s => s.Name));
        Assert.Equal("#202020", m.States.Single(s => s.Name == "loading").Colors[0]);
        Assert.Equal((250, 900), (m.EnterMs!.Value, m.ExitMs!.Value));
        Assert.Equal(3, catalog.Resolve("game").Menus.States.Count);
    }

    [Fact]
    public void DetectorHoldsAStateOnlyAfterItLasts()
    {
        var d = new MenuStateDetector(new MenuSpec { EnterMs = 390, ExitMs = 190, States = [State("pause", "#102060"), State("loading", "#000000")] }, F);
        float[] play = Grid("#808080"), pause = Grid("#102060"), nearPause = Grid("#122262"), loading = Grid("#000000");
        long t = 0;
        bool Step(float[] g)
        {
            bool changed = d.Step(t, g);
            t += Frame;
            return changed;
        }

        Assert.False(Step(play));
        Assert.Equal(MenuStateDetector.Play, d.Held);

        // A few frames that look like the menu change nothing.
        for (int i = 0; i < 5; i++)
        {
            Step(pause);
        }

        Step(play);
        Assert.Equal(("play", MenuStateDetector.Play), (d.HeldName, d.Raw));

        // Held after 390 ms (the 12th frame after the first, at 30 fps), dated from the first matching frame; a close shade still matches.
        long from = t;
        int frames = 0;
        while (!Step(frames % 2 == 0 ? pause : nearPause))
        {
            frames++;
            Assert.True(frames < 20);
        }

        Assert.Equal("pause", d.HeldName);
        Assert.Equal(12, frames);
        Assert.Equal(from, d.HeldFrom);
        Assert.True(d.Distances[0] < d.MaxDistance(0) && d.Distances[1] > d.MaxDistance(1));

        // From one menu straight to another takes the enter time; back to play the (shorter) exit time.
        frames = 0;
        while (!Step(loading))
        {
            frames++;
        }

        Assert.Equal(("loading", 12), (d.HeldName, frames));
        frames = 0;
        while (!Step(play))
        {
            frames++;
        }

        Assert.Equal(("play", 6), (d.HeldName, frames));
        d.Reset();
        Assert.Equal(MenuStateDetector.Play, d.Held);
    }

    [Fact]
    public void IgnoredCellsDoNotCount()
    {
        // The centre cells show the game through the menu: whatever is there, it still matches.
        string?[] colors = [.. All("#102060")];
        float[] frame = Grid("#102060");
        for (int c = 8; c < 10; c++)
        {
            colors[c] = null;
            frame[c * 3] = frame[(c * 3) + 1] = frame[(c * 3) + 2] = 0.9f;
        }

        var d = new MenuStateDetector(new MenuSpec { EnterMs = 0, States = [new MenuState { Name = "pause", Colors = colors, MaxDistance = 0.02f }] }, F);
        Assert.True(d.Step(0, frame));
        Assert.Equal("pause", d.HeldName);
        Assert.Equal(0, d.Distances[0], 5);
    }

    [Fact]
    public void TrackerPublishesMenuValuesAndFollowsTheActiveProfile()
    {
        var game = new GameProfile { Id = "game", DisplayName = "Game", Menus = new MenuSpec { EnterMs = 0, ExitMs = 0, States = [State("pause", "#102060")] } };
        var quiet = new GameProfile { Id = "game.quiet", DisplayName = "Quiet", Parent = "game", Level = ProfileLevelKind.Mode, Disable = ["pause"] };
        var tracker = new MenuTracker(new ProfileCatalog([game, quiet]), "game", F);
        Assert.Equal(["menu.pause", "menu.pause.margin", "menu.any"], tracker.ValueNames);

        var schema = new SignalSchema { SchemaId = 1, Family = SignalFamily.Video, Names = { "luma.mean", ColorGridSignal.AllNames } };
        tracker.OnSchema(schema);
        SignalFrame GridFrame(string color, long t)
        {
            var f = new SignalFrame { SchemaId = 1, WindowStartQpc = (ulong)t };
            f.Values.Add(0.5f);
            f.Values.Add(Grid(color));
            return f;
        }

        float[] values = new float[3];
        Assert.True(tracker.OnVideoFrame(0, GridFrame("#102060", 0), "game"));
        Assert.True(tracker.Changed && tracker.InMenu);
        tracker.WriteValues(values);
        Assert.Equal(1f, values[0]);
        Assert.Equal(0.05f, values[1], 4);
        Assert.Equal(1f, values[2]);

        // The mode switches the state off: the tracker leaves the menu with it.
        Assert.False(tracker.OnVideoFrame(Frame, GridFrame("#102060", Frame), "game.quiet"));
        Assert.True(tracker.Changed);
        Assert.False(tracker.InMenu);
        tracker.WriteValues(values);
        Assert.Equal([0f, -1f, 0f], values);
    }

    [Fact]
    public void ATreeWithoutMenusTracksNothing()
    {
        var tracker = new MenuTracker(new ProfileCatalog([new GameProfile { Id = "game", DisplayName = "Game" }]), "game", F);
        Assert.False(tracker.HasMenus);
        Assert.Empty(tracker.ValueNames);
        tracker.OnSchema(new SignalSchema { SchemaId = 1, Family = SignalFamily.Video, Names = { ColorGridSignal.AllNames } });
        Assert.False(tracker.OnVideoFrame(0, new SignalFrame { SchemaId = 1, Values = { Grid("#000000") } }, "game"));
        Assert.Equal("play", tracker.HeldName);

        // The detector's margins gain no menu values either.
        var detector = new ProfileEventDetector(new GameProfile { Id = "game", DisplayName = "Game" }, F);
        Assert.DoesNotContain(detector.MarginNames, n => n.StartsWith("menu.", StringComparison.Ordinal));
    }

    [Fact]
    public void ProfileDetectorPublishesTheMenuStateWithItsMargins()
    {
        var game = new GameProfile
        {
            Id = "game",
            DisplayName = "Game",
            Events = [new EventRule { Name = "flash", When = [new Condition { Signal = "luma.mean", Above = 0.9f }] }],
            Menus = new MenuSpec { EnterMs = 100, States = [State("pause", "#102060")] },
        };
        var detector = new ProfileEventDetector(game, F);
        Assert.Equal(["rule.flash.0", "menu.pause", "menu.pause.margin", "menu.any"], detector.MarginNames);
        var schema = new SignalSchema { SchemaId = 1, Family = SignalFamily.Video, Names = { "luma.mean", ColorGridSignal.AllNames } };
        detector.OnSchema(schema);
        var events = new List<GameEvent>();
        for (int i = 0; i < 10; i++)
        {
            var f = new SignalFrame { SchemaId = 1, WindowStartQpc = (ulong)(i * Frame) };
            f.Values.Add(0.2f);
            f.Values.Add(Grid("#102060"));
            Assert.True(detector.OnFrame(f, events));
        }

        Assert.Equal("pause", detector.MenuState);
        Assert.Equal(1f, detector.Margins[1]);
        Assert.Equal(1f, detector.Margins[3]);
    }
}
