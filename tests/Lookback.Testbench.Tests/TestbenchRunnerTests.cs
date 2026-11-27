using Lookback.Contracts.V1;
using Lookback.Stream.SessionLog;
using Lookback.Testbench.Report;

namespace Lookback.Testbench.Tests;

public class TestbenchRunnerTests(FootageFixture fixture) : IClassFixture<FootageFixture>
{
    private string NewOut() => Path.Combine(fixture.Directory, "out-" + Guid.NewGuid().ToString("N")[..8]);

    [Fact]
    public async Task OwnedFootageProducesLogAndReport()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string outDir = NewOut();
        AnalyzeResult r = await TestbenchRunner.AnalyzeAsync(new AnalyzeOptions { File = fixture.File, OutputDirectory = outDir, SessionId = "owned" }, null, ct);

        Assert.InRange(r.Analysis.VideoFrames, 89, 91);   // 3 s at 30 fps
        Assert.InRange(r.Analysis.AudioHops, 295, 310);
        Assert.True(File.Exists(r.SessionBasePath + SessionLogFormat.LogExtension));
        Assert.True(File.Exists(r.ReportPath));

        // Everything published reached the log: both schemas (and the clipper's curve), every frame and hop, and the end marker.
        await using SessionLogReader reader = await SessionLogReader.OpenAsync(r.SessionBasePath!, ct);
        List<Envelope> all = await reader.ReadAsync(cancellationToken: ct).ToListAsync(ct);
        Assert.Equal(3, all.Count(e => e.PayloadCase == Envelope.PayloadOneofCase.SignalSchema));
        Assert.Equal(r.Analysis.VideoFrames + r.Analysis.AudioHops, all.Count(e => e.PayloadCase == Envelope.PayloadOneofCase.SignalFrame && e.Topic != "signals.clipper"));
        Assert.Single(all, e => e.PayloadCase == Envelope.PayloadOneofCase.SessionEnded);
    }

    [Fact]
    public async Task FlashInTheFootageShowsUpInTheTimeline()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        AnalyzeResult r = await TestbenchRunner.AnalyzeAsync(new AnalyzeOptions { File = fixture.File, OutputDirectory = NewOut(), SessionId = "flash" }, null, ct);
        TimelineData t = await TimelineBuilder.BuildAsync(r.SessionBasePath!, null, ct);

        TimelineData.Lane flash = t.Lanes.Single(l => l.Name == "flash.brighten");
        int peak = flash.Values.IndexOf(flash.Values.Max());
        Assert.InRange(peak * flash.Step, 1.95, 2.25); // the fixture flashes at 2.0 to 2.2 s
        Assert.NotEmpty(t.Mel);
    }

    [Fact]
    public async Task EphemeralWritesNothing()
    {
        string outDir = NewOut();
        AnalyzeResult r = await TestbenchRunner.AnalyzeAsync(
            new AnalyzeOptions { File = fixture.File, OutputDirectory = outDir, Retention = Retention.Ephemeral }, null, TestContext.Current.CancellationToken);

        Assert.True(r.Analysis.VideoFrames > 0); // it still analysed
        Assert.Null(r.SessionBasePath);
        Assert.Null(r.ReportPath);
        Assert.False(Directory.Exists(outDir));
    }

    [Fact]
    public async Task PermissionedWithoutReferenceIsRefusedBeforeAnyWork()
    {
        string outDir = NewOut();
        await Assert.ThrowsAsync<RetentionViolationException>(() => TestbenchRunner.AnalyzeAsync(
            new AnalyzeOptions { File = fixture.File, OutputDirectory = outDir, Retention = Retention.Permissioned }, null, TestContext.Current.CancellationToken));
        Assert.False(Directory.Exists(outDir));
    }

    [Fact]
    public void RelativeHrefIsEscaped()
    {
        string root = Path.GetTempPath();
        string href = TestbenchRunner.RelativeHref(Path.Combine(root, "out"), Path.Combine(root, "clips", "my clip#1.mp4"));
        Assert.Equal("../clips/my%20clip%231.mp4", href);
    }

    [Fact]
    public void SessionIdIsFileNameSafe()
    {
        string id = TestbenchRunner.MakeSessionId("/x/My Clip: Ace! (2026).mp4");
        Assert.Matches("^[0-9]{8}-[0-9]{6}-[a-z0-9-]+$", id);
    }
}
