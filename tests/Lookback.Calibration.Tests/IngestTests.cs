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

    private static Task SendText(ClientWebSocket socket, string json, CancellationToken ct) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, ct);

    private static async Task<string> ReceiveText(ClientWebSocket socket, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        byte[] buffer = new byte[4096];
        WebSocketReceiveResult r = await socket.ReceiveAsync(buffer, timeout.Token);
        Assert.Equal(WebSocketMessageType.Text, r.MessageType);
        return Encoding.UTF8.GetString(buffer, 0, r.Count);
    }

    [Fact]
    public async Task A_replay_sends_the_player_back_to_the_candidates_moment_in_the_video()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using var bus = new InProcessEventBus();
        await using var host = new CalibrationHost(bus);
        await using var bridge = new WebSocketBridge(bus, new WebSocketBridgeOptions { Pages = [CalibrationHost.Page], Sockets = host.Sockets });
        await bridge.StartAsync(ct);

        using var ingest = new ClientWebSocket();
        await ingest.ConnectAsync(new Uri($"{bridge.SocketUrl(CalibrationHost.IngestPath)}?token={bridge.Token}"), ct);
        await SendText(ingest, """{"type":"start","platform":"youtube","videoRef":"abc"}""", ct);

        // The user started the video at 0:57. Five seconds later they skipped to 3:20.
        await SendText(ingest, """{"type":"position","t":0,"video":57,"rate":1,"playing":true,"seekable":true}""", ct);
        await SendText(ingest, """{"type":"position","t":5000000,"video":200,"rate":1,"playing":true,"seekable":true}""", ct);
        for (int wait = 0; wait < 200 && host.Session?.HasPosition != true; wait++)
        {
            await Task.Delay(25, ct);
        }

        OfficialPlayerSession session = host.Session!;
        for (int wait = 0; wait < 200 && session.VideoSecondsAt(session.OriginQpc + (5 * session.Check.Frequency)) != 200; wait++)
        {
            await Task.Delay(25, ct);
        }

        long f = session.Check.Frequency;
        session.Check.Add(new ClipCandidate { CandidateId = "early", StartQpc = (ulong)(session.OriginQpc + (2 * f)), EndQpc = (ulong)(session.OriginQpc + (4 * f)) }, null);
        session.Check.Add(new ClipCandidate { CandidateId = "late", StartQpc = (ulong)(session.OriginQpc + (12 * f)), EndQpc = (ulong)(session.OriginQpc + (15 * f)) }, null);

        // The panel shows each candidate at its moment in the video, and offers a replay.
        JsonNode check = JsonNode.Parse(host.State())!["check"]!;
        Assert.True((bool)check["canReplay"]!);
        Assert.Equal(59.0, (double)check["recent"]!.AsArray().Single(c => (string)c!["id"]! == "early")!["video"]!);
        Assert.Equal(207.0, (double)check["recent"]!.AsArray().Single(c => (string)c!["id"]! == "late")!["video"]!);

        // Replay: the recorder asks the viewer's player to go there.
        Assert.Contains("replay", await host.CommandAsync(JsonNode.Parse("""{"type":"replay","candidateId":"late"}""")), StringComparison.Ordinal);
        JsonNode seek = JsonNode.Parse(await ReceiveText(ingest, ct))!;
        Assert.Equal("seek", (string?)seek["type"]);
        Assert.Equal(207.0, (double)seek["seconds"]!, 3);

        await Assert.ThrowsAsync<KeyNotFoundException>(() => host.CommandAsync(JsonNode.Parse("""{"type":"replay","candidateId":"nope"}""")));

        // A live channel cannot go back: the replay is refused and the panel offers none.
        await SendText(ingest, """{"type":"position","t":6000000,"video":201,"rate":1,"playing":true,"seekable":false}""", ct);
        for (int wait = 0; wait < 200 && session.Seekable; wait++)
        {
            await Task.Delay(25, ct);
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => host.CommandAsync(JsonNode.Parse("""{"type":"replay","candidateId":"late"}""")));
        Assert.False((bool)JsonNode.Parse(host.State())!["check"]!["canReplay"]!);
    }

    [Fact]
    public async Task Positions_map_capture_time_to_the_video_through_pauses_speed_and_seeks()
    {
        using var bus = new InProcessEventBus();
        await using var session = new OfficialPlayerSession(bus, Lookback.Profiles.Model.ProfileCatalog.BuiltIn, "youtube");
        session.SetOrigin(0);
        long f = session.Check.Frequency;
        Assert.Null(session.VideoSecondsAt(f));
        Assert.False(session.HasPosition);

        session.OnPosition(0, 10, 1, playing: true, seekable: true);
        session.OnPosition(4_000_000, 14, 2, playing: true, seekable: true);   // now at double speed
        session.OnPosition(6_000_000, 18, 2, playing: false, seekable: true);  // paused at 0:18
        session.OnPosition(9_000_000, 18, 1, playing: true, seekable: true);   // resumed three seconds later
        session.OnPosition(1_000_000, 99, 1, playing: true, seekable: true);   // out of order: ignored
        session.OnPosition(10_000_000, double.NaN, 1, playing: true, seekable: true); // impossible: ignored

        Assert.Equal(12.0, session.VideoSecondsAt(2 * f)!.Value, 6);           // 0:10 + 2 s
        Assert.Equal(16.0, session.VideoSecondsAt(5 * f)!.Value, 6);           // 0:14 + 1 s at 2x
        Assert.Equal(18.0, session.VideoSecondsAt(7 * f)!.Value, 6);           // paused
        Assert.Equal(20.0, session.VideoSecondsAt(11 * f)!.Value, 6);          // 0:18 + 2 s
        Assert.Equal(8.0, session.VideoSecondsAt(-2 * f)!.Value, 6);           // before the first report: counted back
        Assert.Equal(0.0, session.VideoSecondsAt(-60 * f)!.Value, 6);          // never before the start of the video
        Assert.True(session.Seekable);
    }
}
