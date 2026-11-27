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
}
