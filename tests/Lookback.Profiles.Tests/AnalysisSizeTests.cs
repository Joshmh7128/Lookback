using Lookback.Media.Frames;
using Lookback.Profiles.Model;
using Lookback.Profiles.Regions;
using Lookback.Signals.Video;

namespace Lookback.Profiles.Tests;

/// <summary>
/// The analysis size is an experiment setting, so every signal must mean the
/// same at every size: a picture with no detail finer than the smallest
/// size gives the same values at 384x216 and at four times that.
/// </summary>
public class AnalysisSizeTests
{
    private static readonly HudRegion[] Regions =
    [
        // On the 384x216 pixel grid, so both sizes cover exactly the same area.
        new HudRegion { Name = "box", X = 0.25, Y = 0.5, W = 0.25, H = 0.25 },
        new HudRegion { Name = "corner", X = 0, Y = 0, W = 0.125, H = 0.125 },
    ];

    /// <summary>A scene drawn in 384x216 cells, scaled up by <paramref name="scale"/> (nearest neighbour).</summary>
    private static VideoFrame Scene(int scale, int step)
    {
        int w = 384 * scale, h = 216 * scale;
        var f = new VideoFrame(w, h);
        Span<byte> p = f.Pixels;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int cx = x / scale, cy = y / scale;
                int o = ((y * w) + x) * 4;
                byte r = (byte)((cx * 3) + (step * 20)), g = (byte)(cy * 2), b = 90;
                if (cx >= 96 + (step * 8) && cx < 160 + (step * 8) && cy >= 108 && cy < 162)
                {
                    (r, g, b) = (250, 30, 30); // a red box that moves right each step
                }

                if (cx < 24 && cy < 12)
                {
                    (r, g, b) = (200, 200, 200); // a static HUD patch
                }

                p[o] = b;
                p[o + 1] = g;
                p[o + 2] = r;
                p[o + 3] = 255;
            }
        }

        return f;
    }

    private static VideoSignalBank Bank(AnalysisFormat format) => new(format,
    [
        new LumaStatsSignal(),
        new MotionSignal(format.FramesPerSecond),
        new FlashSignal(),
        new SceneCutSignal(),
        new HudSignal(format.PixelCount, warmupFrames: 2),
        new RegionSignal(Regions, format),
    ]);

    [Fact]
    public void Every_signal_reads_the_same_at_four_times_the_size()
    {
        VideoSignalBank small = Bank(AnalysisFormat.Default);
        VideoSignalBank large = Bank(AnalysisFormat.Default.WithSize(1536, 864));
        Assert.Equal(small.Names, large.Names);

        for (int step = 0; step < 4; step++)
        {
            float[] a = small.Process(Scene(1, step)).ToArray();
            float[] b = large.Process(Scene(4, step)).ToArray();
            for (int i = 0; i < a.Length; i++)
            {
                Assert.True(Math.Abs(a[i] - b[i]) < 1e-4, $"step {step}: {small.Names[i]} is {a[i]} at 384x216 but {b[i]} at 1536x864");
            }
        }

        // And the comparison was not vacuous: the scene moved and the HUD patch was learned.
        float[] last = small.Process(Scene(1, 5)).ToArray();
        Assert.True(last[IndexOf(small, "motion.energy")] > 0);
        Assert.True(last[IndexOf(small, "region.box.activity")] > 0);
        Assert.True(last[IndexOf(small, "hud.coverage")] > 0);
    }

    [Fact]
    public void A_region_signal_refuses_a_frame_of_another_size()
    {
        var signal = new RegionSignal(Regions, AnalysisFormat.Default);
        Assert.Equal((384, 216), (signal.Width, signal.Height));
        VideoFrame frame = Scene(2, 0);
        var luma = new LumaPlane(frame.Width, frame.Height);
        luma.Compute(frame);
        Assert.Throws<ArgumentException>(() => signal.Process(frame, luma, new float[signal.Names.Count]));
    }

    private static int IndexOf(VideoSignalBank bank, string name) => bank.Names.ToList().IndexOf(name);
}
