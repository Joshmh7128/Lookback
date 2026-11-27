using Lookback.Profiles.Detection;

namespace Lookback.Profiles.Tests;

public class MergerTests
{
    [Fact]
    public void ReleasesInTimeOrderOnlyBelowTheWatermark()
    {
        var m = new TimeOrderedMerger<string>(maxLag: 1000);
        m.Register(1);
        m.Register(2);
        var output = new List<string>();

        m.Add(1, 10, "v10");
        m.Add(1, 30, "v30");
        m.Release(output);
        Assert.Empty(output); // stream 2 has said nothing yet

        m.Add(2, 20, "a20");
        m.Release(output);
        Assert.Equal(["v10", "a20"], output); // watermark is 20; v30 waits

        m.Add(2, 40, "a40");
        m.Release(output);
        Assert.Equal(["v10", "a20", "v30"], output);

        m.Flush(output);
        Assert.Equal(["v10", "a20", "v30", "a40"], output);
        Assert.Equal(0, m.Pending);
    }

    [Fact]
    public void TiesKeepArrivalOrder()
    {
        var m = new TimeOrderedMerger<string>(1000);
        m.Add(1, 5, "first");
        m.Add(2, 5, "second");
        var output = new List<string>();
        m.Release(output);
        Assert.Equal(["first", "second"], output);
    }

    [Fact]
    public void StalledStreamStopsHoldingBackAfterMaxLag()
    {
        var m = new TimeOrderedMerger<int>(maxLag: 100);
        m.Register(1);
        m.Register(2); // never sends: a game with no audio
        var output = new List<int>();

        m.Add(1, 50, 50);
        m.Release(output);
        Assert.Empty(output); // within maxLag the silent stream still holds the watermark

        m.Add(1, 500, 500);
        m.Release(output);
        Assert.Equal([50, 500], output);
    }
}
