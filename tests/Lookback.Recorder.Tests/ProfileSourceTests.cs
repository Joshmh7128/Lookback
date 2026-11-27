using Lookback.Profiles.Model;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lookback.Recorder.Tests;

/// <summary>
/// Live recording runs on the profiles folder as it is when a session starts,
/// so the profile editor's saves apply without a rebuild; a folder that does
/// not load never stops recording, it falls back to the built-in profiles.
/// </summary>
public sealed class ProfileSourceTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("lookback-profiles-").FullName;

    public ProfileSourceTests()
    {
        string profiles = Path.Combine(Root, "profiles");
        foreach (string f in Directory.EnumerateFiles(profiles, "*.json", SearchOption.AllDirectories))
        {
            string to = Path.Combine(_dir, Path.GetRelativePath(profiles, f));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(f, to);
        }
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static string Root
    {
        get
        {
            for (string? d = AppContext.BaseDirectory; d is not null; d = Path.GetDirectoryName(d))
            {
                if (File.Exists(Path.Combine(d, "Lookback.slnx")))
                {
                    return d;
                }
            }

            throw new DirectoryNotFoundException("Repository root not found.");
        }
    }

    private string Valorant => Path.Combine(_dir, "valorant", "valorant.json");

    private static float Weight(ProfileCatalog c) => c.Resolve("valorant.standard").Events.Single(e => e.Name == "elimination").Weight;

    [Fact]
    public void LoadsTheFolderAndPicksUpAnEdit()
    {
        var source = new ProfileSource(_dir, NullLogger.Instance);
        ProfileCatalog first = source.Current();
        Assert.True(source.FromFolder);
        Assert.Same(first, source.Current()); // unchanged folder: no reload
        Assert.Equal(1.0f, Weight(first));

        File.WriteAllText(Valorant, File.ReadAllText(Valorant).Replace("\"weight\": 1.0,", "\"weight\": 1.75,", StringComparison.Ordinal));
        File.SetLastWriteTimeUtc(Valorant, DateTime.UtcNow.AddMinutes(1)); // a coarse clock must still see the change
        ProfileCatalog second = source.Current();
        Assert.NotSame(first, second);
        Assert.Equal(1.75f, Weight(second));
    }

    [Fact]
    public void FallsBackToTheBuiltInProfilesWhenTheFolderDoesNotLoad()
    {
        var source = new ProfileSource(_dir, NullLogger.Instance);
        File.WriteAllText(Valorant, File.ReadAllText(Valorant).Replace("\"weight\": 1.0,", "\"wieght\": 1.0,", StringComparison.Ordinal));
        Assert.Same(ProfileCatalog.BuiltIn, source.Current());
        Assert.False(source.FromFolder);

        // A clipper low above the default high loads per file but could not run: refused too.
        File.Copy(Path.Combine(Root, "profiles", "valorant", "valorant.json"), Valorant, overwrite: true);
        string standard = Path.Combine(_dir, "valorant", "valorant.standard.json");
        File.WriteAllText(standard, File.ReadAllText(standard).TrimEnd().TrimEnd('}') + ",\n  \"clipper\": { \"low\": 2.0 }\n}\n");
        File.SetLastWriteTimeUtc(standard, DateTime.UtcNow.AddMinutes(1));
        Assert.Same(ProfileCatalog.BuiltIn, source.Current());
    }

    [Fact]
    public void WithoutAFolderUsesTheBuiltInProfiles()
    {
        Assert.Same(ProfileCatalog.BuiltIn, new ProfileSource(null, NullLogger.Instance).Current());
        Assert.Same(ProfileCatalog.BuiltIn, new ProfileSource(Path.Combine(_dir, "missing"), NullLogger.Instance).Current());
    }
}
