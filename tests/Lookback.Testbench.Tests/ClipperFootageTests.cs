using System.Diagnostics;
using Lookback.Contracts.V1;
using Lookback.Media.Ffmpeg;
using Lookback.Testbench.Report;

namespace Lookback.Testbench.Tests;

/// <summary>
/// The clipper on generated footage (ffmpeg's lavfi sources): a moving test
/// pattern with a quiet tone, plus flashes, white-noise bursts, and black,
/// frozen or silent stretches where each test needs them.
/// </summary>
public sealed class ClipperFootageTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lookback-clipper", Guid.NewGuid().ToString("N"));

    public ClipperFootageTests() => Directory.CreateDirectory(_dir);

    [Fact]
    public async Task AFlashWithALoudBurstIsOneCandidateWithTheRightWindow()
    {
        string clip = Make("burst", 20, moments: [12]);
        AnalyzeResult r = await AnalyzeAsync(clip, profile: null);

        ClipCandidate c = Assert.Single(r.Candidates);
        double peak = Sec(c.PeakQpc);
        Assert.InRange(peak, 12, 12.4);
        Assert.Equal(peak - 6, Sec(c.StartQpc), 2); // default pre-roll
        Assert.Equal(peak + 3, Sec(c.EndQpc), 2);   // default post-roll
        Assert.StartsWith("excitement ×1", Assert.Single(c.Reasons), StringComparison.Ordinal);
        Assert.True(r.Qa[c.CandidateId].Passed, Describe(r.Qa[c.CandidateId]));

        // The report shows the candidate window, its QA and the curve.
        TimelineData t = await TimelineBuilder.BuildAsync(r.SessionBasePath!, null, CancellationToken);
        TimelineData.CandidateMark mark = Assert.Single(t.Candidates);
        Assert.True(mark.Qa!.Passed);
        Assert.Equal(["length", "black", "frozen", "silence"], mark.Qa.Checks.Select(k => k.Name));
        TimelineData.Lane curve = t.Lanes.Single(l => l.Name == "clipper.excitement");
        Assert.Equal("clipper", curve.Family);
        Assert.Equal(0.1, curve.Step, 3);
        Assert.True(curve.Values.Max() > 2);
        string html = await File.ReadAllTextAsync(r.ReportPath!, CancellationToken);
        Assert.Contains("\"qa\":{\"passed\":true", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OverlappingPeaksMergeIntoOneCandidate()
    {
        string clip = Make("two", 24, moments: [10, 14]); // 4 s apart: two peaks, overlapping windows
        AnalyzeResult r = await AnalyzeAsync(clip, profile: null);

        ClipCandidate c = Assert.Single(r.Candidates);
        Assert.StartsWith("excitement ×2", c.Reasons[0], StringComparison.Ordinal);
        Assert.InRange(Sec(c.StartQpc), 3.9, 4.5);
        Assert.InRange(Sec(c.EndQpc), 17, 17.5);
    }

    [Fact]
    public async Task ABlackStretchFailsQa()
    {
        string clip = Make("black", 20, moments: [14], video: "drawbox=c=black:t=fill:enable='gte(t,10)'");
        AnalyzeResult r = await AnalyzeAsync(clip, profile: null);

        QaReport qa = r.Qa[r.Candidates.Single(c => Sec(c.PeakQpc) is > 13.5 and < 14.6).CandidateId];
        Assert.False(qa.Passed);
        Assert.False(qa.Checks.Single(k => k.Name == "black").Passed, Describe(qa));
        Assert.True(qa.Checks.Single(k => k.Name == "silence").Passed);
    }

    [Fact]
    public async Task AFrozenStretchFailsQaButIsNotBlack()
    {
        // From 10 s the picture is one still grey frame: nothing moves, nothing is black.
        string clip = Make("frozen", 20, moments: [14], video: "drawbox=c=gray:t=fill:enable='gte(t,10)'");
        AnalyzeResult r = await AnalyzeAsync(clip, profile: null);

        QaReport qa = r.Qa[r.Candidates.Single(c => Sec(c.PeakQpc) is > 13.5 and < 14.6).CandidateId];
        Assert.False(qa.Checks.Single(k => k.Name == "frozen").Passed, Describe(qa));
        Assert.True(qa.Checks.Single(k => k.Name == "black").Passed, Describe(qa));
    }

    [Fact]
    public async Task SilentAudioFailsQa()
    {
        string clip = Make("silent", 20, moments: [12], silent: true); // flashes only, over digital silence
        AnalyzeResult r = await AnalyzeAsync(clip, profile: null);

        QaReport qa = r.Qa[Assert.Single(r.Candidates).CandidateId];
        Assert.False(qa.Checks.Single(k => k.Name == "silence").Passed, Describe(qa));
        Assert.True(qa.Checks.Single(k => k.Name == "black").Passed);
    }

    [Fact]
    public async Task ProfileEventsAndCurvePeaksDedupe()
    {
        string clip = Make("generic", 20, moments: [12]);
        AnalyzeResult r = await AnalyzeAsync(clip, profile: "generic");

        Assert.Contains(r.Events, e => e.Name == "impact");
        ClipCandidate c = Assert.Single(r.Candidates); // one candidate, not one per source
        Assert.Contains(c.Reasons, x => x.StartsWith("impact ×1", StringComparison.Ordinal));
        Assert.Contains(c.Reasons, x => x.StartsWith("excitement ×1", StringComparison.Ordinal));
        Assert.Equal("generic", c.GameProfile);
        Assert.InRange(Sec(c.StartQpc), 3.9, 6.5); // the impact's 8 s pre-roll reaches further back than the curve's 6 s
        Assert.True(r.Qa[c.CandidateId].Passed, Describe(r.Qa[c.CandidateId]));
    }

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    private static double Sec(ulong qpc) => qpc / (double)TestbenchRunner.Frequency;

    private static string Describe(QaReport q) => string.Join("; ", q.Checks.Select(k => $"{k.Name} {(k.Passed ? "ok" : "FAIL")} {k.Detail}"));

    private Task<AnalyzeResult> AnalyzeAsync(string clip, string? profile) => TestbenchRunner.AnalyzeAsync(
        new AnalyzeOptions { File = clip, OutputDirectory = Path.Combine(_dir, "out"), SessionId = Path.GetFileNameWithoutExtension(clip), Profile = profile },
        null,
        CancellationToken);

    /// <summary>
    /// A 320x180, 30 fps test pattern with a quiet 440 Hz tone. At each moment
    /// a 0.2 s flash and (unless <paramref name="silent"/>) a 0.3 s white-noise
    /// burst. <paramref name="video"/> is an extra filter applied afterwards.
    /// </summary>
    private string Make(string name, int seconds, double[] moments, string? video = null, bool silent = false)
    {
        string path = Path.Combine(_dir, name + ".mp4");
        string at(double ms) => string.Join("+", moments.Select(m => $"between(t,{m},{m + ms})"));
        string flash = $"eq=brightness='if({at(0.2)},0.6,0)':eval=frame" + (video is null ? "" : "," + video);
        string[] audio = silent
            ? ["-f", "lavfi", "-i", $"anullsrc=r=48000:cl=mono:d={seconds}"]
            : ["-f", "lavfi", "-i", $"sine=frequency=440:sample_rate=48000:duration={seconds},volume=0.05",
               "-f", "lavfi", "-i", $"anoisesrc=d={seconds}:c=white:a=0.8:r=48000"];
        string graph = silent
            ? $"[0:v]{flash}[v]"
            : $"[0:v]{flash}[v];[2:a]volume='if({at(0.3)},1,0)':eval=frame[n];[1:a][n]amix=inputs=2:normalize=0[a]";

        var psi = new ProcessStartInfo(FfmpegLocator.RequireFfmpeg()) { RedirectStandardError = true, UseShellExecute = false };
        foreach (string a in (string[])[
            "-y", "-loglevel", "error",
            "-f", "lavfi", "-i", $"testsrc2=size=320x180:rate=30:duration={seconds}",
            .. audio,
            "-filter_complex", graph,
            "-map", "[v]", "-map", silent ? "1:a" : "[a]",
            "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-c:a", "aac", "-t", $"{seconds}", path])
        {
            psi.ArgumentList.Add(a);
        }

        using Process p = Process.Start(psi)!;
        string err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, err);
        return path;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
