using Lookback.Core.Time;

namespace Lookback.Core.Tests;

public class QpcTimeTests
{
    [Fact]
    public void RoundTripsAtTypicalFrequency()
    {
        const long freq = 10_000_000;
        TimeSpan d = TimeSpan.FromMilliseconds(1234.5678);
        Assert.Equal(d, QpcTime.ToTimeSpan(QpcTime.FromTimeSpan(d, freq), freq));
    }

    [Fact]
    public void DoesNotOverflowAfterWeeksOfUptime()
    {
        // ~30 days of uptime at a 3 GHz TSC-style counter.
        const long freq = 3_000_000_000;
        long ticks = 30L * 24 * 3600 * freq;
        Assert.Equal(TimeSpan.FromDays(30), QpcTime.ToTimeSpan(ticks, freq));
    }

    [Fact]
    public void RescalesAudioSamplesToQpc() =>
        Assert.Equal(10_000_000, QpcTime.Rescale(48_000, 48_000, 10_000_000));

    [Fact]
    public void ManualClockAdvances()
    {
        var clock = new ManualClock(frequency: 1000);
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(2000, clock.Now);
    }
}
