using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Lookback.Profiles.Model;
using Lookback.Testbench.Report;

namespace Lookback.Testbench.Datasets;

/// <summary>One learned condition and how well it separates on the training footage.</summary>
internal sealed record LearnedCondition(string Signal, bool Above, float Threshold, double Separation, double PositiveRate, double NegativeRate);

/// <summary>A learned fingerprint for one profile, against its siblings (or its parent alone).</summary>
internal sealed record LearnedFingerprint(string Profile, IReadOnlyList<LearnedCondition> Conditions, double MinScore, int PositiveFrames, int NegativeFrames, string? Problem);

/// <summary>
/// Learns visual fingerprints for sub-games and modes from labelled footage.
/// Every dataset entry says which profile it shows; the footage is analysed
/// from the game down, so every region signal of the game's tree is measured.
/// Then, for each profile, frames from footage of that profile (positives) are
/// compared with frames from footage of its parent's other children or of the
/// parent alone (negatives):
/// <list type="number">
/// <item>Each video signal gets a separation score, the gap between the two
/// means over the sum of the two standard deviations.</item>
/// <item>The best-separated signals (up to three, separation of at least 1
/// and at least half the best) become conditions, with the threshold where the two distributions cross
/// (means weighted by the other side's spread) and the direction of the gap.</item>
/// <item><c>minScore</c> is set halfway between the average share of conditions
/// that hold on positive frames and on negative frames.</item>
/// </list>
/// The result is written as profile files with <c>detect.visual</c> filled in,
/// ready to load with <c>--profiles</c> and to check with the dataset command.
/// </summary>
internal static class FingerprintLearner
{
    public const int MaxConditions = 3;
    public const double MinSeparation = 1.0;

    private static readonly JsonSerializerOptions ProfileJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static async Task<IReadOnlyList<LearnedFingerprint>> RunAsync(string manifestPath, string outputDirectory, IReadOnlyList<string> extraProfiles, CancellationToken ct)
    {
        DatasetManifest manifest = DatasetManifest.Load(manifestPath);
        string root = Path.GetDirectoryName(Path.GetFullPath(manifestPath)) ?? ".";
        ProfileCatalog catalog = extraProfiles.Count > 0 ? ProfileCatalog.WithFiles(extraProfiles.SelectMany(TestbenchRunner.ExpandProfilePath)) : ProfileCatalog.BuiltIn;
        var samples = new List<(string Profile, Dictionary<string, List<float>> Frames)>();

        foreach ((DatasetEntry entry, int index) in manifest.Entries.Select((e, i) => (e, i)))
        {
            if (entry.Profile is null)
            {
                continue;
            }

            if (!catalog.Contains(entry.Profile))
            {
                throw new InvalidDataException($"{entry.File}: unknown profile '{entry.Profile}'.");
            }

            string file = Path.GetFullPath(Path.Combine(root, entry.File));
            AnalyzeResult r = await TestbenchRunner.AnalyzeAsync(
                new AnalyzeOptions
                {
                    File = file,
                    OutputDirectory = outputDirectory,
                    Retention = entry.Retention.ToRetention(),
                    PermissionRef = entry.PermissionRef,
                    Title = entry.Title,
                    Profile = manifest.Game,
                    ExtraProfiles = extraProfiles,
                    Game = manifest.Game,
                    SessionId = $"{manifest.Game}-fp-{index:000}",
                },
                null,
                ct).ConfigureAwait(false);

            TimelineData t = await TimelineBuilder.BuildAsync(r.SessionBasePath!, null, ct).ConfigureAwait(false);
            samples.Add((entry.Profile, t.Lanes.Where(l => l.Family == "video").ToDictionary(l => l.Name, l => l.Values, StringComparer.Ordinal)));
        }

        var learned = new List<LearnedFingerprint>();
        IEnumerable<string> targets = samples
            .SelectMany(s => catalog.PathTo(s.Profile).Skip(1).Select(p => p.Id))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);
        foreach (string id in targets)
        {
            string parent = catalog.Get(id).Parent!;
            var positives = samples.Where(s => OnPath(catalog, s.Profile, id)).Select(s => s.Frames).ToList();
            var negatives = samples.Where(s => OnPath(catalog, s.Profile, parent) && !OnPath(catalog, s.Profile, id)).Select(s => s.Frames).ToList();
            learned.Add(Learn(id, positives, negatives));
        }

        string dir = Path.Combine(outputDirectory, "learned");
        Directory.CreateDirectory(dir);
        foreach (LearnedFingerprint f in learned.Where(f => f.Problem is null))
        {
            GameProfile own = catalog.Get(f.Profile);
            GameProfile updated = own with
            {
                Version = own.Version + 1,
                Detect = own.Detect with
                {
                    Visual = [.. f.Conditions.Select(c => c.Above ? new Condition { Signal = c.Signal, Above = c.Threshold } : new Condition { Signal = c.Signal, Below = c.Threshold })],
                    MinScore = f.MinScore,
                },
            };
            await File.WriteAllTextAsync(Path.Combine(dir, f.Profile + ".json"), JsonSerializer.Serialize(updated, ProfileJson), ct).ConfigureAwait(false);
        }

        return learned;
    }

    internal static LearnedFingerprint Learn(string id, IReadOnlyList<Dictionary<string, List<float>>> positives, IReadOnlyList<Dictionary<string, List<float>>> negatives)
    {
        int pf = positives.Sum(p => p.Values.FirstOrDefault()?.Count ?? 0);
        int nf = negatives.Sum(n => n.Values.FirstOrDefault()?.Count ?? 0);
        if (positives.Count == 0 || negatives.Count == 0)
        {
            return new LearnedFingerprint(id, [], 0, pf, nf, negatives.Count == 0
                ? "no footage of its siblings or its parent alone to compare with"
                : "no footage of it");
        }

        IEnumerable<string> signals = positives.SelectMany(p => p.Keys).Intersect(negatives.SelectMany(n => n.Keys), StringComparer.Ordinal).Distinct(StringComparer.Ordinal);
        var candidates = new List<LearnedCondition>();
        foreach (string signal in signals)
        {
            float[] pos = [.. positives.SelectMany(p => p.TryGetValue(signal, out List<float>? v) ? v : [])];
            float[] neg = [.. negatives.SelectMany(n => n.TryGetValue(signal, out List<float>? v) ? v : [])];
            if (pos.Length == 0 || neg.Length == 0)
            {
                continue;
            }

            (double mp, double sp) = MeanStd(pos);
            (double mn, double sn) = MeanStd(neg);
            double separation = Math.Abs(mp - mn) / (sp + sn + 1e-6);
            if (separation < MinSeparation)
            {
                continue;
            }

            // Where the two distributions cross, kept at least a tenth of the gap away from
            // either mean so a side with no spread (a constant signal) is still separated.
            double threshold = sp + sn < 1e-9 ? (mp + mn) / 2 : ((mp * sn) + (mn * sp)) / (sp + sn);
            double lo = Math.Min(mp, mn), hi = Math.Max(mp, mn), margin = (hi - lo) / 10;
            threshold = Math.Clamp(threshold, lo + margin, hi - margin);
            bool above = mp > mn;
            float th = (float)threshold;
            candidates.Add(new LearnedCondition(signal, above, th, separation, Rate(pos, above, th), Rate(neg, above, th)));
        }

        // Keep only conditions nearly as good as the best: a weak one would drag the score down.
        double best = candidates.Count == 0 ? 0 : candidates.Max(c => c.Separation);
        var chosen = candidates
            .Where(c => c.Separation >= best / 2)
            .OrderByDescending(c => c.Separation).ThenBy(c => c.Signal, StringComparer.Ordinal)
            .Take(MaxConditions)
            .ToList();
        if (chosen.Count == 0)
        {
            return new LearnedFingerprint(id, [], 0, pf, nf, $"no signal separates it by {MinSeparation} or more; add a region over something it alone shows");
        }

        double posScore = chosen.Average(c => c.PositiveRate);
        double negScore = chosen.Average(c => c.NegativeRate);
        double minScore = Math.Clamp(Math.Round((posScore + negScore) / 2, 2), 0.5, 0.95);
        return new LearnedFingerprint(id, chosen, minScore, pf, nf, null);
    }

    private static bool OnPath(ProfileCatalog catalog, string leaf, string id) => catalog.PathTo(leaf).Any(p => p.Id == id);

    private static (double Mean, double Std) MeanStd(float[] v)
    {
        double mean = v.Average(x => (double)x);
        double var = v.Sum(x => (x - mean) * (x - mean)) / v.Length;
        return (mean, Math.Sqrt(var));
    }

    private static double Rate(float[] v, bool above, float threshold) =>
        (double)v.Count(x => above ? x > threshold : x < threshold) / v.Length;

    public static string Describe(LearnedCondition c) =>
        string.Create(CultureInfo.InvariantCulture, $"{c.Signal} {(c.Above ? ">" : "<")} {c.Threshold:0.####}  (separation {c.Separation:0.0}, holds on {c.PositiveRate:P0} of its frames, {c.NegativeRate:P0} of others)");
}
