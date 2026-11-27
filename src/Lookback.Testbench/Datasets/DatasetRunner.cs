using System.Text.Json;
using Lookback.Core.Time;

namespace Lookback.Testbench.Datasets;

/// <summary>Result for one dataset entry.</summary>
internal sealed record DatasetEntryResult(string File, string? Report, int Events, IReadOnlyList<EventScore> Scores);

/// <summary>
/// Runs every entry of a game's dataset through the testbench with that game's
/// profile, then scores detections against labels per entry and overall. This
/// is the automated per-game QA for profile rules: change a threshold, rerun,
/// and see precision and recall move.
/// </summary>
internal static class DatasetRunner
{
    private static readonly JsonSerializerOptions ScoresJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public static async Task<(IReadOnlyList<DatasetEntryResult> Entries, IReadOnlyList<EventScore> Overall)> RunAsync(
        string manifestPath, string outputDirectory, double toleranceSeconds, string? profileOverride, CancellationToken ct)
    {
        DatasetManifest manifest = DatasetManifest.Load(manifestPath);
        string root = Path.GetDirectoryName(Path.GetFullPath(manifestPath)) ?? ".";
        var entries = new List<DatasetEntryResult>();

        foreach ((DatasetEntry entry, int index) in manifest.Entries.Select((e, i) => (e, i)))
        {
            string file = Path.GetFullPath(Path.Combine(root, entry.File));
            AnalyzeResult r = await TestbenchRunner.AnalyzeAsync(
                new AnalyzeOptions
                {
                    File = file,
                    OutputDirectory = outputDirectory,
                    Retention = entry.Retention.ToRetention(),
                    PermissionRef = entry.PermissionRef,
                    Title = entry.Title,
                    Profile = profileOverride ?? manifest.Game,
                    Game = manifest.Game,
                    SessionId = $"{manifest.Game}-{index:000}-{Path.GetFileNameWithoutExtension(TestbenchRunner.MakeSessionId(file))[16..]}",
                },
                null,
                ct).ConfigureAwait(false);

            // Labels are in seconds from the start of the file; detections are QPC from the same origin.
            var detections = r.Events.Select(e => (e.Name, QpcTime.ToTimeSpan((long)e.AtQpc, TestbenchRunner.Frequency).TotalSeconds)).ToList();
            entries.Add(new DatasetEntryResult(entry.File, r.ReportPath, r.Events.Count, EventScoring.Score(entry.Labels, detections, toleranceSeconds)));
        }

        // Sum per-entry counts: every entry has its own time origin, so pooling
        // timestamps across files would match a label in one to a detection in another.
        IReadOnlyList<EventScore> overall = EventScoring.Sum(entries.SelectMany(e => e.Scores));
        Directory.CreateDirectory(outputDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(outputDirectory, "scores.json"),
            JsonSerializer.Serialize(new { manifest.Game, toleranceSeconds, overall, entries }, ScoresJson),
            ct).ConfigureAwait(false);
        return (entries, overall);
    }
}
