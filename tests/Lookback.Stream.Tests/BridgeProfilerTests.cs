using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Lookback.Contracts.V1;
using Lookback.Core.Bus;
using Lookback.Stream.WebSockets;

namespace Lookback.Stream.Tests;

public class BridgeProfilerTests
{
    private static readonly BridgePage Page = new("/viewer", "<!DOCTYPE html><title>viewer</title>");

    [Fact]
    public async Task ServesAConfiguredPageWithLockedDownHeaders()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using var bus = new InProcessEventBus();
        await using var bridge = new WebSocketBridge(bus, new WebSocketBridgeOptions { Pages = [Page] });
        await bridge.StartAsync(ct);

        using var http = new HttpClient();
        using HttpResponseMessage r = await http.GetAsync(new Uri($"http://{bridge.Endpoint!.Authority}/viewer"), ct);

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("<!DOCTYPE html><title>viewer</title>", await r.Content.ReadAsStringAsync(ct));
        string csp = string.Join(";", r.Headers.GetValues("Content-Security-Policy"));
        Assert.Contains("connect-src 'self'", csp, StringComparison.Ordinal);
        Assert.Contains("frame-ancestors 'none'", csp, StringComparison.Ordinal);
        Assert.Equal("nosniff", r.Headers.GetValues("X-Content-Type-Options").Single());
    }

    [Fact]
    public async Task PageUrlCarriesTheTokenOnlyInTheFragment()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using var bus = new InProcessEventBus();
        await using var bridge = new WebSocketBridge(bus, new WebSocketBridgeOptions { Pages = [Page] });
        await bridge.StartAsync(ct);

        Uri url = bridge.PageUrl("/viewer");
        Assert.Equal("/viewer", url.AbsolutePath);
        Assert.Equal("", url.Query);
        Assert.Equal("#token=" + bridge.Token, url.Fragment);
    }

    [Fact]
    public async Task ServesNoPagesByDefault()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using var bus = new InProcessEventBus();
        await using var bridge = new WebSocketBridge(bus);
        await bridge.StartAsync(ct);

        using var http = new HttpClient();
        using HttpResponseMessage r = await http.GetAsync(new Uri($"http://{bridge.Endpoint!.Authority}/profiler"), ct);
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }

    [Fact]
    public async Task OwnOriginIsAllowedOnlyWhenServingPages()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using var bus = new InProcessEventBus();

        await using (var plain = new WebSocketBridge(bus))
        {
            await plain.StartAsync(ct);
            using var ws = new ClientWebSocket();
            ws.Options.SetRequestHeader("Origin", $"http://127.0.0.1:{plain.Endpoint!.Port}");
            await Assert.ThrowsAsync<WebSocketException>(() => ws.ConnectAsync(new Uri($"{plain.Endpoint}?token={plain.Token}"), ct));
        }

        await using var served = new WebSocketBridge(bus, new WebSocketBridgeOptions { Pages = [Page] });
        await served.StartAsync(ct);
        foreach (string host in new[] { "127.0.0.1", "localhost" })
        {
            using var ws = new ClientWebSocket();
            ws.Options.SetRequestHeader("Origin", $"http://{host}:{served.Endpoint!.Port}");
            await ws.ConnectAsync(new Uri($"{served.Endpoint}?token={served.Token}"), ct);
            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", ct);
        }

        using var evil = new ClientWebSocket();
        evil.Options.SetRequestHeader("Origin", "https://evil.example");
        await Assert.ThrowsAsync<WebSocketException>(() => evil.ConnectAsync(new Uri($"{served.Endpoint}?token={served.Token}"), ct));
    }

    [Fact]
    public async Task ALateClientGetsTheSessionAndSchemasFirst()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using var bus = new InProcessEventBus();
        await using var bridge = new WebSocketBridge(bus);
        await bridge.StartAsync(ct);

        bus.Publish(TestEvents.Start());
        bus.Publish(Schema());
        bus.Publish(TestEvents.Signal(5, values: [1f]));
        while (bridge.SnapshotSeen < 3)
        {
            await Task.Delay(5, ct);
        }

        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri($"{bridge.Endpoint}?token={bridge.Token}"), ct);
        using JsonDocument first = await ReceiveAsync(ws, ct);
        using JsonDocument second = await ReceiveAsync(ws, ct);

        Assert.True(first.RootElement.TryGetProperty("sessionStarted", out _));
        Assert.Equal("a", second.RootElement.GetProperty("signalSchema").GetProperty("names")[0].GetString());
        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", ct);
    }

    [Fact]
    public async Task SnapshotForgetsEndedSessionsAndKeepsTheLatestSchema()
    {
        using var bus = new InProcessEventBus();
        await using var snap = new StreamSnapshot(bus);

        snap.Observe(TestEvents.Start("s1"));
        snap.Observe(Schema("s1", "a"));
        snap.Observe(Schema("s1", "b"));   // same producer and id: replaces
        snap.Observe(TestEvents.Signal(1, "s1", 1f));
        Assert.Collection(
            snap.Capture(),
            e => Assert.Equal(Envelope.PayloadOneofCase.SessionStarted, e.PayloadCase),
            e => Assert.Equal("b", e.SignalSchema.Names[0]));

        snap.Observe(new Envelope { SessionId = "s1", Topic = "session", SessionEnded = new SessionEnded() });
        Assert.Empty(snap.Capture());
    }

    [Fact]
    public async Task SnapshotIsBoundedToTheNewestSessions()
    {
        using var bus = new InProcessEventBus();
        await using var snap = new StreamSnapshot(bus);
        for (int i = 0; i < StreamSnapshot.MaxSessions + 5; i++)
        {
            snap.Observe(TestEvents.Start("s" + i));
        }

        IReadOnlyList<Envelope> all = snap.Capture();
        Assert.Equal(StreamSnapshot.MaxSessions, all.Count);
        Assert.Equal("s5", all[0].SessionId);
    }

    private static Envelope Schema(string session = "s1", string name = "a") => new()
    {
        SessionId = session,
        Topic = "signals.video",
        SourceId = "test",
        SignalSchema = new SignalSchema { SchemaId = 1, Family = SignalFamily.Video, Names = { name } },
    };

    private static async Task<JsonDocument> ReceiveAsync(ClientWebSocket ws, CancellationToken ct)
    {
        byte[] buffer = new byte[8192];
        WebSocketReceiveResult r = await ws.ReceiveAsync(buffer, ct);
        return JsonDocument.Parse(Encoding.UTF8.GetString(buffer, 0, r.Count));
    }
}
