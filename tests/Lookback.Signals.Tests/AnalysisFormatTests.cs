using Lookback.Core.Bus;
using Lookback.Core.Time;
using Lookback.Media.Frames;

namespace Lookback.Signals.Tests;

public class AnalysisFormatTests
{
    [Theory]
    [InlineData("768x432", 768, 432)]
    [InlineData("1280X720", 1280, 720)]
    [InlineData(" 1920x1080 ", 1920, 1080)]
    [InlineData("2560x1440", 2560, 1440)]
    [InlineData("default", 384, 216)]
    [InlineData("", 384, 216)]
    [InlineData(null, 384, 216)]
    public void Parses_sizes_and_keeps_rate_and_audio(string? text, int w, int h)
    {
        Assert.True(AnalysisFormat.TryParse(text, out AnalysisFormat? f, out string? error), error);
        Assert.Equal(w, f!.Width);
        Assert.Equal(h, f.Height);
        Assert.Equal(AnalysisFormat.Default.FramesPerSecond, f.FramesPerSecond);
        Assert.Equal(AnalysisFormat.Default.SampleRate, f.SampleRate);
        Assert.Equal(AnalysisFormat.Default.HopSamples, f.HopSamples);
        Assert.Equal($"{w}x{h}", f.Size);
        Assert.Equal(w * h, f.PixelCount);
    }

    [Fact]
    public void The_default_size_is_the_default_instance()
    {
        Assert.Same(AnalysisFormat.Default, AnalysisFormat.Parse("384x216"));
        Assert.True(AnalysisFormat.Default.IsDefaultSize);
        Assert.False(AnalysisFormat.Parse("768x432").IsDefaultSize);
    }

    [Theory]
    [InlineData("768")]
    [InlineData("x432")]
    [InlineData("768x")]
    [InlineData("-768x432")]
    [InlineData("768x432x2")]
    [InlineData("769x432")]   // odd
    [InlineData("768x431")]
    [InlineData("32x18")]     // too small
    [InlineData("8000x4500")] // too large
    [InlineData("native")]    // only the testbench knows the footage's size
    public void Refuses_bad_sizes(string text)
    {
        Assert.False(AnalysisFormat.TryParse(text, out AnalysisFormat? f, out string? error));
        Assert.Null(f);
        Assert.False(string.IsNullOrEmpty(error));
        Assert.Throws<ArgumentException>(() => AnalysisFormat.Parse(text));
    }

    [Fact]
    public void With_size_validates_and_keeps_the_rest()
    {
        AnalysisFormat f = (AnalysisFormat.Default with { FramesPerSecond = 15 }).WithSize(640, 360);
        Assert.Equal((640, 360, 15), (f.Width, f.Height, f.FramesPerSecond));
        Assert.Throws<ArgumentException>(() => AnalysisFormat.Default.WithSize(641, 360));
    }

    [Fact]
    public async Task Live_analyzer_runs_at_a_larger_size_and_times_its_work()
    {
        var bus = new InProcessEventBus();
        using IEventSubscription sub = bus.Subscribe("signals.video", 1000);
        AnalysisFormat f = AnalysisFormat.Parse("768x432");
        var analyzer = new LiveAnalyzer(bus, "live-big", new ManualClock(10_000_000), f);
        Assert.Same(f, analyzer.Format);

        // The default size is now too small for the analyzer.
        byte[] small = new byte[AnalysisFormat.Default.PixelCount * 4];
        Assert.Throws<ArgumentException>(() => analyzer.SubmitVideo(small, AnalysisFormat.Default.Width * 4, 1));

        // Padded rows (a staging texture's pitch) are accepted.
        int pitch = (f.Width * 4) + 64;
        byte[] frame = new byte[pitch * f.Height];
        for (int i = 0; i < 4; i++)
        {
            frame.AsSpan().Fill((byte)(i * 40));
            Assert.True(analyzer.SubmitVideo(frame, pitch, 1_000_000 + (i * 333_334)));
            var wait = System.Diagnostics.Stopwatch.StartNew();
            while (analyzer.ProcessedVideo < i + 1 && wait.Elapsed < TimeSpan.FromSeconds(5))
            {
                Thread.Yield();
            }
        }

        await analyzer.DisposeAsync();
        Assert.Equal(4, analyzer.ProcessedVideo);
        Assert.True(analyzer.VideoBusy > TimeSpan.Zero);
        Assert.True(analyzer.VideoBusyMax > TimeSpan.Zero && analyzer.VideoBusyMax <= analyzer.VideoBusy);
    }
}
