using Lookback.Media.Frames;
using Lookback.Signals.Video;

namespace Lookback.Signals.Tests;

public class VideoSignalTests
{
    private static float[] Run(IVideoSignal signal, VideoFrame frame)
    {
        var luma = new LumaPlane(frame.Width, frame.Height);
        luma.Compute(frame);
        float[] output = new float[signal.Names.Count];
        signal.Process(frame, luma, output);
        return output;
    }

    [Fact]
    public void LumaUsesBt709Weights()
    {
        var luma = new LumaPlane(Synth.W, Synth.H);
        luma.Compute(Synth.Solid(255, 255, 255));
        // Luma is stored in 8 bits, so allow one code value (1/255) of quantisation.
        const double tolerance = 1.0 / 255;
        Assert.Equal(1.0, luma.Mean, tolerance);
        luma.Compute(Synth.Solid(0, 255, 0));
        Assert.Equal(0.7152, luma.Mean, tolerance); // green dominates luma
        luma.Compute(Synth.Solid(0, 0, 255));
        Assert.Equal(0.0722, luma.Mean, tolerance);
        Assert.Equal(0f, luma.StdDev, 3);
    }

    [Fact]
    public void BlackFrameIsFlagged()
    {
        var s = new LumaStatsSignal();
        Assert.Equal(1f, Run(s, Synth.Solid(4, 4, 4))[2]);
        Assert.Equal(0f, Run(s, Synth.Solid(128, 128, 128))[2]);
    }

    [Fact]
    public void MotionMeasuresChangeAndCountsFrozenTime()
    {
        var s = new MotionSignal(framesPerSecond: 10);
        var rng = new Random(1);
        VideoFrame f = Synth.Solid(0, 0, 0);
        Synth.Noise(f, rng);
        Assert.Equal(0f, Run(s, f)[0]); // first frame has no predecessor

        Synth.Noise(f, rng);
        float[] moving = Run(s, f);
        Assert.True(moving[0] > 0.1f, $"energy {moving[0]}");
        Assert.True(moving[1] > 0.5f, $"coverage {moving[1]}");

        float[] frozen = [];
        for (int i = 0; i < 5; i++)
        {
            frozen = Run(s, f); // identical frames
        }

        Assert.Equal(0f, frozen[0]);
        Assert.Equal(0.5f, frozen[2], 3); // 5 frames at 10 fps
    }

    [Fact]
    public void FlashFiresOnSuddenBrighteningOnly()
    {
        var s = new FlashSignal();
        for (int i = 0; i < 20; i++)
        {
            Assert.Equal(0f, Run(s, Synth.Solid(60, 60, 60))[0]);
        }

        Assert.True(Run(s, Synth.Solid(230, 230, 230))[0] > 0.5f);
        Assert.Equal(0f, Run(s, Synth.Solid(20, 20, 20))[0]); // darkening is not a flash
    }

    [Fact]
    public void RedEdgeDetectsDamageVignette()
    {
        VideoFrame f = Synth.Solid(80, 80, 80);
        Assert.Equal(0f, FlashSignal.RedEdge(f), 3);

        // Paint the border red, leave the centre grey.
        Synth.FillRect(f, 0, 0, Synth.W, 4, 220, 30, 30);
        Synth.FillRect(f, 0, Synth.H - 4, Synth.W, 4, 220, 30, 30);
        Synth.FillRect(f, 0, 0, 8, Synth.H, 220, 30, 30);
        Synth.FillRect(f, Synth.W - 8, 0, 8, Synth.H, 220, 30, 30);
        Assert.True(FlashSignal.RedEdge(f) > 0.5f);
    }

    [Fact]
    public void SceneCutSeparatesCutsFromSmallChanges()
    {
        var s = new SceneCutSignal();
        VideoFrame f = Synth.Solid(30, 30, 30);
        Run(s, f);
        Synth.FillRect(f, 0, 0, 4, 4, 200, 200, 200); // small change
        Assert.True(Run(s, f)[0] < 0.05f);
        Assert.True(Run(s, Synth.Solid(220, 220, 220))[0] > 0.9f); // whole picture changed
    }

    [Fact]
    public void HudMaskFindsStaticOverlayAndWatchesIt()
    {
        var rng = new Random(7);
        var s = new HudSignal(Synth.W * Synth.H, warmupFrames: 30);
        VideoFrame f = Synth.Solid(0, 0, 0);

        // Gameplay noise everywhere, a static bright "health bar" at the bottom left.
        float[] o = [];
        for (int i = 0; i < 30; i++)
        {
            Synth.Noise(f, rng);
            Synth.FillRect(f, 2, 30, 20, 4, 40, 200, 40);
            o = Run(s, f);
        }

        Assert.True(s.IsReady);
        float expected = 20f * 4 / (Synth.W * Synth.H);
        Assert.Equal(expected, o[1], 3); // the mask is exactly the bar

        // The bar drops: activity inside the mask jumps, while noise outside is ignored.
        Synth.Noise(f, rng);
        Synth.FillRect(f, 2, 30, 20, 4, 40, 200, 40);
        float steady = Run(s, f)[2];
        Synth.Noise(f, rng);
        Synth.FillRect(f, 2, 30, 20, 4, 40, 200, 40);
        Synth.FillRect(f, 12, 30, 10, 4, 20, 20, 20);
        float dropped = Run(s, f)[2];
        Assert.Equal(0f, steady, 4);
        Assert.True(dropped > 0.2f, $"activity {dropped}");
    }

    [Fact]
    public void HudIgnoresLetterboxBars()
    {
        var rng = new Random(3);
        var s = new HudSignal(Synth.W * Synth.H, warmupFrames: 10);
        VideoFrame f = Synth.Solid(0, 0, 0);
        float[] o = [];
        for (int i = 0; i < 10; i++)
        {
            Synth.Noise(f, rng);
            Synth.FillRect(f, 0, 0, Synth.W, 4, 0, 0, 0); // black bar: static but not HUD
            o = Run(s, f);
        }

        Assert.Equal(0f, o[1]);
    }

    [Fact]
    public void BankConcatenatesSignalsInOrder()
    {
        var format = new AnalysisFormat { Width = Synth.W, Height = Synth.H, FramesPerSecond = 30 };
        VideoSignalBank bank = VideoSignalBank.CreateDefault(format);
        Assert.Equal(bank.Names.Count, bank.Process(Synth.Solid(10, 10, 10)).Length);
        Assert.Equal("luma.mean", bank.Names[0]);
        Assert.Contains("hud.activity", bank.Names);
        Assert.Equal(bank.Names.Count, bank.Names.Distinct().Count());
    }

    [Fact]
    public void BankDoesNotAllocatePerFrame()
    {
        var format = new AnalysisFormat { Width = 384, Height = 216, FramesPerSecond = 30 };
        VideoSignalBank bank = VideoSignalBank.CreateDefault(format);
        VideoFrame f = Synth.Solid(0, 0, 0, 384, 216);
        var rng = new Random(5);
        for (int i = 0; i < 5; i++)
        {
            Synth.Noise(f, rng);
            bank.Process(f); // warm up: first-frame buffers
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 50; i++)
        {
            bank.Process(f);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
