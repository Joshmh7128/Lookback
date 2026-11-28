using Lookback.Calibration.Profiles;
using Lookback.Profiles.Model;

namespace Lookback.Calibration.Tests;

/// <summary>
/// The profile editor: edits land on exactly the lines they change, overrides
/// come and go without leaving anything behind, and anything the recorder
/// could not run is refused before a file is touched.
/// </summary>
public sealed class ProfileEditorTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("lookback-edit-").FullName;

    public ProfileEditorTests()
    {
        foreach (string f in Directory.EnumerateFiles(Repo.Profiles("valorant")))
        {
            File.Copy(f, Path.Combine(_dir, Path.GetFileName(f)));
        }
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string File_(string name) => Path.Combine(_dir, name);

    private string[] Snapshot() => [.. Directory.EnumerateFiles(_dir).Order(StringComparer.Ordinal).Select(File.ReadAllText)];

    private static string[] Changed(string[] before, string[] after) =>
        [.. before.Zip(after).Where(p => p.First != p.Second).Select(p => p.Second.Trim())];

    [Fact]
    public void RuleEditsChangeOnlyTheirLinesInTheFileThatDefinesTheRule()
    {
        string[] before = File.ReadAllLines(File_("valorant.json"));
        string standard = File.ReadAllText(File_("valorant.standard.json"));
        var editor = new ProfileEditor(_dir);

        ProfileSaveResult r = editor.Save("valorant.standard", new ProfileEdits
        {
            Events = [new RuleEdit { Name = "elimination", Weight = 1.5, Thresholds = new Dictionary<int, double> { [0] = 0.06 } }],
            Composites = [new CompositeEdit { Name = "multikill", Weight = 3 }],
        });

        Assert.Equal([File_("valorant.json")], r.Files);
        Assert.Equal(standard, File.ReadAllText(File_("valorant.standard.json")));
        string[] after = File.ReadAllLines(File_("valorant.json"));
        Assert.Equal(before.Length, after.Length);
        string[] changed = Changed(before, after);
        Assert.Equal(3, changed.Length);
        Assert.Contains("\"weight\": 1.5,", changed);
        Assert.Contains(changed, l => l.Contains("\"rise\": 0.06 }", StringComparison.Ordinal));
        Assert.Contains(changed, l => l.StartsWith("{ \"name\": \"multikill\"", StringComparison.Ordinal) && l.Contains("\"weight\": 3.0,", StringComparison.Ordinal));

        RuleView elimination = r.View.Events.Single(e => e.Name == "elimination");
        Assert.Equal("valorant", elimination.Source);
        Assert.Equal(1.5f, elimination.Weight);
        Assert.Equal(0.06f, elimination.Conditions[0].Value);
        Assert.Equal(3f, r.View.Composites.Single(c => c.Name == "multikill").Weight);
    }

    [Fact]
    public void ClipperOverridesAreAddedToTheEditedProfileAndRemovedWithoutTrace()
    {
        string original = File.ReadAllText(File_("valorant.standard.json"));
        var editor = new ProfileEditor(_dir);
        Assert.Equal(ProfileEditor.Defaults, editor.View("valorant.standard").Clipper.Numbers.Single(n => n.Field == "high").Source);

        ProfileSaveResult r = editor.Save("valorant.standard", new ProfileEdits
        {
            Clipper = new Dictionary<string, double?> { ["high"] = 1.8, ["preMs"] = 7000 },
            Components = new Dictionary<string, double?> { ["region.kill_feed.activity"] = 1.2, ["scene.cut"] = 0 },
        });

        Assert.Equal([File_("valorant.standard.json")], r.Files);
        string text = File.ReadAllText(File_("valorant.standard.json"));
        Assert.Contains("\n  \"clipper\": {\n    \"high\": 1.8,\n    \"preMs\": 7000,\n    \"components\": [\n      { \"signal\": \"region.kill_feed.activity\", \"weight\": 1.2 },\n      { \"signal\": \"scene.cut\", \"weight\": 0.0 }\n    ]\n  }\n}", text, StringComparison.Ordinal);

        ClipperView c = r.View.Clipper;
        Assert.Equal(("valorant.standard", 1.8), c.Numbers.Where(n => n.Field == "high").Select(n => (n.Source, Math.Round(n.Value, 3))).Single());
        Assert.Equal(ProfileEditor.Defaults, c.Numbers.Single(n => n.Field == "low").Source);
        Assert.Equal(("valorant.standard", 0f), c.Components.Where(x => x.Signal == "scene.cut").Select(x => (x.Source, x.Weight)).Single());
        Assert.Equal(0.05f, c.Components.Single(x => x.Signal == "scene.cut").MinSpread); // an unset spread is still inherited
        Assert.Equal(1.2f, c.Components.Single(x => x.Signal == "region.kill_feed.activity").Weight);
        Assert.Equal(ProfileEditor.Defaults, c.Components.Single(x => x.Signal == "audio.rms_db").Source);

        // Changing an override edits it in place; removing every override restores the file byte for byte.
        editor.Save("valorant.standard", new ProfileEdits { Components = new Dictionary<string, double?> { ["scene.cut"] = 0.25 } });
        Assert.Contains("{ \"signal\": \"scene.cut\", \"weight\": 0.25 }", File.ReadAllText(File_("valorant.standard.json")), StringComparison.Ordinal);
        editor.Save("valorant.standard", new ProfileEdits
        {
            Clipper = new Dictionary<string, double?> { ["high"] = null, ["preMs"] = null },
            Components = new Dictionary<string, double?> { ["region.kill_feed.activity"] = null, ["scene.cut"] = null },
        });
        Assert.Equal(original, File.ReadAllText(File_("valorant.standard.json")));
    }

    [Fact]
    public void AChildInheritsAParentsOverride()
    {
        var editor = new ProfileEditor(_dir);
        editor.Save("valorant", new ProfileEdits { Clipper = new Dictionary<string, double?> { ["low"] = 0.7 } });

        ClipperNumberView low = editor.View("valorant.standard").Clipper.Numbers.Single(n => n.Field == "low");
        Assert.Equal("valorant", low.Source);
        Assert.Equal(0.7, low.Value, 4);
    }

    public static TheoryData<string> Refused => ["low-over-defaults", "unknown-signal", "negative-weight", "unknown-event", "bad-condition", "fractional-ms", "unknown-field"];

    [Theory]
    [MemberData(nameof(Refused))]
    public void RefusesWhatTheRecorderCouldNotRunAndTouchesNothing(string what)
    {
        string[] before = Snapshot();
        var editor = new ProfileEditor(_dir);
        ProfileEdits edits = what switch
        {
            // Valid on its own, but above the clipper's default high (1.6) once the defaults fill in.
            "low-over-defaults" => new() { Clipper = new Dictionary<string, double?> { ["low"] = 2.0 } },
            "unknown-signal" => new() { Components = new Dictionary<string, double?> { ["memory.health"] = 1 } },
            "negative-weight" => new() { Events = [new RuleEdit { Name = "elimination", Weight = -1 }] },
            "unknown-event" => new() { Events = [new RuleEdit { Name = "no_such_event", Weight = 1 }] },
            "bad-condition" => new() { Events = [new RuleEdit { Name = "elimination", Thresholds = new Dictionary<int, double> { [5] = 0.1 } }] },
            "fractional-ms" => new() { Clipper = new Dictionary<string, double?> { ["preMs"] = 1500.5 } },
            _ => new() { Clipper = new Dictionary<string, double?> { ["allowPresentHook"] = 1 } },
        };

        Exception ex = Assert.ThrowsAny<Exception>(() => editor.Save("valorant.standard", edits));
        Assert.True(ex is InvalidDataException or ArgumentException, ex.ToString());
        Assert.Equal(before, Snapshot());
        Assert.Equal(["valorant.deathmatch.json", "valorant.json", "valorant.spike-rush.json", "valorant.standard.json", "valorant.team-deathmatch.json"],
            Directory.EnumerateFiles(_dir).Select(Path.GetFileName).Order(StringComparer.Ordinal)); // no temp files
        if (what == "low-over-defaults")
        {
            Assert.Contains("low (2) must be below high (1.6)", ex.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void RefusesToSaveIntoAFolderThatDoesNotLoad()
    {
        File.WriteAllText(File_("valorant.json"), File.ReadAllText(File_("valorant.json")).Replace("\"weight\": 1.0,", "\"wieght\": 1.0,", StringComparison.Ordinal));
        var editor = new ProfileEditor(_dir);
        InvalidDataException ex = Assert.Throws<InvalidDataException>(() => editor.Save("valorant", new ProfileEdits { Clipper = new Dictionary<string, double?> { ["high"] = 2 } }));
        Assert.Contains("wieght", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>Every shipped profile takes a clipper override and gives it back exactly, whatever its layout.</summary>
    [Fact]
    public void EveryShippedProfileRoundTripsAnOverride()
    {
        string all = Path.Combine(_dir, "all");
        foreach (string f in Directory.EnumerateFiles(Path.Combine(Repo.Root, "profiles"), "*.json", SearchOption.AllDirectories))
        {
            string to = Path.Combine(all, Path.GetRelativePath(Path.Combine(Repo.Root, "profiles"), f));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(f, to);
        }

        var editor = new ProfileEditor(all);
        Dictionary<string, string> files = ProfileFolder.Files(all);
        foreach ((string id, string path) in files)
        {
            string original = File.ReadAllText(path);
            editor.Save(id, new ProfileEdits { Clipper = new Dictionary<string, double?> { ["peakScore"] = 0.75 }, Components = new Dictionary<string, double?> { ["motion.energy"] = 0.9 } });
            GameProfile p = GameProfileLoader.Load(path);
            Assert.Equal(0.75f, p.Clipper.PeakScore);
            Assert.Equal(0.9f, p.Clipper.Components.Single().Weight);
            editor.Save(id, new ProfileEdits { Clipper = new Dictionary<string, double?> { ["peakScore"] = null }, Components = new Dictionary<string, double?> { ["motion.energy"] = null } });
            Assert.Equal(original, File.ReadAllText(path));
        }
    }
}
