using Lookback.Media.Frames;
using Lookback.Profiles.Model;
using Lookback.Profiles.Regions;
using Lookback.Signals.Video;

namespace Lookback.Profiles.Tests;

public class RegionSignalTests
{
    private static VideoFrame Frame(Action<Span<byte>, int> paint)
    {
        var f = new VideoFrame(100, 100);
        Span<byte> p = f.Pixels;
        for (int i = 0; i < p.Length; i += 4)
        {
            p[i + 3] = 255;
        }

        paint(p, 100);
        return f;
    }

    private static void Rect(Span<byte> p, int w, int x0, int y0, int x1, int y1, byte r, byte g, byte b)
    {
        for (int y = y0; y < y1; y++)
        {
            for (int x = x0; x < x1; x++)
            {
                int o = ((y * w) + x) * 4;
                p[o] = b;
                p[o + 1] = g;
                p[o + 2] = r;
            }
        }
    }

    [Fact]
    public void MeasuresOnlyInsideTheRegion()
    {
        HudRegion[] regions = [new HudRegion { Name = "corner", X = 0, Y = 0, W = 0.2, H = 0.2 }, new HudRegion { Name = "rest", X = 0.5, Y = 0.5, W = 0.5, H = 0.5 }];
        var signal = new RegionSignal(regions, 100, 100);
        Assert.Equal(["region.corner.luma", "region.corner.activity", "region.corner.saturation", "region.corner.red"], signal.Names.Take(4));

        var luma = new LumaPlane(100, 100);
        var out1 = new float[8];
        VideoFrame red = Frame((p, w) => Rect(p, w, 0, 0, 20, 20, 255, 0, 0));
        luma.Compute(red);
        signal.Process(red, luma, out1);

        Assert.Equal(0, out1[1]); // no activity on the first frame
        Assert.Equal(1, out1[2], 2); // pure red is fully saturated
        Assert.Equal(1, out1[3], 2);
        Assert.Equal(0, out1[4], 3); // the other region is black
        Assert.Equal(0, out1[6], 3);

        // Change only the corner: activity there, none elsewhere.
        VideoFrame white = Frame((p, w) => Rect(p, w, 0, 0, 20, 20, 255, 255, 255));
        luma.Compute(white);
        var out2 = new float[8];
        signal.Process(white, luma, out2);
        Assert.True(out2[1] > 0.5f);
        Assert.Equal(0, out2[5], 3);
        Assert.Equal(1, out2[0], 2);
        Assert.Equal(0, out2[2], 2); // white is unsaturated
    }

    [Fact]
    public void DoesNotAllocatePerFrame()
    {
        var signal = new RegionSignal([new HudRegion { Name = "a", X = 0.1, Y = 0.1, W = 0.5, H = 0.5 }], 100, 100);
        VideoFrame f = Frame((_, _) => { });
        var luma = new LumaPlane(100, 100);
        luma.Compute(f);
        var output = new float[4];
        signal.Process(f, luma, output);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++)
        {
            signal.Process(f, luma, output);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
