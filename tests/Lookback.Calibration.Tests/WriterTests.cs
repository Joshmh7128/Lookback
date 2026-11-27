using Lookback.Calibration.Probe;
using Lookback.Calibration.Profiles;
using Lookback.Contracts.V1;
using Lookback.Profiles.Model;
using Lookback.Stream.SessionLog;

namespace Lookback.Calibration.Tests;

public sealed class WriterTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("lookback-cal-").FullName;

    public WriterTests()
    {
        foreach (string f in Directory.EnumerateFiles(Repo.Profiles("cs2")))
        {
            File.Copy(f, Path.Combine(_dir, Path.GetFileName(f)));
        }
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static SessionStarted Player => new() { SourceKind = SourceKind.OfficialPlayer, Retention = Retention.Ephemeral };

    private static CalibrationEdits Edits => new()
    {
        Regions = new Dictionary<string, (double, double, double, double)> { ["kill_feed"] = (0.7, 0.05, 0.28, 0.2) },
        Thresholds = new Dictionary<ConditionKey, float> { [new ConditionKey("cs2", "elimination", 1)] = 0.045f },
    };

    [Fact]
    public void WritesOnlyTheConfirmedNumbers()
    {
        string path = Path.Combine(_dir, "cs2.json");
        string[] before = File.ReadAllLines(path);
        var writer = new CalibrationWriter(_dir);

        IReadOnlyList<string> files = writer.Save(Player, Edits, userConfirmed: true);

        Assert.Equal([path], files);
        string[] after = File.ReadAllLines(path);
        Assert.Equal(before.Length, after.Length);
        string[] changed = [.. before.Zip(after).Where(p => p.First != p.Second).Select(p => p.Second.Trim())];
        Assert.Equal(3, changed.Length); // the reference flag, the region line, the threshold line
        Assert.Contains(changed, l => l.StartsWith("\"calibrated\": true", StringComparison.Ordinal));
        Assert.Contains(changed, l => l.Contains("\"x\": 0.7, \"y\": 0.05, \"w\": 0.28, \"h\": 0.2", StringComparison.Ordinal));
        Assert.Contains(changed, l => l.Contains("\"rise\": 0.045", StringComparison.Ordinal));

        GameProfile p = writer.Catalog().Get("cs2");
        Assert.Equal(0.28, p.Regions.Single(r => r.Name == "kill_feed").W, 6);
        Assert.True(p.Reference.Calibrated);
        Assert.Equal(["cs2.arms-race.json", "cs2.competitive.json", "cs2.deathmatch.json", "cs2.json", "cs2.wingman.json"],
            Directory.EnumerateFiles(_dir).Select(Path.GetFileName).Order(StringComparer.Ordinal)); // no temp or extra files
    }

    [Fact]
    public void RefusesWithoutConfirmationAndTouchesNothing()
    {
        string path = Path.Combine(_dir, "cs2.json");
        string before = File.ReadAllText(path);
        var writer = new CalibrationWriter(_dir);

        Assert.Throws<RetentionViolationException>(() => writer.Save(Player, Edits, userConfirmed: false));
        Assert.Throws<RetentionViolationException>(() => writer.Save(new SessionStarted { SourceKind = SourceKind.OfficialPlayer, Retention = Retention.Owned }, Edits, userConfirmed: true));
        Assert.Equal(before, File.ReadAllText(path));
    }

    [Fact]
    public void RefusesARegionOutsideTheFrame()
    {
        var bad = new CalibrationEdits { Regions = new Dictionary<string, (double, double, double, double)> { ["kill_feed"] = (0.9, 0.05, 0.28, 0.2) } };
        Assert.Throws<ArgumentException>(() => new CalibrationWriter(_dir).Save(Player, bad, userConfirmed: true));
    }
}

internal static class Repo
{
    public static string Root
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

    public static string Profiles(string game) => Path.Combine(Root, "profiles", game);
}
