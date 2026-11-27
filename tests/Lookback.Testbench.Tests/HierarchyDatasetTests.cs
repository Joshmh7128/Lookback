using System.Diagnostics;
using Lookback.Contracts.V1;
using Lookback.Media.Ffmpeg;
using Lookback.Testbench.Datasets;

namespace Lookback.Testbench.Tests;

/// <summary>
/// The whole training loop on generated footage: a game with two sub-games
/// that differ only by the colour of a HUD box. Fingerprints are learned from
/// labelled clips, then the dataset run detects each sub-game on its own and
/// scores the clip candidates against labelled clips.
/// </summary>
public sealed class HierarchyDatasetTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lookback-hier", Guid.NewGuid().ToString("N"));

    public HierarchyDatasetTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "profiles"));
        File.WriteAllText(Path.Combine(_dir, "profiles", "boxgame.json"), """
            {
              "id": "boxgame", "displayName": "Box game", "executables": ["Box.exe"],
              "regions": [ { "name": "box", "x": 0.75, "y": 0.0, "w": 0.25, "h": 0.25 } ],
              "events": [ { "name": "flash", "cooldownMs": 1000, "when": [ { "signal": "flash.brighten", "above": 0.08 } ] } ]
            }
            """);
        foreach (string colour in new[] { "red", "blue" })
        {
            File.WriteAllText(Path.Combine(_dir, "profiles", $"boxgame.{colour}.json"), $$"""
                {
                  "id": "boxgame.{{colour}}", "displayName": "{{colour}}", "parent": "boxgame", "level": "subgame",
                  "detect": { "windowSeconds": 2, "holdSeconds": 1 }
                }
                """);
            MakeClip(colour);
        }

        File.WriteAllText(Path.Combine(_dir, "manifest.json"), """
            {
              "game": "boxgame",
              "entries": [
                { "file": "red.mp4", "profile": "boxgame.red", "clips": [ { "start": 3, "end": 11 } ], "labels": [ { "event": "flash", "at": 8 } ] },
                { "file": "blue.mp4", "profile": "boxgame.blue", "clips": [ { "start": 3, "end": 11 } ] }
              ]
            }
            """);
    }

    private void MakeClip(string colour)
    {
        var psi = new ProcessStartInfo(FfmpegLocator.RequireFfmpeg()) { RedirectStandardError = true, UseShellExecute = false };
        foreach (string a in new[]
        {
            "-y", "-loglevel", "error",
            "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=25:duration=16",
            "-vf", $"drawbox=x=240:y=0:w=80:h=45:color={colour}:t=fill,eq=brightness='if(between(t,8,8.2),0.6,0)':eval=frame",
            "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", Path.Combine(_dir, colour + ".mp4"),
        })
        {
            psi.ArgumentList.Add(a);
        }

        using Process p = Process.Start(psi)!;
        string err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, err);
    }

    [Fact]
    public async Task LearnsFingerprintsThenDetectsSubgamesAndScoresCandidates()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string manifest = Path.Combine(_dir, "manifest.json");
        string profiles = Path.Combine(_dir, "profiles");

        IReadOnlyList<LearnedFingerprint> learned = await FingerprintLearner.RunAsync(manifest, Path.Combine(_dir, "fp"), [profiles], ct);
        foreach (LearnedFingerprint f in learned)
        {
            TestContext.Current.SendDiagnosticMessage($"{f.Profile} minScore {f.MinScore}: {string.Join(" | ", f.Conditions.Select(FingerprintLearner.Describe))}");
        }

        LearnedFingerprint red = learned.Single(f => f.Profile == "boxgame.red");
        Assert.Null(red.Problem);
        Assert.Contains(red.Conditions, c => c.Signal.StartsWith("region.box.", StringComparison.Ordinal));
        Assert.All(learned.SelectMany(f => f.Conditions), c => Assert.True(c.PositiveRate > 0.9 && c.NegativeRate < 0.1, FingerprintLearner.Describe(c)));
        Assert.All(red.Conditions, c => Assert.True(c.PositiveRate > 0.9 && c.NegativeRate < 0.1, FingerprintLearner.Describe(c)));
        Assert.True(File.Exists(Path.Combine(_dir, "fp", "learned", "boxgame.blue.json")));

        DatasetResult result = await DatasetRunner.RunAsync(manifest, Path.Combine(_dir, "ds"), 1.0, null, ct, [profiles, Path.Combine(_dir, "fp", "learned")]);
        Assert.All(result.Entries, e => Assert.Equal(e.ExpectedProfile, e.FinalProfile));
        Assert.True(result.DetectionAccuracy > 0.7, $"detection accuracy {result.DetectionAccuracy}");

        CandidateScore c = result.Candidates!;
        Assert.Equal(2, c.TruePositives); // one flash per clip, each inside its labelled clip
        Assert.Equal(0, c.FalseNegatives);
        Assert.Equal(1, result.Overall.Single(s => s.Event == "flash").TruePositives);
    }

    [Fact]
    public void CandidateScoringUsesTimeOverlap()
    {
        Assert.Equal(1.0 / 3, CandidateScoring.IoU((0, 10), (5, 15)), 6);
        Assert.Equal(0, CandidateScoring.IoU((0, 1), (2, 3)));
        CandidateScore s = CandidateScoring.Score([new ClipLabel(0, 10), new ClipLabel(30, 40)], [(1, 9), (2, 10), (100, 110)]);
        Assert.Equal((1, 2, 1), (s.TruePositives, s.FalsePositives, s.FalseNegatives)); // two candidates for one clip: one counts
    }

    [Fact]
    public void ProfileSecondsCountsOnlyTheExpectedProfile()
    {
        ProfileDetected D(string id, double s) => new() { ProfileId = id, AtQpc = (ulong)(s * TestbenchRunner.Frequency) };
        Assert.Equal(6, DatasetRunner.ProfileSeconds([D("g", 0), D("g.red", 4)], "g.red", 10), 6);
        Assert.Equal(3, DatasetRunner.ProfileSeconds([D("g.red", 0), D("g", 3), D("g.blue", 5)], "g.red", 10), 6);
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
