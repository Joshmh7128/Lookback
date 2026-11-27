using Lookback.Profiles.Model;
using Lookback.Testbench.Datasets;

namespace Lookback.Testbench.Tests;

/// <summary>
/// Every example manifest in <c>datasets/</c> parses, names a profile in its
/// game's tree, and labels only moments that profile can produce, so copying
/// an example is a working start for a real dataset.
/// </summary>
public class DatasetExampleTests
{
    public static TheoryData<string> Manifests()
    {
        var data = new TheoryData<string>();
        foreach (string path in Directory.EnumerateFiles(Path.Combine(RepoRoot(), "datasets"), "manifest*.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            data.Add(Path.GetRelativePath(RepoRoot(), path));
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Manifests))]
    public void ExampleManifestMatchesItsProfiles(string relativePath)
    {
        DatasetManifest manifest = DatasetManifest.Load(Path.Combine(RepoRoot(), relativePath));
        ProfileCatalog catalog = ProfileCatalog.BuiltIn;
        Assert.True(catalog.Contains(manifest.Game), $"no profile '{manifest.Game}'");

        foreach (DatasetEntry entry in manifest.Entries)
        {
            string id = entry.Profile ?? manifest.Game;
            Assert.Equal(manifest.Game, catalog.PathTo(id)[0].Id);

            GameProfile p = catalog.Resolve(id);
            var moments = p.Events.Select(e => e.Name).Concat(p.Composites.Select(c => c.Name)).ToHashSet(StringComparer.Ordinal);
            Assert.All(entry.Labels, l => Assert.True(moments.Contains(l.Event), $"{entry.File}: '{l.Event}' is not a moment of {id}"));
        }
    }

    private static string RepoRoot()
    {
        for (DirectoryInfo? d = new(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            if (File.Exists(Path.Combine(d.FullName, "Lookback.slnx")))
            {
                return d.FullName;
            }
        }

        throw new DirectoryNotFoundException("Lookback.slnx not found above the test output.");
    }
}
