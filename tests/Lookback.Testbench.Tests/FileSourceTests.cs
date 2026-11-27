using Lookback.Media.Ffmpeg;
using Lookback.Media.Frames;

namespace Lookback.Testbench.Tests;

public class FileSourceTests(FootageFixture fixture) : IClassFixture<FootageFixture>
{
    [Fact]
    public async Task ProbeReadsStreams()
    {
        MediaInfo info = await MediaProbe.ProbeAsync(fixture.File, TestContext.Current.CancellationToken);
        Assert.Equal(320, info.Width);
        Assert.Equal(180, info.Height);
        Assert.Equal(25, info.FramesPerSecond, 1);
        Assert.True(info.HasVideo);
        Assert.True(info.HasAudio);
        Assert.InRange(info.Duration.TotalSeconds, 2.9, 3.2);
    }

    [Fact]
    public async Task DecodesConstantRateFramesWithExactTimestamps()
    {
        var format = new AnalysisFormat { Width = 96, Height = 54, FramesPerSecond = 10 };
        var source = new FfmpegFileSource(fixture.File, format, frequency: 1000);

        var times = new List<long>();
        await foreach (VideoFrame f in source.ReadVideoAsync(TestContext.Current.CancellationToken))
        {
            Assert.Equal(96, f.Width);
            Assert.Equal(54, f.Height);
            times.Add(f.Qpc);
        }

        Assert.InRange(times.Count, 29, 31); // 3 s at 10 fps
        Assert.Equal(Enumerable.Range(0, times.Count).Select(i => (long)i * 100), times); // frame N at N/fps
    }

    [Fact]
    public async Task DecodesAudioInHops()
    {
        var source = new FfmpegFileSource(fixture.File, AnalysisFormat.Default, frequency: 48_000);
        long hops = 0, lastFirst = -1;
        float peak = 0;
        await foreach (AudioBlock b in source.ReadAudioAsync(TestContext.Current.CancellationToken))
        {
            Assert.True(b.FirstSample > lastFirst);
            Assert.Equal(b.FirstSample, b.Qpc); // frequency equals the sample rate here
            lastFirst = b.FirstSample;
            foreach (float s in b.Samples)
            {
                peak = Math.Max(peak, Math.Abs(s));
            }

            hops++;
        }

        Assert.InRange(hops, 295, 310); // ~3 s of 10 ms hops, plus codec padding
        Assert.InRange(peak, 0.05f, 1f);
    }

    [Fact]
    public async Task StoppingEarlyDoesNotHang()
    {
        var source = new FfmpegFileSource(fixture.File);
        await foreach (VideoFrame _ in source.ReadVideoAsync(TestContext.Current.CancellationToken))
        {
            break; // disposing the enumerator must kill ffmpeg
        }
    }

    [Fact]
    public async Task MissingFileIsReported() =>
        await Assert.ThrowsAsync<FileNotFoundException>(() => MediaProbe.ProbeAsync(Path.Combine(fixture.Directory, "nope.mp4"), TestContext.Current.CancellationToken));
}
