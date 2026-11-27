using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Lookback.Core.Bus;
using Lookback.Stream.WebSockets;

namespace Lookback.Stream.Tests;

public class WebSocketBridgeTests
{
    [Fact]
    public async Task StreamsJsonToAClientWithTheToken()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using var bus = new InProcessEventBus();
        await using var bridge = new WebSocketBridge(bus);
        await bridge.StartAsync(ct);

        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri($"{bridge.Endpoint}?token={bridge.Token}"), ct);

        // The subscription is created after the upgrade; publish until the first frame lands.
        byte[] buffer = new byte[4096];
        Task<WebSocketReceiveResult> receive = ws.ReceiveAsync(buffer, ct);
        while (!receive.IsCompleted)
        {
            bus.Publish(TestEvents.Signal(99, values: [0.5f]));
            await Task.WhenAny(receive, Task.Delay(20, ct));
        }

        WebSocketReceiveResult result = await receive;
        using JsonDocument doc = JsonDocument.Parse(Encoding.UTF8.GetString(buffer, 0, result.Count));
        Assert.Equal("signals.video", doc.RootElement.GetProperty("topic").GetString());
        Assert.Equal("99", doc.RootElement.GetProperty("qpc").GetString());

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", ct);
    }

    [Fact]
    public async Task RejectsMissingToken()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using var bus = new InProcessEventBus();
        await using var bridge = new WebSocketBridge(bus);
        await bridge.StartAsync(ct);

        using var ws = new ClientWebSocket();
        await Assert.ThrowsAsync<WebSocketException>(() => ws.ConnectAsync(bridge.Endpoint!, ct));
    }

    [Fact]
    public async Task RejectsForeignBrowserOrigin()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using var bus = new InProcessEventBus();
        await using var bridge = new WebSocketBridge(bus);
        await bridge.StartAsync(ct);

        using var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("Origin", "https://evil.example");
        await Assert.ThrowsAsync<WebSocketException>(() => ws.ConnectAsync(new Uri($"{bridge.Endpoint}?token={bridge.Token}"), ct));
    }
}
