using Lookback.Core.Diagnostics;
using Lookback.Core.Time;

namespace Lookback.Core.Tests;

/// <summary>The watchdog on a manual clock: when it reports, how often, and what it ignores.</summary>
public class StallWatchdogTests
{
    private static readonly TimeSpan Threshold = TimeSpan.FromSeconds(2);

    [Fact]
    public void ReportsABusyStepOnceItOutlivesTheThreshold()
    {
        var clock = new ManualClock();
        using var dog = new StallWatchdog(clock);
        var beat = new Beat();
        List<StallReport> reports = [];
        using IDisposable w = dog.Watch("encoder", beat, Threshold, reports.Add);

        beat.Enter("ProcessInput", clock.Now);
        clock.Advance(TimeSpan.FromSeconds(1.9));
        Assert.Equal(0, dog.CheckNow());
        clock.Advance(TimeSpan.FromSeconds(0.2));
        Assert.Equal(1, dog.CheckNow());

        StallReport r = Assert.Single(reports);
        Assert.Equal("encoder", r.Name);
        Assert.Equal("ProcessInput", r.Step);
        Assert.InRange(r.For.TotalSeconds, 2.0, 2.2);
    }

    [Fact]
    public void OneEpisodeIsReportedOnceAndANewEpisodeRearms()
    {
        var clock = new ManualClock();
        using var dog = new StallWatchdog(clock);
        var beat = new Beat();
        int reports = 0;
        using IDisposable w = dog.Watch("ticker", beat, Threshold, _ => reports++);

        beat.Enter("encode", clock.Now);
        clock.Advance(TimeSpan.FromSeconds(3));
        dog.CheckNow();
        clock.Advance(TimeSpan.FromSeconds(3));
        dog.CheckNow();
        Assert.Equal(1, reports); // still the same stuck call

        beat.Exit();
        beat.Enter("encode", clock.Now);
        clock.Advance(TimeSpan.FromSeconds(3));
        dog.CheckNow();
        Assert.Equal(2, reports);
    }

    [Fact]
    public void IdleThreadsAndShortStepsAreNeverReported()
    {
        var clock = new ManualClock();
        using var dog = new StallWatchdog(clock);
        var beat = new Beat();
        int reports = 0;
        using IDisposable w = dog.Watch("capture", beat, Threshold, _ => reports++);

        clock.Advance(TimeSpan.FromMinutes(5)); // idle for minutes: WGC sends nothing while the window is static
        dog.CheckNow();
        for (int i = 0; i < 100; i++)
        {
            beat.Enter("frame callback", clock.Now);
            clock.Advance(TimeSpan.FromMilliseconds(30));
            beat.Exit();
            dog.CheckNow();
        }

        Assert.Equal(0, reports);
    }

    [Fact]
    public void DisposedWatchesStopReporting()
    {
        var clock = new ManualClock();
        using var dog = new StallWatchdog(clock);
        var beat = new Beat();
        int reports = 0;
        IDisposable w = dog.Watch("x", beat, Threshold, _ => reports++);
        beat.Enter("step", clock.Now);
        w.Dispose();
        clock.Advance(TimeSpan.FromSeconds(10));
        dog.CheckNow();
        Assert.Equal(0, reports);
    }

    [Fact]
    public void TheThreadedWatchdogFiresOnItsOwn()
    {
        var clock = new ManualClock();
        using var dog = new StallWatchdog(clock, TimeSpan.FromMilliseconds(10));
        var beat = new Beat();
        using var fired = new ManualResetEventSlim();
        using IDisposable w = dog.Watch("x", beat, Threshold, _ => fired.Set());
        beat.Enter("step", clock.Now);
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.True(fired.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
    }
}
