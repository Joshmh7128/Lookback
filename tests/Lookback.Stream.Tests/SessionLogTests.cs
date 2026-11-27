using Lookback.Contracts.V1;
using Lookback.Core.Bus;
using Lookback.Stream.SessionLog;

namespace Lookback.Stream.Tests;

public class SessionLogTests
{
    [Fact]
    public async Task EphemeralSessionsNeverTouchDisk()
    {
        string dir = TestEvents.TempDir();
        Envelope start = TestEvents.Start(retention: Retention.Ephemeral, kind: SourceKind.OfficialPlayer);

        await Assert.ThrowsAsync<RetentionViolationException>(() => SessionLogWriter.CreateAsync(dir, start, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Empty(Directory.EnumerateFileSystemEntries(dir));
    }

    [Fact]
    public async Task OfficialPlayerCannotBeMarkedOwned()
    {
        string dir = TestEvents.TempDir();
        Envelope start = TestEvents.Start(retention: Retention.Owned, kind: SourceKind.OfficialPlayer);
        await Assert.ThrowsAsync<RetentionViolationException>(() => SessionLogWriter.CreateAsync(dir, start, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Empty(Directory.EnumerateFileSystemEntries(dir));
    }

    [Fact]
    public async Task PermissionedFootageWithoutReferenceIsRefused()
    {
        string dir = TestEvents.TempDir();
        Envelope start = TestEvents.Start(retention: Retention.Permissioned);
        await Assert.ThrowsAsync<RetentionViolationException>(() => SessionLogWriter.CreateAsync(dir, start, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task WritesReadsAndSeeksWithOutOfOrderRecords()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string dir = TestEvents.TempDir();

        // Frequency 1000 and a 100 ms interval: an index entry roughly every 100 ticks.
        await using (SessionLogWriter writer = await SessionLogWriter.CreateAsync(dir, TestEvents.Start(), TimeSpan.FromMilliseconds(100), ct))
        {
            for (ulong t = 1; t <= 1000; t++)
            {
                // Swap neighbours to mimic two producers publishing slightly out of order.
                ulong qpc = t % 2 == 0 ? t - 1 : t + 1;
                await writer.AppendAsync(TestEvents.Signal(qpc), ct);
            }
        }

        await using SessionLogReader reader = await SessionLogReader.OpenAsync(Path.Combine(dir, "s1"), ct);
        Assert.Equal(Retention.Owned, reader.Header.SessionStarted.Retention);

        List<Envelope> all = await reader.ReadAsync(cancellationToken: ct).ToListAsync(ct);
        Assert.Equal(1001, all.Count); // header + records

        foreach (ulong from in new ulong[] { 1, 150, 151, 500, 999, 1000, 1001 })
        {
            List<ulong> got = await reader.ReadAsync(from, ct).Select(e => e.Qpc).ToListAsync(ct);
            List<ulong> expected = all.Where(e => e.Qpc >= from).Select(e => e.Qpc).ToList();
            Assert.Equal(expected, got);
        }

        // Seeking really skips work: the start offset for t=500 is past the start of the file.
        Assert.True(reader.FindStartOffset(500) > reader.FindStartOffset(1));
    }

    [Fact]
    public async Task ToleratesTornTail()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string dir = TestEvents.TempDir();
        await using (SessionLogWriter writer = await SessionLogWriter.CreateAsync(dir, TestEvents.Start(), cancellationToken: ct))
        {
            for (ulong t = 1; t <= 10; t++)
            {
                await writer.AppendAsync(TestEvents.Signal(t, values: [1, 2, 3, 4]), ct);
            }
        }

        string log = Path.Combine(dir, "s1" + SessionLogFormat.LogExtension);
        await using (var fs = new FileStream(log, FileMode.Open))
        {
            fs.SetLength(fs.Length - 3); // crash mid-record
        }

        await using SessionLogReader reader = await SessionLogReader.OpenAsync(Path.Combine(dir, "s1"), ct);
        Assert.Equal(10, await reader.ReadAsync(cancellationToken: ct).CountAsync(ct)); // header + 9 complete
    }

    [Fact]
    public async Task RecorderCapturesOnlyItsSessionFromTheBus()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string dir = TestEvents.TempDir();
        using var bus = new InProcessEventBus();

        SessionLogWriter writer = await SessionLogWriter.CreateAsync(dir, TestEvents.Start("live"), cancellationToken: ct);
        await using (var recorder = new SessionLogRecorder(bus, writer, "live"))
        {
            for (ulong t = 1; t <= 50; t++)
            {
                bus.Publish(TestEvents.Signal(t, session: "live"));
                bus.Publish(TestEvents.Signal(t, session: "other"));
            }
        }

        await using SessionLogReader reader = await SessionLogReader.OpenAsync(Path.Combine(dir, "live"), ct);
        List<Envelope> records = await reader.ReadAsync(cancellationToken: ct).ToListAsync(ct);
        Assert.Equal(51, records.Count);
        Assert.All(records, e => Assert.Equal("live", e.SessionId));
    }
}
