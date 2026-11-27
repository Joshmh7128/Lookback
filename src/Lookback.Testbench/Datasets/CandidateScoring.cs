namespace Lookback.Testbench.Datasets;

/// <summary>How well clip candidates line up with the clips a person marked.</summary>
internal sealed record CandidateScore(int TruePositives, int FalsePositives, int FalseNegatives, double MeanIoU)
{
    public double Precision => TruePositives + FalsePositives == 0 ? double.NaN : (double)TruePositives / (TruePositives + FalsePositives);

    public double Recall => TruePositives + FalseNegatives == 0 ? double.NaN : (double)TruePositives / (TruePositives + FalseNegatives);
}

/// <summary>
/// Matches candidates to labelled clips by temporal overlap: intersection
/// over union of the two time ranges. Pairs at or above the threshold match
/// one-to-one, best overlap first.
/// </summary>
internal static class CandidateScoring
{
    public const double DefaultIoU = 0.3;

    public static double IoU((double Start, double End) a, (double Start, double End) b)
    {
        double inter = Math.Max(0, Math.Min(a.End, b.End) - Math.Max(a.Start, b.Start));
        double union = Math.Max(a.End, b.End) - Math.Min(a.Start, b.Start);
        return union <= 0 ? 0 : inter / union;
    }

    public static CandidateScore Score(IReadOnlyList<ClipLabel> clips, IReadOnlyList<(double Start, double End)> candidates, double threshold = DefaultIoU)
    {
        ArgumentNullException.ThrowIfNull(clips);
        ArgumentNullException.ThrowIfNull(candidates);
        var pairs = (from i in Enumerable.Range(0, clips.Count)
                     from j in Enumerable.Range(0, candidates.Count)
                     let iou = IoU((clips[i].Start, clips[i].End), candidates[j])
                     where iou >= threshold
                     orderby iou descending
                     select (i, j, iou)).ToList();
        var usedC = new bool[clips.Count];
        var usedK = new bool[candidates.Count];
        int tp = 0;
        double sum = 0;
        foreach ((int i, int j, double iou) in pairs)
        {
            if (!usedC[i] && !usedK[j])
            {
                usedC[i] = usedK[j] = true;
                tp++;
                sum += iou;
            }
        }

        return new CandidateScore(tp, candidates.Count - tp, clips.Count - tp, tp == 0 ? 0 : sum / tp);
    }

    public static CandidateScore Sum(IEnumerable<CandidateScore> scores)
    {
        int tp = 0, fp = 0, fn = 0;
        double iou = 0;
        foreach (CandidateScore s in scores)
        {
            tp += s.TruePositives;
            fp += s.FalsePositives;
            fn += s.FalseNegatives;
            iou += s.MeanIoU * s.TruePositives;
        }

        return new CandidateScore(tp, fp, fn, tp == 0 ? 0 : iou / tp);
    }
}
