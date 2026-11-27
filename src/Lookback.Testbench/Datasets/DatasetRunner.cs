using System.Text.Json;
using Lookback.Contracts.V1;
using Lookback.Core.Time;

namespace Lookback.Testbench.Datasets;

/// <summary>Result for one dataset entry.</summary>
internal sealed record DatasetEntryResult(
    string File,
    string? Report,
    int Events,
    IReadOnlyList<EventScore> Scores,
    string? ExpectedProfile,
    string? FinalProfile,
    double? DetectionAccuracy,
    CandidateScore? Candidates);

/// <summary>Everything a dataset run produced.</summary>
internal sealed record DatasetResult(
    IReadOnlyList<DatasetEntryResult> Entries,
    IReadOnlyList<EventScore> Overall,
    double? DetectionAccuracy,
    CandidateScore? Candidates);

/// <summary>
/// Runs every entry of a game's dataset through the testbench, starting from
/// the game itself so sub-games and modes are detected rather than told, then
/// scores three things per entry and overall:
/// <list type="bullet">
/// <item><b>Events</b> against labelled moments (precision and recall per event).</item>
/// <item><b>Detection</b>: the share of the footage during which the active
/// profile was the one the entry says it shows.</item>
/// <item><b>Candidates</b> against labelled clips, by time overlap.</item>
/// </list>
/// This is the automated per-game QA loop: change a rule, a region or a
/// fingerprint, rerun, and watch the numbers move.
/// </summary>
internal static class DatasetRunner
{
    private static readonly JsonSerializerOptions ScoresJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public static async Task<DatasetResult> RunAsync(
        string manifestPath, string outputDirectory, double toleranceSeconds, string? profileOverride, CancellationToken ct, IReadOnlyList<string>? extraProfiles = null)
    {
        DatasetManifest manifest = DatasetManifest.Load(manifestPath);
        string root = Path.GetDirectoryName(Path.GetFullPath(manifestPath)) ?? ".";
        var entries = new List<DatasetEntryResult>();
        double detectedSeconds = 0, labelledSeconds = 0;

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
                    ExtraProfiles = extraProfiles ?? [],
                    Game = manifest.Game,
                    SessionId = $"{manifest.Game}-{index:000}-{Path.GetFileNameWithoutExtension(TestbenchRunner.MakeSessionId(file))[16..]}",
                },
                null,
                ct).ConfigureAwait(false);

            // Labels are in seconds from the start of the file; detections are QPC from the same origin.
            double Sec(ulong q) => QpcTime.ToTimeSpan((long)q, TestbenchRunner.Frequency).TotalSeconds;
            var detections = r.Events.Select(e => (e.Name, Sec(e.AtQpc))).ToList();
            double duration = r.Media.Duration.TotalSeconds;

            double? accuracy = null;
            if (entry.Profile is not null)
            {
                double right = ProfileSeconds(r.Detections, entry.Profile, duration);
                accuracy = duration > 0 ? right / duration : null;
                detectedSeconds += right;
                labelledSeconds += duration;
            }

            CandidateScore? candidates = entry.Clips.Count == 0
                ? null
                : CandidateScoring.Score(entry.Clips, [.. r.Candidates.Select(c => (Sec(c.StartQpc), Sec(c.EndQpc)))]);

            entries.Add(new DatasetEntryResult(
                entry.File,
                r.ReportPath,
                r.Events.Count,
                EventScoring.Score(entry.Labels, detections, toleranceSeconds),
                entry.Profile,
                r.Detections.Count == 0 ? null : r.Detections[^1].ProfileId,
                accuracy,
                candidates));
        }

        // Sum per-entry counts: every entry has its own time origin, so pooling
        // timestamps across files would match a label in one to a detection in another.
        var result = new DatasetResult(
            entries,
            EventScoring.Sum(entries.SelectMany(e => e.Scores)),
            labelledSeconds > 0 ? detectedSeconds / labelledSeconds : null,
            entries.Any(e => e.Candidates is not null) ? CandidateScoring.Sum(entries.Where(e => e.Candidates is not null).Select(e => e.Candidates!)) : null);

        Directory.CreateDirectory(outputDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(outputDirectory, "scores.json"),
            JsonSerializer.Serialize(new { manifest.Game, toleranceSeconds, result.Overall, result.DetectionAccuracy, result.Candidates, entries }, ScoresJson),
            ct).ConfigureAwait(false);
        return result;
    }

    /// <summary>Seconds during which <paramref name="expected"/> was the active profile.</summary>
    internal static double ProfileSeconds(IReadOnlyList<ProfileDetected> detections, string expected, double duration)
    {
        double total = 0;
        for (int i = 0; i < detections.Count; i++)
        {
            if (detections[i].ProfileId != expected)
            {
                continue;
            }

            double start = QpcTime.ToTimeSpan((long)detections[i].AtQpc, TestbenchRunner.Frequency).TotalSeconds;
            double end = i + 1 < detections.Count ? QpcTime.ToTimeSpan((long)detections[i + 1].AtQpc, TestbenchRunner.Frequency).TotalSeconds : duration;
            total += Math.Max(0, Math.Min(end, duration) - start);
        }

        return total;
    }
}
