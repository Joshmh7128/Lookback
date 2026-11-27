using Lookback.Profiles.Model;
using Lookback.Profiles.Regions;
using Lookback.Signals.Audio;
using Lookback.Signals.Video;
using Lookback.Media.Frames;

namespace Lookback.Profiles.Tests;

public class LoaderTests
{
    [Fact]
    public void BuiltInProfilesAreEmbedded()
    {
        Assert.Contains("overwatch", GameProfileLoader.BuiltInIds);
        Assert.Contains("generic", GameProfileLoader.BuiltInIds);
    }

    public static TheoryData<string> BuiltIns => [.. GameProfileLoader.BuiltInIds];

    /// <summary>Every shipped profile, at every level, validates and names only signals that exist for its game's tree.</summary>
    [Theory]
    [MemberData(nameof(BuiltIns))]
    public void BuiltInProfilesAreValidAndUseOnlyRealSignals(string id)
    {
        GameProfile p = GameProfileLoader.Load(id);
        Assert.Empty(GameProfileLoader.Validate(p));

        ProfileCatalog catalog = ProfileCatalog.BuiltIn;
        string root = catalog.PathTo(id)[0].Id;
        var available = new HashSet<string>(StringComparer.Ordinal);
        available.UnionWith(VideoSignalBank.CreateDefault(AnalysisFormat.Default).Names);
        available.UnionWith(AudioSignalBank.CreateDefault(AnalysisFormat.Default).Names);
        available.UnionWith(new RegionSignal(catalog.SubtreeRegions(root), 384, 216).Names);
        Assert.Empty(GameProfileLoader.ValidateSignals(catalog.Resolve(id), available));
    }

    [Fact]
    public void EveryRobloxExperienceIsListedAndExternalOnly()
    {
        string[] expected =
        [
            "steal-an-egg", "murder-mystery-2", "brookhaven", "adopt-me", "99-nights-in-the-forest", "blox-fruits", "rivals",
            "steal-a-brainrot", "jujutsu-shenanigans", "plus-one-speed-keyboard-escape", "bedwars", "slap-battles", "fisch",
            "arsenal", "jailbreak", "doors",
        ];
        ProfileCatalog c = ProfileCatalog.BuiltIn;
        Assert.All(expected, e => Assert.Equal(ProfileLevelKind.Subgame, c.Get("roblox." + e).Level));
        Assert.All(c.Subtree("roblox"), p =>
        {
            GameProfile r = c.Resolve(p.Id);
            Assert.False(r.CapturePolicy.AllowPresentHook || r.CapturePolicy.AllowMemoryReading);
            Assert.False(r.Reference.Calibrated && p.Regions.Count > 0, $"{p.Id} claims calibrated regions");
        });
        Assert.NotEmpty(c.Get("roblox").IdHints);
    }

    [Theory]
    [InlineData("gta-v")]
    [InlineData("minecraft")]
    [InlineData("rocket-league")]
    [InlineData("league-of-legends")]
    [InlineData("repo")]
    [InlineData("lethal-company")]
    [InlineData("peak")]
    [InlineData("dead-by-daylight")]
    public void NonShooterTreesAreExternalOnlyTaggedAndUncalibrated(string game)
    {
        ProfileCatalog c = ProfileCatalog.BuiltIn;
        Assert.NotEmpty(c.Get(game).Executables);
        Assert.Contains(c.Get(game), c.Tagged(c.Get(game).Tags[0]));
        Assert.All(c.Subtree(game), p =>
        {
            GameProfile r = c.Resolve(p.Id);
            Assert.False(r.CapturePolicy.AllowPresentHook || r.CapturePolicy.AllowMemoryReading);
            Assert.False(p.Reference.Calibrated, $"{p.Id} claims calibrated regions"); // estimates until checked against footage
            Assert.NotEmpty(p.Tags);
        });
    }

    [Theory]
    [InlineData("11", "league-of-legends.summoners-rift")]
    [InlineData("12", "league-of-legends.aram")]
    [InlineData("30", "league-of-legends.arena")]
    [InlineData("22", "league-of-legends.tft")]
    public void LeagueMapIdsPickTheMode(string map, string expected) =>
        Assert.Equal(expected, ProfileCatalog.BuiltIn.FindById("league.map", map)?.Id);

    [Fact]
    public void OverwatchIsVisionAndAudioOnly()
    {
        GameProfile p = GameProfileLoader.Load("overwatch");
        Assert.False(p.CapturePolicy.AllowPresentHook);
        Assert.False(p.CapturePolicy.AllowMemoryReading);
        Assert.Contains("Overwatch.exe", p.Executables);
        Assert.False(p.Reference.Calibrated); // until someone checks the regions against real footage
    }

    [Fact]
    public void UnknownFieldsAreRejected()
    {
        var ex = Assert.Throws<InvalidDataException>(() => GameProfileLoader.Parse("""{ "id": "x", "displayName": "X", "memoryOffsets": [1] }"""));
        Assert.Contains("memoryOffsets", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{ "id": "Bad Id", "displayName": "X" }""", "id must be")]
    [InlineData("""{ "id": "x", "displayName": "X", "regions": [ { "name": "r", "x": 0.9, "y": 0, "w": 0.2, "h": 0.1 } ] }""", "inside the 0..1")]
    [InlineData("""{ "id": "x", "displayName": "X", "events": [ { "name": "e", "when": [ { "signal": "s", "above": 1, "below": 2 } ] } ] }""", "exactly one")]
    [InlineData("""{ "id": "x", "displayName": "X", "events": [ { "name": "e" } ] }""", "no conditions")]
    [InlineData("""{ "id": "x", "displayName": "X", "composites": [ { "name": "c", "of": "nope" } ] }""", "unknown event")]
    public void InvalidProfilesAreRejected(string json, string expected)
    {
        var ex = Assert.Throws<InvalidDataException>(() => GameProfileLoader.Parse(json));
        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
    }
}
