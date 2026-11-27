using System.Buffers.Binary;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Lookback.Contracts;
using Lookback.Contracts.V1;
using Lookback.Core.Bus;
using Lookback.Stream.WebSockets;

namespace Lookback.Calibration.Tests;

public sealed class IngestTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("lookback-ingest-").FullName;

    public IngestTests()
    {
        foreach (string f in Directory.EnumerateFiles(Repo.Profiles("cs2")))
        {
            File.Copy(f, Path.Combine(_dir, Path.GetFileName(f)));
        }
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static byte[] Video(long micros, byte[] px)
    {
        byte[] m = new byte[13 + px.Length];
        m[0] = 1;
        BinaryPrimitives.WriteInt64LittleEndian(m.AsSpan(1), micros);
        BinaryPrimitives.WriteUInt16LittleEndian(m.AsSpan(9), Synthetic.W);
        BinaryPrimitives.WriteUInt16LittleEndian(m.AsSpan(11), Synthetic.H);
        px.CopyTo(m, 13);
        return m;
    }

    [Fact]
    public async Task PlayerFramesAreAnalysedLiveAndOnlyConfirmedNumbersAreWritten()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using var bus = new InProcessEventBus();
        using IEventSubscription seen = bus.Subscribe("*", capacity: 1 << 16);
        await using var host = new CalibrationHost(bus, new CalibrationHostOptions { ProfilesDirectory = _dir });
        await using var bridge = new WebSocketBridge(bus, new WebSocketBridgeOptions { Pages = [CalibrationHost.Page], Sockets = host.Sockets });
        await bridge.StartAsync(ct);
        Dictionary<string, DateTime> before = Directory.EnumerateFiles(_dir).ToDictionary(f => f, File.GetLastWriteTimeUtc);

        using var ingest = new ClientWebSocket();
        await ingest.ConnectAsync(new Uri($"{bridge.SocketUrl(CalibrationHost.IngestPath)}?token={bridge.Token}"), ct);
        await ingest.SendAsync(Encoding.UTF8.GetBytes("""{"type":"start","platform":"youtube","videoRef":"abc","profile":"cs2"}"""), WebSocketMessageType.Text, true, ct);
        var s = new Synthetic();
        for (int i = 0; i < 90; i++)
        {
            await ingest.SendAsync(Video(i * 33_333L, s.Frame(false)), WebSocketMessageType.Binary, true, ct);
        }

        for (int wait = 0; wait < 200 && host.Session?.VideoFrames < 90; wait++)
        {
            await Task.Delay(25, ct);
        }

        OfficialPlayerSession session = host.Session!;
        Assert.Equal(90, session.VideoFrames);
        Assert.Equal(SourceKind.OfficialPlayer, session.Started!.SourceKind);
        Assert.Equal(Retention.Ephemeral, session.Started.Retention);
        Assert.False(RetentionRules.CanPersist(session.Started));
        Assert.DoesNotContain("abc", session.Started.ToString(), StringComparison.Ordinal); // the video is not named anywhere

        JsonNode state = JsonNode.Parse(host.State())!;
        Assert.Equal("cs2", (string?)state["profile"]);
        Assert.NotEmpty(state["regions"]!.AsArray());

        // Nothing was written while watching.
        Assert.Equal(before, Directory.EnumerateFiles(_dir).ToDictionary(f => f, File.GetLastWriteTimeUtc));

        // Unconfirmed save is refused; a confirmed one writes the profile.
        JsonNode save = JsonNode.Parse("""{"type":"save","regions":{"health":{"x":0.04,"y":0.8,"w":0.21,"h":0.15}}}""")!;
        await Assert.ThrowsAsync<Lookback.Stream.SessionLog.RetentionViolationException>(() => host.CommandAsync(save));
        save["confirmed"] = true;
        string reply = await host.CommandAsync(save);
        Assert.Contains("cs2.json", reply, StringComparison.Ordinal);
        Assert.Contains("\"x\": 0.04, \"y\": 0.8, \"w\": 0.21, \"h\": 0.15", File.ReadAllText(Path.Combine(_dir, "cs2.json")), StringComparison.Ordinal);
        Assert.Equal(5, Directory.EnumerateFiles(_dir).Count());

        await ingest.SendAsync(Encoding.UTF8.GetBytes("""{"type":"stop"}"""), WebSocketMessageType.Text, true, ct);
        for (int wait = 0; wait < 200 && host.Session is not null; wait++)
        {
            await Task.Delay(25, ct);
        }

        Assert.Null(host.Session);
        var kinds = new List<Envelope.PayloadOneofCase>();
        while (seen.TryRead(out Envelope? e))
        {
            kinds.Add(e!.PayloadCase);
        }

        Assert.Contains(Envelope.PayloadOneofCase.SessionStarted, kinds);
        Assert.Contains(Envelope.PayloadOneofCase.SignalFrame, kinds);
        Assert.Contains(Envelope.PayloadOneofCase.SessionEnded, kinds);
    }

    [Fact]
    public async Task IngestNeedsTheToken()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using var bus = new InProcessEventBus();
        await using var host = new CalibrationHost(bus);
        await using var bridge = new WebSocketBridge(bus, new WebSocketBridgeOptions { Sockets = host.Sockets });
        await bridge.StartAsync(ct);
        using var ingest = new ClientWebSocket();
        await Assert.ThrowsAnyAsync<WebSocketException>(() => ingest.ConnectAsync(new Uri($"{bridge.SocketUrl(CalibrationHost.IngestPath)}?token=wrong"), ct));
    }
}
