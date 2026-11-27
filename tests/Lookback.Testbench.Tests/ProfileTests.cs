using System.Buffers.Binary;
using System.IO.Compression;
using Lookback.Contracts.V1;
using Lookback.Core.Time;
using Lookback.Testbench.Datasets;
using Lookback.Testbench.Report;

namespace Lookback.Testbench.Tests;

public class ProfileTests(FootageFixture fixture) : IClassFixture<FootageFixture>
{
    [Fact]
    public async Task GenericProfileFindsTheFlashAsAnImpact()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        AnalyzeResult r = await TestbenchRunner.AnalyzeAsync(
            new AnalyzeOptions { File = fixture.File, OutputDirectory = Path.Combine(fixture.Directory, "generic"), SessionId = "generic", Profile = "generic" }, null, ct);

        GameEvent impact = Assert.Single(r.Events, e => e.Name == "impact");
        Assert.InRange(QpcTime.ToTimeSpan((long)impact.AtQpc, TestbenchRunner.Frequency).TotalSeconds, 1.95, 2.1);
        Assert.Contains(impact.Evidence, x => x.StartsWith("flash.brighten", StringComparison.Ordinal));

        // The events and the rule margins reach the report.
        TimelineData t = await TimelineBuilder.BuildAsync(r.SessionBasePath!, null, ct);
        Assert.Equal("generic", t.Profile);
        Assert.Contains(t.Events, e => e.Name == "impact");
        Assert.Contains(t.Lanes, l => l.Family == "rules" && l.Name == "rule.impact.0");
    }

    [Fact]
    public async Task OverwatchProfileAddsRegionSignalsAndCalibrationOverlay()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        AnalyzeResult r = await TestbenchRunner.AnalyzeAsync(
            new AnalyzeOptions { File = fixture.File, OutputDirectory = Path.Combine(fixture.Directory, "ow"), SessionId = "ow", Profile = "overwatch" }, null, ct);

        Assert.Empty(r.Warnings); // every signal the profile uses exists
        TimelineData t = await TimelineBuilder.BuildAsync(r.SessionBasePath!, null, ct);
        Assert.Contains(t.Lanes, l => l.Name == "region.kill_feed.activity");
        string html = await File.ReadAllTextAsync(r.ReportPath!, ct);
        Assert.Contains("\"kill_feed\"", html, StringComparison.Ordinal); // region boxes for the calibration view
    }

    [Fact]
    public void PngHasValidSignatureChunksAndPixels()
    {
        byte[] rgba = [255, 0, 0, 255, 0, 255, 0, 128, 0, 0, 255, 0, 9, 9, 9, 9];
        byte[] png = PngWriter.EncodeRgba(2, 2, rgba);

        Assert.Equal([137, 80, 78, 71, 13, 10, 26, 10], png[..8]);
        var idat = new MemoryStream();
        int pos = 8;
        while (pos < png.Length)
        {
            int len = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(pos));
            string type = System.Text.Encoding.ASCII.GetString(png, pos + 4, 4);
            uint crc = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(pos + 8 + len));
            Assert.Equal(Crc32(png.AsSpan(pos + 4, 4 + len)), crc);
            if (type == "IHDR")
            {
                Assert.Equal(2, BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(pos + 8)));
            }
            else if (type == "IDAT")
            {
                idat.Write(png, pos + 8, len);
            }

            pos += 12 + len;
        }

        idat.Position = 0;
        using var z = new ZLibStream(idat, CompressionMode.Decompress);
        var raw = new MemoryStream();
        z.CopyTo(raw);
        // Each row: filter byte 0, then 2 RGBA pixels.
        Assert.Equal([0, .. rgba[..8], 0, .. rgba[8..]], raw.ToArray());
    }

    [Fact]
    public void ScoringMatchesOneToOneWithinTolerance()
    {
        Label[] labels = [new("kill", 10), new("kill", 20), new("ult", 5)];
        (string, double)[] detections = [("kill", 10.3), ("kill", 10.4), ("kill", 25), ("ult", 5.9)];

        IReadOnlyList<EventScore> s = EventScoring.Score(labels, detections, toleranceSeconds: 1);
        EventScore kill = s.Single(x => x.Event == "kill");
        Assert.Equal((1, 2, 1), (kill.TruePositives, kill.FalsePositives, kill.FalseNegatives)); // two detections of one kill: one hit
        Assert.Equal(1.0 / 3, kill.Precision, 6);
        Assert.Equal(0.5, kill.Recall, 6);
        EventScore ult = s.Single(x => x.Event == "ult");
        Assert.Equal((1, 0, 0), (ult.TruePositives, ult.FalsePositives, ult.FalseNegatives));

        IReadOnlyList<EventScore> sum = EventScoring.Sum([.. s, new EventScore("kill", 2, 0, 0)]);
        Assert.Equal(3, sum.Single(x => x.Event == "kill").TruePositives);
    }

    [Fact]
    public void ManifestParsesAndRejectsEphemeral()
    {
        string dir = Path.Combine(fixture.Directory, "manifest");
        Directory.CreateDirectory(dir);
        string good = Path.Combine(dir, "good.json");
        File.WriteAllText(good, """
            {
              // comments allowed
              "game": "overwatch",
              "entries": [
                { "file": "a.mp4", "source": "own recording", "labels": [ { "event": "elimination", "at": 12.5 } ] },
                { "file": "b.mp4", "retention": "permissioned", "permissionRef": "email 2026-11-20" },
              ]
            }
            """);
        DatasetManifest m = DatasetManifest.Load(good);
        Assert.Equal("overwatch", m.Game);
        Assert.Equal(DatasetRetention.Owned, m.Entries[0].Retention);
        Assert.Equal(Retention.Permissioned, m.Entries[1].Retention.ToRetention());
        Assert.Equal(new Label("elimination", 12.5), m.Entries[0].Labels[0]);

        string bad = Path.Combine(dir, "bad.json");
        File.WriteAllText(bad, """{ "game": "overwatch", "entries": [ { "file": "c.mp4", "retention": "ephemeral" } ] }""");
        Assert.Throws<InvalidDataException>(() => DatasetManifest.Load(bad));
    }

    [Fact]
    public async Task DatasetRunScoresAgainstLabels()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string dir = Path.Combine(fixture.Directory, "dataset");
        Directory.CreateDirectory(dir);
        string manifest = Path.Combine(dir, "manifest.json");
        string rel = Path.GetRelativePath(dir, fixture.File).Replace('\\', '/');
        File.WriteAllText(manifest, $$"""{ "game": "generic", "entries": [ { "file": "{{rel}}", "labels": [ { "event": "impact", "at": 2.0 } ] } ] }""");

        DatasetResult result = await DatasetRunner.RunAsync(manifest, Path.Combine(dir, "out"), 0.5, null, ct);
        var (entries, overall) = (result.Entries, result.Overall);
        Assert.Single(entries);
        EventScore impact = overall.Single(s => s.Event == "impact");
        Assert.Equal(1, impact.TruePositives);
        Assert.Equal(0, impact.FalseNegatives);
        Assert.True(File.Exists(Path.Combine(dir, "out", "scores.json")));
    }

    /// <summary>Bitwise CRC-32 (reflected, 0xEDB88320), independent of the writer's table.</summary>
    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint c = 0xFFFFFFFF;
        foreach (byte b in data)
        {
            c ^= b;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            }
        }

        return ~c;
    }
}
