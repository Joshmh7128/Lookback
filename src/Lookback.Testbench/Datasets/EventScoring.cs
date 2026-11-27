namespace Lookback.Testbench.Datasets;

/// <summary>Precision and recall for one event type.</summary>
internal sealed record EventScore(string Event, int TruePositives, int FalsePositives, int FalseNegatives)
{
    public double Precision => TruePositives + FalsePositives == 0 ? double.NaN : (double)TruePositives / (TruePositives + FalsePositives);

    public double Recall => TruePositives + FalseNegatives == 0 ? double.NaN : (double)TruePositives / (TruePositives + FalseNegatives);
}

/// <summary>
/// Scores detected events against labels. A detection matches a label of the
/// same name within the tolerance; each label and each detection is used at
/// most once, closest pairs first, so two detections of one kill count as one
/// hit and one false positive.
/// </summary>
internal static class EventScoring
{
    public static IReadOnlyList<EventScore> Score(IEnumerable<Label> labels, IEnumerable<(string Event, double At)> detections, double toleranceSeconds)
    {
        ILookup<string, double> byLabel = labels.ToLookup(l => l.Event, l => l.At, StringComparer.Ordinal);
        ILookup<string, double> byDetection = detections.ToLookup(d => d.Event, d => d.At, StringComparer.Ordinal);
        var names = byLabel.Select(g => g.Key).Union(byDetection.Select(g => g.Key)).Order(StringComparer.Ordinal);

        var scores = new List<EventScore>();
        foreach (string name in names)
        {
            double[] l = [.. byLabel[name]];
            double[] d = [.. byDetection[name]];
            var pairs = (from i in Enumerable.Range(0, l.Length)
                         from j in Enumerable.Range(0, d.Length)
                         let gap = Math.Abs(l[i] - d[j])
                         where gap <= toleranceSeconds
                         orderby gap
                         select (i, j)).ToList();

            var usedL = new bool[l.Length];
            var usedD = new bool[d.Length];
            int tp = 0;
            foreach ((int i, int j) in pairs)
            {
                if (!usedL[i] && !usedD[j])
                {
                    usedL[i] = usedD[j] = true;
                    tp++;
                }
            }

            scores.Add(new EventScore(name, tp, d.Length - tp, l.Length - tp));
        }

        return scores;
    }

    /// <summary>Adds up scores for the same event across entries.</summary>
    public static IReadOnlyList<EventScore> Sum(IEnumerable<EventScore> scores) =>
        [.. scores.GroupBy(s => s.Event, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new EventScore(g.Key, g.Sum(s => s.TruePositives), g.Sum(s => s.FalsePositives), g.Sum(s => s.FalseNegatives)))];
}
