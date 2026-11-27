using System.Globalization;
using Lookback.Contracts.V1;
using Lookback.Core.Time;

namespace Lookback.Clipper.Qa;

/// <summary>
/// QA gates v1. Every check reads signals that already exist; nothing is
/// decoded again. A candidate passes when every check passes.
/// <list type="table">
/// <item><term>length</term><description>between the minimum and maximum length</description></item>
/// <item><term>black</term><description>less than half the frames black (<c>luma.black</c>)</description></item>
/// <item><term>frozen</term><description>less than half the frames frozen (<c>motion.frozen_s</c> over 0.5 s)</description></item>
/// <item><term>silence</term><description>audio not silent throughout (<c>audio.silent</c>)</description></item>
/// </list>
/// </summary>
internal static class QaGates
{
    public const string Length = "length";
    public const string Black = "black";
    public const string Frozen = "frozen";
    public const string Silence = "silence";

    /// <summary>Checks the window [start, end]. In <c>video</c> flag A is black and flag B frozen; in <c>audio</c> flag A is silent.</summary>
    public static QaReport Evaluate(string candidateId, long start, long end, FlagHistory video, FlagHistory audio, QaOptions o, long frequency)
    {
        var report = new QaReport { CandidateId = candidateId };
        double seconds = QpcTime.ToTimeSpan(end - start, frequency).TotalSeconds;
        bool lengthOk = seconds >= o.MinLengthMs / 1000.0 && seconds <= o.MaxLengthMs / 1000.0;
        report.Checks.Add(Check(Length, lengthOk, seconds, $"{seconds:0.0} s (allowed {o.MinLengthMs / 1000.0:0.#}-{o.MaxLengthMs / 1000.0:0.#} s)"));

        (long frames, long black, long frozen) = video.Count(start, end);
        if (frames == 0)
        {
            report.Checks.Add(Check(Black, false, 1, $"no video frames in the window"));
            report.Checks.Add(Check(Frozen, false, 1, $"no video frames in the window"));
        }
        else
        {
            double b = black / (double)frames, f = frozen / (double)frames;
            report.Checks.Add(Check(Black, b < o.MaxBlackFraction, b, $"{b:P0} of {frames} frames black (fails at {o.MaxBlackFraction:P0})"));
            report.Checks.Add(Check(Frozen, f < o.MaxFrozenFraction, f, $"{f:P0} of {frames} frames frozen (fails at {o.MaxFrozenFraction:P0})"));
        }

        (long hops, long silent, _) = audio.Count(start, end);
        if (hops == 0)
        {
            report.Checks.Add(Check(Silence, false, 1, $"no audio in the window"));
        }
        else
        {
            double s = silent / (double)hops;
            report.Checks.Add(Check(Silence, s < o.MaxSilentFraction, s, $"{s:P0} of {hops} audio hops silent (fails at {o.MaxSilentFraction:P0})"));
        }

        report.Passed = report.Checks.All(c => c.Passed);
        return report;
    }

    private static QaCheck Check(string name, bool passed, double value, FormattableString detail) =>
        new() { Name = name, Passed = passed, Value = (float)value, Detail = detail.ToString(CultureInfo.InvariantCulture) };
}
