using Lookback.Contracts.V1;
using Lookback.Core.Bus;
using Lookback.Core.Time;
using Lookback.Media.Frames;
using Lookback.Media.Live;

namespace Lookback.Signals.Tests;

public class LiveTests
{
    private const long Freq = 10_000_000;

    [Fact]
    public void Sample_clock_tracks_a_drifting_jittery_device()
    {
        var clock = new SampleClock(48_000, Freq);
        var rng = new Random(3);
        double actualRate = 48_000 * 1.0003; // the device crystal runs 300 ppm fast
        double worst = 0;
        for (long pos = 0; pos < 48_000L * 60; pos += 480)
        {
            double trueQpc = 5_000_000 + (pos / actualRate * Freq);
            long reported = (long)(trueQpc + ((rng.NextDouble() - 0.5) * 2 * 20_000)); // ±2 ms jitter
            long smoothed = clock.Observe(pos, reported);
            if (pos > 48_000 * 5)
            {
                worst = Math.Max(worst, Math.Abs(smoothed - trueQpc));
            }
        }

        Assert.Equal(1, clock.Resets);
        Assert.True(worst < 5_000, $"worst error {worst / 10_000.0:F2} ms"); // under 0.5 ms
        Assert.InRange(clock.MeasuredRate, actualRate - 3, actualRate + 3);
    }

    [Fact]
    public void Sample_clock_resets_on_a_jump_or_discontinuity()
    {
        var clock = new SampleClock(48_000, Freq);
        clock.Observe(0, 0);
        clock.Observe(480, 100_000);
        Assert.Equal(5_100_000, clock.Observe(960, 5_100_000)); // 500 ms jump: trust it
        clock.Observe(1440, 5_200_000, discontinuity: true);
        Assert.Equal(3, clock.Resets);
    }

    [Fact]
    public void Assembler_resamples_downmixes_and_fills_gaps_with_silence()
    {
        var asm = new LiveAudioAssembler(44_100);
        var blocks = new List<(long Qpc, float Peak)>();
        float[] packet = new float[441 * 2]; // 10 ms stereo at 44.1 kHz
        long qpc = 1_000_000;
        void Feed(int packets, float value)
        {
            for (int p = 0; p < packets; p++)
            {
                Array.Fill(packet, value);
                asm.Add(packet, 2, qpc, b => blocks.Add((b.Qpc, b.Samples.ToArray().Max())));
                qpc += 100_000;
            }
        }

        Feed(100, 0.5f);           // 1 s of sound
        qpc += 5_000_000;          // 0.5 s where loopback delivered nothing
        Feed(100, 0.5f);

        Assert.InRange(blocks.Count, 248, 252); // 2.5 s of 10 ms hops
        for (int i = 1; i < blocks.Count; i++)
        {
            Assert.Equal(100_000, blocks[i].Qpc - blocks[i - 1].Qpc); // continuous hops
        }

        Assert.Contains(blocks, b => b.Peak == 0f);
        Assert.InRange(asm.SilenceInserted, 23_500, 24_500);
        Assert.Equal(0.5f, blocks[50].Peak, 3);
    }

    [Fact]
    public async Task Live_analyzer_thins_to_the_analysis_rate_and_publishes_with_capture_time()
    {
        var bus = new InProcessEventBus();
        using IEventSubscription sub = bus.Subscribe("signals.*", 100_000);
        var clock = new ManualClock(Freq);
        var analyzer = new LiveAnalyzer(bus, "live-1", clock);
        AnalysisFormat f = analyzer.Format;
        byte[] frame = new byte[f.Width * f.Height * 4];
        int accepted = 0;
        for (int i = 0; i < 120; i++) // 2 s of 60 fps capture
        {
            long qpc = 1000 + (i * Freq / 60);
            frame.AsSpan().Fill((byte)(i * 2));
            if (analyzer.SubmitVideo(frame, f.Width * 4, qpc))
            {
                accepted++;
            }

            await Task.Delay(1, TestContext.Current.CancellationToken);
        }

        var block = new AudioBlock(48_000, 480) { Count = 480, Qpc = 1000 };
        Assert.True(analyzer.SubmitAudio(block));
        await analyzer.DisposeAsync();

        Assert.InRange(accepted, 58, 62);
        Assert.Equal(accepted, analyzer.ProcessedVideo + analyzer.DroppedVideo);
        var frames = new List<Envelope>();
        while (sub.TryRead(out Envelope? e))
        {
            frames.Add(e!);
        }

        Assert.Equal(2, frames.Count(e => e.SignalSchema is not null));
        List<Envelope> video = [.. frames.Where(e => e.SignalFrame is { SchemaId: 1 })];
        Assert.Equal(analyzer.ProcessedVideo, video.Count);
        Assert.Equal(1000UL, video[0].SignalFrame.WindowStartQpc);
        Assert.Single(frames, e => e.SignalFrame is { SchemaId: 2 });
    }
}
