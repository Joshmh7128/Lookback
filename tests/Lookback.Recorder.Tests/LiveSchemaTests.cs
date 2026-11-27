using System.Runtime.InteropServices;
using Lookback.Clipper;
using Lookback.Contracts;
using Lookback.Contracts.V1;
using Lookback.Core.Bus;
using Lookback.Core.Time;
using Lookback.Media.Frames;
using Lookback.Signals;

namespace Lookback.Recorder.Tests;

/// <summary>
/// The live recorder's ordering, without a GPU or a game: the analyzer names
/// its signals as capture starts, and the profile runner and clipper subscribe
/// after it. Until the analyzer names them again they cannot read a frame
/// (no game events, no candidates); after <see cref="LiveAnalyzer.PublishSchemas"/>
/// they can, which is what <c>CaptureService</c> now does once they are in place.
/// </summary>
public sealed class LiveSchemaTests
{
    [Fact]
    public async Task A_clipper_created_after_the_analyzer_reads_frames_only_once_the_schemas_are_published_again()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using var bus = new InProcessEventBus();
        using IEventSubscription curve = bus.Subscribe(Topics.SignalsClipper, capacity: 1 << 14);
        SystemClock clock = SystemClock.Instance;
        await using var analyzer = new LiveAnalyzer(bus, "live-1", clock); // names its signals now
        await using var clipper = new ClipperRunner(bus, "live-1", clock.Frequency); // subscribes after

        AnalysisFormat f = analyzer.Format;
        byte[] frame = new byte[f.Width * f.Height * 4];
        var hop = new AudioBlock(f.SampleRate, f.HopSamples) { Count = f.HopSamples };
        long origin = clock.Now, sample = 0;
        int index = 0;
        var rng = new Random(3);

        async Task FeedAsync(int frames)
        {
            for (int i = 0; i < frames; i++, index++)
            {
                rng.NextBytes(frame);
                long qpc = origin + QpcTime.Rescale(index, f.FramesPerSecond, clock.Frequency);
                analyzer.SubmitVideo(frame, f.Width * 4, qpc);
                for (long until = (index + 1L) * f.SampleRate / f.FramesPerSecond; sample + f.HopSamples <= until; sample += f.HopSamples)
                {
                    Span<float> s = MemoryMarshal.CreateSpan(ref MemoryMarshal.GetReference(hop.Samples), hop.Count);
                    for (int k = 0; k < s.Length; k++)
                    {
                        s[k] = 0.05f * (float)((rng.NextDouble() * 2) - 1);
                    }

                    hop.FirstSample = sample;
                    hop.Qpc = origin + QpcTime.Rescale(sample, f.SampleRate, clock.Frequency);
                    analyzer.SubmitAudio(hop);
                }

                await Task.Delay(2, ct); // let the analysis worker keep up
            }

            await Task.Delay(500, ct);
        }

        static int CurveFrames(IEventSubscription sub)
        {
            int n = 0;
            while (sub.TryRead(out Envelope? e))
            {
                n += e!.PayloadCase == Envelope.PayloadOneofCase.SignalFrame ? 1 : 0;
            }

            return n;
        }

        // The schemas went out before the clipper subscribed: it cannot name a single frame.
        await FeedAsync(60);
        Assert.Equal(0, CurveFrames(curve));

        // Named again: from here on the clipper reads the signals and publishes its curve. The
        // analysis worker runs below normal priority, so on a busy machine keep feeding and wait
        // for the curve (up to 10 s) rather than trusting one fixed delay.
        analyzer.PublishSchemas();
        int read = 0;
        for (var waited = System.Diagnostics.Stopwatch.StartNew(); read == 0 && waited.Elapsed < TimeSpan.FromSeconds(10);)
        {
            await FeedAsync(30);
            read += CurveFrames(curve);
        }

        Assert.True(read > 0, "the clipper still cannot read the signals");
    }
}
