using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Lookback.Contracts.V1;
using Lookback.Core.Bus;
using Lookback.Core.Time;
using Lookback.Stream.SessionLog;
using Lookback.Testbench.Live;

namespace Lookback.Testbench.Tests;

public class LiveTests(FootageFixture fixture) : IClassFixture<FootageFixture>
{
    private const long F = 10_000_000;

    [Fact]
    public async Task RelayReleasesInFootageOrderAtFootageTime()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var wall = new ManualClock(F);
        var released = new List<(ulong Qpc, long At)>();

        using var bus = new InProcessEventBus();
        using IEventSubscription sub = bus.Subscribe("*");
        var relay = new WallClockRelay(
            sub,
            (e, _) => { released.Add((e.Qpc, wall.Now)); return ValueTask.CompletedTask; },
            wall,
            F,
            speed: 2,
            delay: (t, _) => { wall.Advance(t); return Task.CompletedTask; });

        // Out of order, as the video and audio threads publish.
        foreach (ulong q in new ulong[] { 0, 3 * F, 1 * F, 2 * F })
        {
            bus.Publish(new Envelope { Qpc = q, Topic = "signals.video", SessionId = "s" });
        }

        bus.Dispose();
        await relay.RunAsync(ct);

        Assert.Equal([0UL, 1 * F, 2 * F, 3 * F], released.Select(r => r.Qpc));
        foreach ((ulong qpc, long at) in released)
        {
            // At 2x, footage second n is due n/2 seconds after the first envelope.
            Assert.InRange(at, (long)qpc / 2, ((long)qpc / 2) + (F / 100));
        }

        Assert.Equal(0, relay.Pending);
    }

    [Fact]
    public async Task LivePlaysTheClipIntoTheProfilerSocket()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string? url = null;
        var announced = new TaskCompletionSource();
        Task<LiveResult> run = LiveRunner.RunAsync(
            new LiveOptions { File = fixture.File, Game = "fixture-game", Profile = "generic", Speed = 6, Token = "T0KEN" },
            line =>
            {
                if (line.StartsWith("profiler ", StringComparison.Ordinal))
                {
                    url = line["profiler ".Length..];
                    announced.TrySetResult();
                }
            },
            ct);
        await announced.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);

        var page = new Uri(url!);
        Assert.Equal("#token=T0KEN", page.Fragment);
        using var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("Origin", $"http://127.0.0.1:{page.Port}"); // as the profiler page would
        await ws.ConnectAsync(new Uri($"ws://{page.Authority}/events?token=T0KEN"), ct);

        var kinds = new List<string>();
        var topics = new List<string>();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        byte[] buffer = new byte[1 << 16];
        while (!kinds.Contains("sessionEnded"))
        {
            WebSocketReceiveResult r = await ws.ReceiveAsync(buffer, ct);
            using JsonDocument doc = JsonDocument.Parse(Encoding.UTF8.GetString(buffer, 0, r.Count));
            if (doc.RootElement.TryGetProperty("sessionStarted", out JsonElement started))
            {
                Assert.Equal("fixture-game", started.GetProperty("gameProfile").GetString());
            }

            topics.Add(doc.RootElement.GetProperty("topic").GetString()!);
            kinds.Add(doc.RootElement.EnumerateObject().Select(p => p.Name).Last(n => n is not ("qpc" or "sessionId" or "topic" or "sourceId" or "seq")));
        }

        TimeSpan took = sw.Elapsed;
        LiveResult result = await run;

        Assert.Equal("sessionStarted", kinds[0]);
        Assert.Equal(3, kinds.Count(k => k == "signalSchema"));   // video, audio, and the profile's rules
        Assert.Contains("signals.rules", topics);                 // the profile ran live, in step with the clip
        Assert.InRange(topics.Count(t => t is "signals.video" or "signals.audio") - 2, 380, 420); // 90 video frames and ~300 audio hops
        Assert.Equal(result.Released, kinds.Count);
        // 3 s of footage at 6x is paced over about half a second, not dumped at once.
        Assert.True(took > TimeSpan.FromMilliseconds(350), $"took {took}");
    }

    [Fact]
    public async Task LiveRefusesPermissionedFootageWithoutAReference()
    {
        await Assert.ThrowsAsync<RetentionViolationException>(() => LiveRunner.RunAsync(
            new LiveOptions { File = fixture.File, Retention = Retention.Permissioned },
            _ => Assert.Fail("nothing should start"),
            TestContext.Current.CancellationToken));
    }
}
