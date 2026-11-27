using System.Text.Json;
using Lookback.Testbench.Catalog;

namespace Lookback.Testbench.Tests;

/// <summary>The JSON contract the desktop app's profile editor uses: <c>profile-view</c> and <c>profile-save</c>.</summary>
public sealed class ProfileEditCommandTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("lookback-edit-cmd-").FullName;

    public ProfileEditCommandTests()
    {
        string root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "Lookback.slnx")))
        {
            root = Path.GetDirectoryName(root) ?? throw new DirectoryNotFoundException("Repository root not found.");
        }

        Directory.CreateDirectory(Path.Combine(_dir, "cs2"));
        foreach (string f in Directory.EnumerateFiles(Path.Combine(root, "profiles", "cs2")))
        {
            File.Copy(f, Path.Combine(_dir, "cs2", Path.GetFileName(f)));
        }
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void ViewListsTheTreeAndResolvesAProfileWithSources()
    {
        (int exit, string json) = ProfileEditCommand.View(_dir, "cs2.competitive");
        Assert.Equal(0, exit);
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        Assert.True(root.GetProperty("ok").GetBoolean());
        Assert.Equal("cs2", root.GetProperty("profiles")[0].GetProperty("id").GetString());
        JsonElement view = root.GetProperty("view");
        Assert.Equal("cs2/cs2.competitive.json", view.GetProperty("file").GetString());
        Assert.Equal(["cs2", "cs2.competitive"], view.GetProperty("path").EnumerateArray().Select(p => p.GetString()));
        Assert.Contains(view.GetProperty("events").EnumerateArray(), e => e.GetProperty("source").GetString() == "cs2");
        Assert.All(view.GetProperty("clipper").GetProperty("numbers").EnumerateArray(), n => Assert.Equal("defaults", n.GetProperty("source").GetString()));
        Assert.Contains(view.GetProperty("signals").EnumerateArray(), s => s.GetString() == "audio.rms_db");
    }

    [Fact]
    public void SaveWritesValidEditsAndReportsTheFiles()
    {
        (int exit, string json) = ProfileEditCommand.Save(_dir, "cs2.competitive", """{ "clipper": { "high": 1.9 }, "components": { "scene.cut": 0 } }""");
        Assert.Equal(0, exit);
        using JsonDocument doc = JsonDocument.Parse(json);
        Assert.Equal(["cs2/cs2.competitive.json"], doc.RootElement.GetProperty("files").EnumerateArray().Select(f => f.GetString()));
        JsonElement high = doc.RootElement.GetProperty("view").GetProperty("clipper").GetProperty("numbers").EnumerateArray().Single(n => n.GetProperty("field").GetString() == "high");
        Assert.Equal("cs2.competitive", high.GetProperty("source").GetString());
    }

    [Theory]
    [InlineData("""{ "capturePolicy": { "allowPresentHook": true } }""", "unknown field 'capturePolicy'")]
    [InlineData("""{ "events": [ { "name": "elimination", "when": [] } ] }""", "unknown field 'when'")]
    [InlineData("""{ "clipper": { "low": 5 } }""", "must be below high")]
    [InlineData("""{ "clipper": { "high": "big" } }""", "must be a number")]
    [InlineData("not json", "not JSON")]
    public void SaveRefusesWithAMessageAndWritesNothing(string edits, string message)
    {
        string before = string.Concat(Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).Select(File.ReadAllText));
        (int exit, string json) = ProfileEditCommand.Save(_dir, "cs2", edits);
        Assert.Equal(1, exit);
        using JsonDocument doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains(message, doc.RootElement.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Equal(before, string.Concat(Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).Select(File.ReadAllText)));
    }
}
