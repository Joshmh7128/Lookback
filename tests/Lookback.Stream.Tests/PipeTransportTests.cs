using Lookback.Contracts.V1;
using Lookback.Core.Bus;
using Lookback.Stream.Pipes;

namespace Lookback.Stream.Tests;

public class PipeTransportTests
{
    private static string NewPipeName() => "lookback-test-" + Guid.NewGuid().ToString("N")[..12];

    [Fact]
    public async Task ServerStreamsBusEventsToClient()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using var bus = new InProcessEventBus();
        string name = NewPipeName();
        await using var server = new PipeEventServer(bus, name, outboundPattern: "signals.*");
        server.Start();

        await using PipeEventClient client = await PipeEventClient.ConnectAsync(name, TimeSpan.FromSeconds(5), ct);
        await WaitForAsync(() => server.ActiveConnections == 1, ct);

        bus.Publish(TestEvents.Signal(42));
        bus.Publish(new Envelope { Topic = "clip.saved", SessionId = "s1" }); // filtered out

        await using IAsyncEnumerator<Envelope> reader = client.ReadAllAsync(ct).GetAsyncEnumerator(ct);
        Assert.True(await reader.MoveNextAsync());
        Assert.Equal(42UL, reader.Current.Qpc);
        Assert.Equal("signals.video", reader.Current.Topic);
    }

    [Fact]
    public async Task ClientPublishesOntoBusWithoutEcho()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using var bus = new InProcessEventBus();
        using IEventSubscription local = bus.Subscribe("*");
        string name = NewPipeName();
        await using var server = new PipeEventServer(bus, name);
        server.Start();

        await using PipeEventClient a = await PipeEventClient.ConnectAsync(name, TimeSpan.FromSeconds(5), ct);
        await using PipeEventClient b = await PipeEventClient.ConnectAsync(name, TimeSpan.FromSeconds(5), ct);
        await WaitForAsync(() => server.ActiveConnections == 2, ct);

        await a.PublishAsync(TestEvents.Signal(7), ct);

        // Reaches the local bus and the other client...
        Envelope onBus = await local.ReadAllAsync(ct).FirstAsync(ct);
        Assert.Equal(7UL, onBus.Qpc);
        await using IAsyncEnumerator<Envelope> bReader = b.ReadAllAsync(ct).GetAsyncEnumerator(ct);
        Assert.True(await bReader.MoveNextAsync());
        Assert.Equal(7UL, bReader.Current.Qpc);

        // ...but not back to its sender: the next thing A sees is a later event.
        bus.Publish(TestEvents.Signal(8));
        await using IAsyncEnumerator<Envelope> aReader = a.ReadAllAsync(ct).GetAsyncEnumerator(ct);
        Assert.True(await aReader.MoveNextAsync());
        Assert.Equal(8UL, aReader.Current.Qpc);
    }

    private static async Task WaitForAsync(Func<bool> condition, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }
}
