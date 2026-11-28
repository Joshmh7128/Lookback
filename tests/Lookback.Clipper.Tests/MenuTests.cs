using Lookback.Contracts.V1;
using Lookback.Profiles.Model;
using Lookback.Signals.Video;
using static Lookback.Clipper.Tests.Synth;

namespace Lookback.Clipper.Tests;

/// <summary>
/// Menu states in the clipper: a synthetic session where the screen turns
/// into a flat dark-blue "pause menu" and back. Entering and leaving it is a
/// big full-screen change (flash, motion and scene cut all jump), the kind of
/// curve peak that made candidates of nothing but a menu opening.
/// </summary>
public class MenuTests
{
    private const string MenuColor = "#102060";

    /// <summary>The usual video signals plus the colour grid.</summary>
    private static readonly SignalSchema GridVideo = new()
    {
        SchemaId = 1,
        Family = SignalFamily.Video,
        Names = { Video.Names, ColorGridSignal.AllNames },
    };

    [Fact]
    public void EnteringAndLeavingAMenuOpensNoCandidate()
    {
        bool Menu(double t) => Between(t, 20, 30);

        // Without menu states the two full-screen changes are curve peaks, and candidates.
        List<ClipperOutput> plain = RunGrid(new ClipperEngine(new ProfileCatalog([Game(menus: false)]), "g", "s", F), 50, Menu, _ => false);
        Assert.NotEmpty(plain);
        Assert.All(plain, o => Assert.True(Seconds(o.Candidate.PeakQpc) is > 19 and < 31));

        // With them, the same session gives nothing: the peaks belong to the menu changing.
        var engine = new ClipperEngine(new ProfileCatalog([Game(menus: true)]), "g", "s", F);
        Assert.Empty(RunGrid(engine, 50, Menu, _ => false));
        Assert.True(engine.MenuDropped >= 2, $"dropped {engine.MenuDropped}");
    }

    [Fact]
    public void PlayAroundAMenuStillClips()
    {
        // A real flash in play, well clear of the menu, is still a candidate.
        var engine = new ClipperEngine(new ProfileCatalog([Game(menus: true)]), "g", "s", F);
        ClipperOutput o = Assert.Single(RunGrid(engine, 60, t => Between(t, 20, 30), t => Between(t, 45, 45.3)));
        Assert.InRange(Seconds(o.Candidate.PeakQpc), 45, 45.35);
        Assert.DoesNotContain(o.Candidate.Reasons, r => r.StartsWith("menu", StringComparison.Ordinal));
        Assert.Equal("play", engine.MenuState);
    }

    [Fact]
    public void EventsInsideAMenuAreDroppedAndPartlyMenuWindowsArePenalised()
    {
        // An event while the menu is up (a scoreboard tick, say) gives no candidate.
        var engine = new ClipperEngine(new ProfileCatalog([Game(menus: true)]), "g", "s", F);
        engine.OnEvent(EngineTests.Event("kill", 25, 0.9f, 2, pre: 8, post: 3, profile: "g"));
        Assert.Empty(RunGrid(engine, 50, t => Between(t, 20, 40), _ => false));

        // A kill in play just before the menu opens: its window runs into the menu, so it is kept with a lower score.
        var before = new ClipperEngine(new ProfileCatalog([Game(menus: true)]), "g", "s", F);
        before.OnEvent(EngineTests.Event("kill", 17, 0.9f, 2, pre: 4, post: 6, profile: "g"));
        ClipperOutput o = Assert.Single(RunGrid(before, 50, t => Between(t, 20, 40), _ => false));
        Assert.Contains(o.Candidate.Reasons, r => r.StartsWith("menu", StringComparison.Ordinal));
        Assert.True(o.Candidate.HeuristicScore < 1 - Math.Exp(-1.8), $"score {o.Candidate.HeuristicScore}");

        // Mostly in the menu: dropped.
        var mostly = new ClipperEngine(new ProfileCatalog([Game(menus: true)]), "g", "s", F);
        mostly.OnEvent(EngineTests.Event("kill", 17.5, 0.9f, 2, pre: 0.5, post: 14, profile: "g"));
        Assert.Empty(RunGrid(mostly, 50, t => Between(t, 20, 40), _ => false));
    }

    [Fact]
    public void AProfileWithoutMenusIsUnchangedByTheGrid()
    {
        // The grid in the schema, no menu states in the profile: same candidates as without the grid at all.
        ProfileCatalog catalog = new([Game(menus: false)]);
        List<ClipperOutput> withGrid = RunGrid(new ClipperEngine(catalog, "g", "s", F), 40, _ => false, t => Between(t, 15, 15.3));
        List<ClipperOutput> without = Run(new ClipperEngine(catalog, "g", "s", F), Session(40, t => Between(t, 15, 15.3)));
        Assert.Equal(without.Select(o => o.Candidate.ToString()), withGrid.Select(o => o.Candidate.ToString()));
        Assert.Single(without);
    }

    [Fact]
    public void SteadyFramesWithMenusAllocateNothing()
    {
        var engine = new ClipperEngine(new ProfileCatalog([Game(menus: true)]), "g", "s", F);
        var output = new List<ClipperOutput>(16);
        engine.OnSchema(GridVideo);
        engine.OnSchema(Audio);
        SignalFrame[] frames = [.. GridSession(60, t => Between(t, 10, 60), _ => false)]; // the change (which may allocate) is in the first half
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

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Equal("pause", engine.MenuState);
    }

    private static GameProfile Game(bool menus) => GameProfileLoader.Parse($$"""
        {
          "id": "g", "displayName": "g",
          "events": [ { "name": "kill", "when": [ { "signal": "region.none.activity", "above": 0.3 } ] } ],
          "menus": { "states": [ {{(menus ? $$"""{ "name": "pause", "colors": [ {{string.Join(", ", Enumerable.Repeat($"\"{MenuColor}\"", ColorGridSignal.Cells))}} ], "maxDistance": 0.06 }""" : "")}} ] }
        }
        """);

    private static List<ClipperOutput> RunGrid(ClipperEngine engine, double seconds, Func<double, bool> menu, Func<double, bool> spike)
    {
        var output = new List<ClipperOutput>();
        engine.OnSchema(GridVideo);
        engine.OnSchema(Audio);
        foreach (SignalFrame f in GridSession(seconds, menu, spike))
        {
            engine.OnFrame(f, output);
        }

        engine.Flush(output);
        return output;
    }

    /// <summary>
    /// <see cref="Synth.Session"/> with the colour grid: play is a noisy mid grey; the menu is
    /// flat <see cref="MenuColor"/>. On the frame the screen changes, every whole-frame signal jumps.
    /// </summary>
    private static IEnumerable<SignalFrame> GridSession(double seconds, Func<double, bool> menu, Func<double, bool> spike)
    {
        bool wasMenu = false;
        foreach (SignalFrame f in Session(seconds, spike))
        {
            if (f.SchemaId != 1)
            {
                yield return f;
                continue;
            }

            long i = (long)(f.WindowStartQpc / VideoTicks);
            double t = f.WindowStartQpc / (double)F;
            bool inMenu = menu(t);
            if (inMenu != wasMenu)
            {
                f.Values[2] = 0.5f; // flash.brighten
                f.Values[3] = 0.6f; // motion.energy
                f.Values[4] = 0.9f; // scene.cut
            }
            else if (inMenu)
            {
                f.Values[3] = 0.005f + Noise(i, 0.002f); // a still menu
            }

            wasMenu = inMenu;
            for (int k = 0; k < ColorGridSignal.Length; k++)
            {
                float play = 0.45f + Noise(i + k, 0.08f);
                float menuValue = (k % 3) switch { 0 => 0x10 / 255f, 1 => 0x20 / 255f, _ => 0x60 / 255f };
                f.Values.Add(inMenu ? menuValue + Noise(i + k, 0.004f) : play);
            }

            yield return f;
        }
    }
}
