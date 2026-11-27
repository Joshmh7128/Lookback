using Lookback.Contracts.V1;
using Lookback.Core.Bus;
using Lookback.Core.Time;

namespace Lookback.Core.Tests;

public class InProcessEventBusTests
{
    private static readonly EnvelopeFactory Factory = new(new ManualClock(), "s1", "test");

    [Fact]
    public void RoutesByTopicPattern()
    {
        using var bus = new InProcessEventBus();
        using IEventSubscription signals = bus.Subscribe("signals.*");
        using IEventSubscription clips = bus.Subscribe("clip.candidate");

        bus.Publish(Factory.Create("signals.video"));
        bus.Publish(Factory.Create("clip.candidate"));
        bus.Publish(Factory.Create("signals.audio"));

        Assert.Equal(["signals.video", "signals.audio"], Drain(signals).Select(e => e.Topic));
        Assert.Equal(["clip.candidate"], Drain(clips).Select(e => e.Topic));
    }

    [Fact]
    public void SlowSubscriberDropsOldestWithoutBlockingPublisher()
    {
        using var bus = new InProcessEventBus();
        using IEventSubscription slow = bus.Subscribe("*", capacity: 4);

        for (int i = 0; i < 10; i++)
        {
            Assert.True(bus.Publish(Factory.Create("signals.video")));
        }

        List<Envelope> kept = Drain(slow);
        Assert.Equal(4, kept.Count);
        Assert.Equal(6, slow.Dropped);

        // The newest events survive; sequence numbers show the gap.
        Assert.True(kept[0].Seq < kept[^1].Seq);
    }

    [Fact]
    public void UnsubscribedReaderStopsReceiving()
    {
        using var bus = new InProcessEventBus();
        IEventSubscription sub = bus.Subscribe("*");
        sub.Dispose();
        bus.Publish(Factory.Create("signals.video"));
        Assert.False(sub.TryRead(out _));
    }

    [Fact]
    public async Task DisposeCompletesReaders()
    {
        var bus = new InProcessEventBus();
        IEventSubscription sub = bus.Subscribe("*");
        bus.Publish(Factory.Create("signals.video"));
        bus.Dispose();

        int count = 0;
        await foreach (Envelope _ in sub.ReadAllAsync(TestContext.Current.CancellationToken))
        {
            count++;
        }

        Assert.Equal(1, count);
        Assert.False(bus.Publish(Factory.Create("signals.video")));
    }

    private static List<Envelope> Drain(IEventSubscription sub)
    {
        var list = new List<Envelope>();
        while (sub.TryRead(out Envelope? e))
        {
            list.Add(e!);
        }

        return list;
    }
}
