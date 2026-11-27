using Lookback.Profiles.Model;
using Lookback.Testbench.Catalog;

namespace Lookback.Testbench.Tests;

public class RobloxIdResolverTests
{
    [Theory]
    [InlineData("Murder Mystery 2", "Murder Mystery 2", true)]
    [InlineData("BedWars", "BedWars 🔫 [UPDATE]", true)]
    [InlineData("Adopt Me!", "[🎃] Adopt Me!", true)]
    [InlineData("Fisch", "Brookhaven 🏡RP", false)]
    public void NamesMatchLoosely(string ours, string theirs, bool expected) =>
        Assert.Equal(expected, RobloxIdResolver.NamesMatch(ours, theirs));

    [Fact]
    public void WritesOnlyTheIdsLineAndTheResultStillLoads()
    {
        string json = """
            {
              // keep this comment
              "id": "roblox.fisch",
              "displayName": "Fisch",
              "parent": "roblox",
              "level": "subgame",
              "ids": {},
              "events": []
            }
            """;
        string updated = RobloxIdResolver.ReplaceIdsLine(json, 123, ["456", "789"]);
        Assert.Contains("// keep this comment", updated, StringComparison.Ordinal);
        Assert.Contains("""  "ids": { "roblox.universe": ["123"], "roblox.place": ["456", "789"] },""", updated, StringComparison.Ordinal);
        GameProfile p = GameProfileLoader.Parse(updated);
        Assert.Equal(["123"], p.Ids["roblox.universe"]);
    }

    [Fact]
    public void WindowsLineEndingsAreMatchedAndKept()
    {
        string json = "{\r\n  \"id\": \"roblox.x\",\r\n  \"ids\": {},\r\n  \"events\": []\r\n}\r\n";
        string updated = RobloxIdResolver.ReplaceIdsLine(json, 1, ["2"]);
        Assert.Equal("{\r\n  \"id\": \"roblox.x\",\r\n  \"ids\": { \"roblox.universe\": [\"1\"], \"roblox.place\": [\"2\"] },\r\n  \"events\": []\r\n}\r\n", updated);
    }
}
