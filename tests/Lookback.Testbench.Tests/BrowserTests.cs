using Lookback.Profiles.Model;
using Lookback.Testbench.Browse;

namespace Lookback.Testbench.Tests;

public class BrowserTests
{
    private static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "lookback-browse", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void BuiltInProfilesOnlyReadSignalsThePipelineProduces()
    {
        BrowserData data = BrowserBuilder.Build(ProfileCatalog.BuiltIn, null, [], Path.Combine(TempDir(), "index.html"));

        Assert.Equal(ProfileCatalog.BuiltIn.All.Count(), data.Profiles.Count);
        Assert.All(data.Profiles, p => Assert.All(p.Components, c => Assert.True(c.Known, $"{p.Id} reads unknown signal {c.Signal}")));
    }

    [Fact]
    public void ChildShowsWhatItInheritsAddsAndSwitchesOff()
    {
        BrowserData data = BrowserBuilder.Build(ProfileCatalog.BuiltIn, null, [], Path.Combine(TempDir(), "index.html"));
        ProfileView arsenal = data.Profiles.Single(p => p.Id == "roblox.arsenal");

        Assert.Equal(["roblox", "roblox.arsenal"], arsenal.Path);
        Assert.Equal("roblox.arsenal", arsenal.Events.Single(e => e.Name == "elimination").From);
        Assert.Equal("roblox", arsenal.Events.Single(e => e.Name == "impact").From);
        Assert.DoesNotContain(arsenal.Events, e => e.Name == "centre_popup");
        Assert.Contains("centre_popup", arsenal.Disabled);
        Assert.Equal("roblox", arsenal.Regions.Single(r => r.Name == "topbar").From);
        Assert.Contains("elimination", arsenal.Regions.Single(r => r.Name == "arsenal_notice").UsedBy);

        // Most prominent first: elimination carries its composites' weight too.
        Assert.Equal("elimination", arsenal.Events[0].Name);
        Assert.Equal("region.arsenal_notice.activity", arsenal.Components[0].Signal);
        Assert.Equal("region", arsenal.Components[0].Kind);
        Assert.Equal(0.15, arsenal.Events[0].Boosts.Single(b => b.Signal.Contains("kill_feed", StringComparison.Ordinal)).Add); // floats are shown rounded
    }

    [Fact]
    public void ManifestsAreCheckedAndResultsJoinedToTheirEntries()
    {
        string root = TempDir();
        string game = Path.Combine(root, "datasets", "roblox");
        Directory.CreateDirectory(Path.Combine(game, "media"));
        File.WriteAllText(Path.Combine(game, "media", "a.mp4"), "not really a video");
        File.WriteAllText(Path.Combine(game, "manifest.json"), """
            {
              "game": "roblox",
              "entries": [
                { "file": "media/a.mp4", "profile": "roblox.arsenal",
                  "labels": [ { "event": "elimination", "at": 3 }, { "event": "headshot", "at": 4 } ] },
                { "file": "media/b.mp4", "retention": "permissioned", "profile": "overwatch.push" }
              ]
            }
            """);
        string results = Path.Combine(root, "out");
        Directory.CreateDirectory(results);
        File.WriteAllText(Path.Combine(results, "scores.json"), """
            { "game": "roblox", "toleranceSeconds": 1, "overall": [], "detectionAccuracy": 0.9,
              "entries": [ { "file": "media/a.mp4", "report": "a.html", "events": 4, "finalProfile": "roblox.arsenal", "detectionAccuracy": 0.9,
                             "scores": [ { "event": "elimination", "truePositives": 1, "falsePositives": 2, "falseNegatives": 0 } ] } ] }
            """);

        BrowserData data = BrowserBuilder.Build(ProfileCatalog.BuiltIn, Path.Combine(root, "datasets"), [results], Path.Combine(root, "browse", "index.html"));

        DatasetView d = Assert.Single(data.Datasets);
        EntryView a = d.Entries[0], b = d.Entries[1];
        Assert.True(a.MediaFound);
        Assert.Contains(a.Issues, i => i.Contains("'headshot'", StringComparison.Ordinal));
        Assert.Equal(4, a.Result!.Events);
        Assert.Equal("../out/a.html", a.Result.Report);
        Assert.False(b.MediaFound);
        Assert.Contains(b.Issues, i => i.Contains("permissionRef", StringComparison.Ordinal));
        Assert.Contains(b.Issues, i => i.Contains("not in the 'roblox' tree", StringComparison.Ordinal));

        ProfileView arsenal = data.Profiles.Single(p => p.Id == "roblox.arsenal");
        Assert.Equal(1, arsenal.Coverage.LabelsByEvent["elimination"]);
        Assert.Contains("headshot", arsenal.Coverage.UnknownLabels);
        Assert.Contains("multikill", arsenal.Coverage.Unlabelled);
        Assert.Equal(new ScoreView("elimination", 1, 2, 0), arsenal.Coverage.Scores.Single());

        // The game's coverage includes footage of its sub-games.
        Assert.Equal(1, data.Profiles.Single(p => p.Id == "roblox").Coverage.Entries);
    }

    [Fact]
    public void ManifestTextCannotBreakOutOfThePage()
    {
        string root = TempDir();
        Directory.CreateDirectory(Path.Combine(root, "generic"));
        File.WriteAllText(Path.Combine(root, "generic", "manifest.json"), """
            { "game": "generic", "entries": [ { "file": "x.mp4", "source": "</script><script>alert(1)</script>" } ] }
            """);

        string html = BrowserWriter.Render(BrowserBuilder.Build(ProfileCatalog.BuiltIn, root, [], Path.Combine(root, "index.html")));

        Assert.DoesNotContain("</script><script>alert", html, StringComparison.Ordinal);
        Assert.DoesNotContain("__DATA__", html, StringComparison.Ordinal);
        Assert.Contains("\\u003C/script\\u003E", html, StringComparison.Ordinal);
    }

    [Fact]
    public void BrokenManifestIsReportedNotFatal()
    {
        string root = TempDir();
        Directory.CreateDirectory(Path.Combine(root, "overwatch"));
        File.WriteAllText(Path.Combine(root, "overwatch", "manifest.json"), """{ "game": "overwatch", "entries": [ { "file": "a.mp4", "lables": [] } ] }""");

        DatasetView d = Assert.Single(BrowserBuilder.Build(ProfileCatalog.BuiltIn, root, [], Path.Combine(root, "index.html")).Datasets);
        Assert.Empty(d.Entries);
        Assert.Contains("lables", Assert.Single(d.Problems), StringComparison.Ordinal);
    }
}
