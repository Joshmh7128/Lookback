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

    [Theory]
    [InlineData("overwatch")]
    [InlineData("generic")]
    [InlineData("valorant")]
    [InlineData("cs2")]
    [InlineData("fortnite")]
    [InlineData("rainbow-six-siege")]
    [InlineData("marvel-rivals")]
    [InlineData("rust")]
    public void BuiltInProfilesAreValidAndUseOnlyRealSignals(string id)
    {
        GameProfile p = GameProfileLoader.Load(id);
        Assert.Empty(GameProfileLoader.Validate(p));

        var available = new HashSet<string>(StringComparer.Ordinal);
        available.UnionWith(VideoSignalBank.CreateDefault(AnalysisFormat.Default).Names);
        available.UnionWith(AudioSignalBank.CreateDefault(AnalysisFormat.Default).Names);
        available.UnionWith(new RegionSignal(p.Regions, 384, 216).Names);
        Assert.Empty(GameProfileLoader.ValidateSignals(p, available));
    }

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
