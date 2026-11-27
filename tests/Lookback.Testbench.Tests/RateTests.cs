using System.Diagnostics;
using Lookback.Contracts.V1;
using Lookback.Media.Ffmpeg;
using Lookback.Scoring.Ratings;
using Lookback.Testbench.Datasets;

namespace Lookback.Testbench.Tests;

/// <summary>The terminal rating path: the question loop on its own, then a whole pass over generated footage.</summary>
public sealed class RateTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lookback-rate", Guid.NewGuid().ToString("N"));

    public RateTests() => Directory.CreateDirectory(_dir);

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

    private sealed class FakePlayer : IClipPlayer
    {
        public List<(TimeSpan Start, TimeSpan Length)> Played { get; } = [];

        public string? Play(string file, TimeSpan start, TimeSpan length)
        {
            Played.Add((start, length));
            return null;
        }
    }

    private static RateItem Item(string id, Retention retention = Retention.Owned, SourceKind kind = SourceKind.File) => new(
        "clip.mp4",
        "boxgame-clip",
        new SessionStarted { SourceKind = kind, Retention = retention, QpcFrequency = TestbenchRunner.Frequency, GameProfile = "boxgame" },
        new ClipCandidate
        {
            CandidateId = id,
            StartQpc = (ulong)(2 * TestbenchRunner.Frequency),
            EndQpc = (ulong)(10 * TestbenchRunner.Frequency),
            PeakQpc = (ulong)(6 * TestbenchRunner.Frequency),
            HeuristicScore = 0.5f,
            GameProfile = "boxgame",
        });

    [Fact]
    public void Stars_skip_play_clear_and_quit()
    {
        var store = new RatingStore(_dir);
        var player = new FakePlayer();
        RateItem[] items = [Item("c1"), Item("c2"), Item("c3"), Item("c4"), Item("c5")];
        using var input = new StringReader("4\n\nx\np\n1\n0\nq\n");
        using var output = new StringWriter();

        RateSummary s = RateRunner.Prompt(items, store, input, output, player, again: false, autoPlay: false);

        Assert.True(s.Quit);
        Assert.Equal((2, 1, 1), (s.Rated, s.Skipped, s.Cleared));
        Assert.Single(player.Played);
        Assert.Equal((TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(8)), player.Played[0]);
        IReadOnlyList<RatingRecord> rows = store.Read("boxgame").Records;
        Assert.Equal(["c1", "c3", "c4"], rows.Select(r => r.CandidateId));
        Assert.All(rows, r => Assert.Equal("testbench", r.Source));
        Assert.Contains(RateRunner.Help, output.ToString(), StringComparison.Ordinal); // "x" was not understood
    }

    [Fact]
    public void Already_rated_candidates_are_skipped_unless_again()
    {
        var store = new RatingStore(_dir);
        RateItem[] items = [Item("c1"), Item("c2")];
        RateRunner.Prompt(items, store, new StringReader("5\n"), TextWriter.Null, null, again: false, autoPlay: false);

        RateSummary second = RateRunner.Prompt(items, store, new StringReader("2\n"), TextWriter.Null, null, again: false, autoPlay: false);
        Assert.Equal((1, 1), (second.AlreadyRated, second.Rated));

        RateSummary again = RateRunner.Prompt(items, store, new StringReader("3\n3\n"), TextWriter.Null, null, again: true, autoPlay: false);
        Assert.Equal((0, 2), (again.AlreadyRated, again.Rated));
        Assert.All(RatingStore.Current(store.Read("boxgame").Records), r => Assert.Equal(3, r.Stars));
    }

    [Fact]
    public void Ephemeral_sessions_are_refused_and_nothing_is_written()
    {
        var store = new RatingStore(_dir);
        using var output = new StringWriter();

        RateSummary s = RateRunner.Prompt([Item("c1", Retention.Ephemeral, SourceKind.OfficialPlayer)], store, new StringReader("5\n"), output, null, again: false, autoPlay: false);

        Assert.Equal((0, 1), (s.Rated, s.Refused));
        Assert.Contains("refused", output.ToString(), StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_dir));
    }

    [Fact]
    public async Task Rates_candidates_from_a_manifest()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string profiles = Path.Combine(_dir, "profiles");
        string game = Path.Combine(_dir, "datasets", "boxgame");
        Directory.CreateDirectory(profiles);
        Directory.CreateDirectory(game);
        File.WriteAllText(Path.Combine(profiles, "boxgame.json"), """
            {
              "id": "boxgame", "displayName": "Box game", "executables": ["Box.exe"],
              "events": [ { "name": "flash", "cooldownMs": 1000, "when": [ { "signal": "flash.brighten", "above": 0.08 } ] } ]
            }
            """);
        File.WriteAllText(Path.Combine(game, "manifest.json"), """
            { "game": "boxgame", "entries": [ { "file": "media/flash.mp4", "source": "generated" } ] }
            """);
        MakeFlashClip(Path.Combine(game, "media", "flash.mp4"));

        var options = new RateOptions
        {
            Manifest = Path.Combine(game, "manifest.json"),
            OutputDirectory = Path.Combine(_dir, "out"),
            ExtraProfiles = [profiles],
        };
        RateSummary s = await RateRunner.RunAsync(options, new StringReader("5\n"), TextWriter.Null, null, ct);

        Assert.Equal(1, s.Candidates);
        Assert.Equal(1, s.Rated);
        Assert.Equal(Path.Combine(_dir, "datasets"), s.DatasetRoot);
        RatingRecord row = Assert.Single(new RatingStore(s.DatasetRoot).Read("boxgame").Records);
        Assert.Equal("owned", row.Retention);
        Assert.Equal(1.0, row.Label);
        Assert.Equal(Lookback.Scoring.Features.ClipFeatureSchema.Id, row.FeatureSchema);

        // Same footage, same session and candidate ids: a second pass has nothing left to ask.
        RateSummary again = await RateRunner.RunAsync(options, new StringReader(""), TextWriter.Null, null, ct);
        Assert.Equal((1, 0), (again.AlreadyRated, again.Rated));
    }

    private static void MakeFlashClip(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var psi = new ProcessStartInfo(FfmpegLocator.RequireFfmpeg()) { RedirectStandardError = true, UseShellExecute = false };
        foreach (string a in new[]
        {
            "-y", "-loglevel", "error",
            "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=25:duration=8",
            "-vf", "eq=brightness='if(between(t,4,4.2),0.6,0)':eval=frame",
            "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", path,
        })
        {
            psi.ArgumentList.Add(a);
        }

        using Process p = Process.Start(psi)!;
        string err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, err);
    }
}
