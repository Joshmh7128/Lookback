using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Lookback.Core.Bus;
using Lookback.Stream.WebSockets;

namespace Lookback.Stream.Tests;

/// <summary>Control messages on <c>/events</c>: replies reach the sender only, never interleave with events, and oversized messages are dropped.</summary>
public class BridgeControlTests
{
    private static async Task<ClientWebSocket> ConnectAsync(WebSocketBridge bridge, CancellationToken ct)
    {
        var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri($"{bridge.Endpoint}?token={bridge.Token}"), ct);
        return ws;
    }

    private static Task SendAsync(ClientWebSocket ws, string text, CancellationToken ct) =>
        ws.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, endOfMessage: true, ct);

    /// <summary>One whole text message, or null if none arrives within <paramref name="within"/>.</summary>
    private static async Task<string?> ReceiveAsync(ClientWebSocket ws, TimeSpan within, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(within);
        var message = new MemoryStream();
        byte[] buffer = new byte[8192];
        try
        {
            while (true)
            {
                WebSocketReceiveResult r = await ws.ReceiveAsync(buffer, timeout.Token);
                message.Write(buffer, 0, r.Count);
                if (r.EndOfMessage)
                {
                    return Encoding.UTF8.GetString(message.ToArray());
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    [Fact]
    public async Task A_reply_goes_to_the_sender_only()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using var bus = new InProcessEventBus();
        await using var bridge = new WebSocketBridge(bus, new WebSocketBridgeOptions { ReplaySnapshot = false, Control = (text, _) => ValueTask.FromResult<string?>("re:" + text) });
        await bridge.StartAsync(ct);
        using ClientWebSocket a = await ConnectAsync(bridge, ct);
        using ClientWebSocket b = await ConnectAsync(bridge, ct);

        await SendAsync(a, "hello", ct);
        Assert.Equal("re:hello", await ReceiveAsync(a, TimeSpan.FromSeconds(5), ct));
        Assert.Null(await ReceiveAsync(b, TimeSpan.FromMilliseconds(300), ct));
    }

    [Fact]
    public async Task Replies_and_streamed_events_never_interleave()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using var bus = new InProcessEventBus();
        string big = new('x', 20_000); // several frames on the wire
        await using var bridge = new WebSocketBridge(bus, new WebSocketBridgeOptions { ReplaySnapshot = false, Control = (text, _) => ValueTask.FromResult<string?>($"re:{text}:{big}") });
        await bridge.StartAsync(ct);
        using ClientWebSocket ws = await ConnectAsync(bridge, ct);

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task publisher = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                bus.Publish(TestEvents.Signal(1, values: [0.5f]));
                await Task.Yield();
            }
        }, CancellationToken.None);

        for (int i = 0; i < 20; i++)
        {
            await SendAsync(ws, $"m{i}", ct);
        }

        int replies = 0, events = 0;
        while (replies < 20)
        {
            string? frame = await ReceiveAsync(ws, TimeSpan.FromSeconds(10), ct);
            Assert.NotNull(frame);
            if (frame.StartsWith("re:", StringComparison.Ordinal))
            {
                Assert.EndsWith(big, frame, StringComparison.Ordinal);
                replies++;
            }
            else
            {
                using JsonDocument doc = JsonDocument.Parse(frame); // a torn event would not parse
                Assert.Equal("signals.video", doc.RootElement.GetProperty("topic").GetString());
                events++;
            }
        }

        await stop.CancelAsync();
        await publisher;
        Assert.Equal(20, replies);
    }

    [Fact]
    public async Task Oversized_messages_are_dropped_and_the_socket_carries_on()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using var bus = new InProcessEventBus();
        await using var bridge = new WebSocketBridge(bus, new WebSocketBridgeOptions { ReplaySnapshot = false, MaxControlMessageBytes = 100, Control = (text, _) => ValueTask.FromResult<string?>("re:" + text) });
        await bridge.StartAsync(ct);
        using ClientWebSocket ws = await ConnectAsync(bridge, ct);

        await SendAsync(ws, new string('y', 200), ct);
        await SendAsync(ws, "small", ct);
        Assert.Equal("re:small", await ReceiveAsync(ws, TimeSpan.FromSeconds(5), ct));
    }

    [Fact]
    public async Task Without_a_handler_inbound_messages_are_ignored()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using var bus = new InProcessEventBus();
        await using var bridge = new WebSocketBridge(bus, new WebSocketBridgeOptions { ReplaySnapshot = false });
        await bridge.StartAsync(ct);
        using ClientWebSocket ws = await ConnectAsync(bridge, ct);
        await SendAsync(ws, "{\"type\":\"rate\"}", ct);

        // No reply comes back, and the event stream keeps flowing.
        string? frame = null;
        for (int i = 0; i < 100 && frame is null; i++)
        {
            bus.Publish(TestEvents.Signal(7, values: [0.5f]));
            frame = await ReceiveAsync(ws, TimeSpan.FromSeconds(5), ct);
        }

        Assert.NotNull(frame);
        using JsonDocument doc = JsonDocument.Parse(frame);
        Assert.Equal("signals.video", doc.RootElement.GetProperty("topic").GetString());
    }
}
