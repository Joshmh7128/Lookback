using Lookback.Core.Buffers;

namespace Lookback.Core.Tests;

public class SpscRingTests
{
    [Fact]
    public void RoundsCapacityUpAndRefusesWhenFull()
    {
        using var ring = new SpscRing<int>(5);
        Assert.Equal(8, ring.Capacity);
        for (int i = 0; i < 8; i++)
        {
            Assert.True(ring.TryWrite(i));
        }

        Assert.False(ring.TryWrite(99));
        Assert.True(ring.TryRead(out int first));
        Assert.Equal(0, first);
        Assert.True(ring.TryWrite(8));
    }

    [Fact]
    public void PreservesOrderAcrossThreads()
    {
        const long n = 2_000_000;
        using var ring = new SpscRing<long>(1024);

        var producer = new Thread(() =>
        {
            for (long i = 0; i < n; i++)
            {
                while (!ring.TryWrite(i))
                {
                    Thread.SpinWait(1);
                }
            }
        });

        long expected = 0;
        producer.Start();
        while (expected < n)
        {
            if (ring.TryRead(out long v))
            {
                Assert.Equal(expected, v);
                expected++;
            }
            else
            {
                Thread.SpinWait(1);
            }
        }

        producer.Join();
        Assert.Equal(0, ring.Count);
    }
}
