using Lookback.Media.Frames;
using Lookback.Profiles.Model;
using Lookback.Profiles.Regions;
using Lookback.Signals.Audio;
using Lookback.Signals.Video;

namespace Lookback.Profiles.Tests;

/// <summary>
/// The shooter profiles: Valorant, Counter-Strike 2, Fortnite, Rainbow Six
/// Siege, Marvel Rivals and Rust, with their modes. Every one is vision and
/// audio only, uncalibrated until checked against footage, and every rule in
/// every mode resolves to signals the tree actually produces.
/// </summary>
public class ShooterProfileTests
{
    public static TheoryData<string, string> Profiles => new()
    {
        { "valorant", "valorant" },
        { "valorant", "valorant.standard" },
        { "valorant", "valorant.spike-rush" },
        { "valorant", "valorant.deathmatch" },
        { "valorant", "valorant.team-deathmatch" },
        { "cs2", "cs2" },
        { "cs2", "cs2.competitive" },
        { "cs2", "cs2.wingman" },
        { "cs2", "cs2.deathmatch" },
        { "cs2", "cs2.arms-race" },
        { "fortnite", "fortnite" },
        { "fortnite", "fortnite.battle-royale" },
        { "fortnite", "fortnite.zero-build" },
        { "fortnite", "fortnite.reload" },
        { "rainbow-six-siege", "rainbow-six-siege" },
        { "rainbow-six-siege", "rainbow-six-siege.bomb" },
        { "rainbow-six-siege", "rainbow-six-siege.secure-area" },
        { "rainbow-six-siege", "rainbow-six-siege.hostage" },
        { "rainbow-six-siege", "rainbow-six-siege.team-deathmatch" },
        { "marvel-rivals", "marvel-rivals" },
        { "marvel-rivals", "marvel-rivals.convoy" },
        { "marvel-rivals", "marvel-rivals.domination" },
        { "marvel-rivals", "marvel-rivals.convergence" },
        { "marvel-rivals", "marvel-rivals.conquest" },
        { "rust", "rust" },
    };

    [Theory]
    [MemberData(nameof(Profiles))]
    public void ProfileIsValidVisionAndAudioOnlyAndUsesOnlyRealSignals(string game, string id)
    {
        ProfileCatalog catalog = ProfileCatalog.BuiltIn;
        GameProfile own = catalog.Get(id);
        Assert.Empty(GameProfileLoader.Validate(own));
        Assert.Equal(game, catalog.PathTo(id)[0].Id);
        Assert.False(own.Reference.Calibrated); // regions are estimates until checked against footage

        GameProfile p = catalog.Resolve(id);
        Assert.False(p.CapturePolicy.AllowPresentHook);
        Assert.False(p.CapturePolicy.AllowMemoryReading);
        Assert.NotEmpty(p.Executables);
        Assert.NotEmpty(p.Events);

        var available = new HashSet<string>(StringComparer.Ordinal);
        available.UnionWith(VideoSignalBank.CreateDefault(AnalysisFormat.Default).Names);
        available.UnionWith(AudioSignalBank.CreateDefault(AnalysisFormat.Default).Names);
        available.UnionWith(new RegionSignal(catalog.SubtreeRegions(game), 384, 216).Names);
        Assert.Empty(GameProfileLoader.ValidateSignals(p, available));
    }

    [Theory]
    [InlineData("valorant.deathmatch", "spike_planted")]
    [InlineData("valorant.team-deathmatch", "round_result")]
    [InlineData("cs2.deathmatch", "bomb_planted")]
    [InlineData("cs2.arms-race", "round_result")]
    [InlineData("rainbow-six-siege.team-deathmatch", "round_result")]
    [InlineData("fortnite.zero-build", "build_burst")]
    public void ModesWithoutThatMechanicTurnItsRulesOff(string id, string disabled)
    {
        GameProfile p = ProfileCatalog.BuiltIn.Resolve(id);
        Assert.DoesNotContain(p.Events, e => e.Name == disabled);
    }

    [Fact]
    public void RemovingAnEventRemovesTheCompositesBuiltOnIt()
    {
        GameProfile zeroBuild = ProfileCatalog.BuiltIn.Resolve("fortnite.zero-build");
        Assert.DoesNotContain(zeroBuild.Composites, c => c.Name == "build_fight");
        Assert.Contains(ProfileCatalog.BuiltIn.Resolve("fortnite.battle-royale").Composites, c => c.Name == "build_fight");
    }

    [Fact]
    public void RoundBasedModesKeepTheAceAndRespawnModesSwapItForAStreak()
    {
        ProfileCatalog c = ProfileCatalog.BuiltIn;
        Assert.Contains(c.Resolve("valorant.standard").Composites, x => x.Name == "ace");
        Assert.Contains(c.Resolve("cs2.competitive").Composites, x => x.Name == "ace");
        Assert.Contains(c.Resolve("rainbow-six-siege.bomb").Events, x => x.Name == "defuser_planted");
        Assert.DoesNotContain(c.Resolve("cs2.wingman").Composites, x => x.Name == "ace");
        Assert.Contains(c.Resolve("cs2.wingman").Composites, x => x.Name == "wingman_wipe");
        Assert.Contains(c.Resolve("valorant.deathmatch").Composites, x => x.Name == "killstreak");
        Assert.DoesNotContain(c.Resolve("valorant.deathmatch").Composites, x => x.Name == "ace");
    }

    [Theory]
    [InlineData("VALORANT-Win64-Shipping.exe", "valorant")]
    [InlineData("cs2.exe", "cs2")]
    [InlineData("FortniteClient-Win64-Shipping.exe", "fortnite")]
    [InlineData("RainbowSix.exe", "rainbow-six-siege")]
    [InlineData("RainbowSix_Vulkan.exe", "rainbow-six-siege")]
    [InlineData("Marvel-Win64-Shipping.exe", "marvel-rivals")]
    [InlineData(@"C:\Program Files (x86)\Steam\steamapps\common\Rust\RustClient.exe", "rust")]
    public void ExecutableFindsTheGame(string exe, string game) =>
        Assert.Equal(game, ProfileCatalog.BuiltIn.FindByExecutable(exe)?.Id);
}
