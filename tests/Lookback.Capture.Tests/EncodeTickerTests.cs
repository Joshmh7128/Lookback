using System.Diagnostics;
using Lookback.Capture.Gpu;
using Lookback.Core.Time;
using Lookback.Encode;
using Lookback.Replay.Packets;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Lookback.Capture.Tests;

/// <summary>
/// The CFR clock on a real adapter: frames arrive irregularly (slow, a burst,
/// then nothing, like WGC), and the encoded track must still be exactly one
/// packet per tick with constant spacing, repeats counted. Needs the video
/// processor, so it skips on WARP-only machines.
/// </summary>
public class EncodeTickerTests(ITestOutputHelper output)
{
    [Fact]
    public void Irregular_capture_becomes_constant_rate_packets()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Media Foundation is Windows only");
        GpuDevice gpu;
        try
        {
            gpu = GpuDevice.Create();
        }
        catch (InvalidOperationException)
        {
            Assert.Skip("no hardware adapter");
            return;
        }

        using (gpu)
        using (var ring = new ReplayRing(1 << 26, 0))
        using (ID3D11Texture2D bgra = gpu.Device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, 1920, 1080, 1, 1, BindFlags.ShaderResource | BindFlags.RenderTarget)))
        {
            const int fps = 30;
            SystemClock clock = SystemClock.Instance;
            var settings = new VideoEncoderSettings(CodecKind.H264, 1280, 720, fps, 6_000_000);
            EncodeStats stats;
            string name;
            using (EncodeTicker ticker = EncodeTicker.Start(gpu, settings, ring, trackId: 1, clock))
            {
                name = ticker.EncoderName;
                var sw = Stopwatch.StartNew();
                byte[] px = new byte[1920 * 1080 * 4];
                int i = 0;
                void Feed()
                {
                    px.AsSpan().Fill((byte)(i++ * 7));
                    lock (gpu.Lock)
                    {
                        gpu.Context.UpdateSubresource(px, bgra, 0, 1920 * 4, 0);
                    }

                    ticker.FrameTap(bgra, clock.Now, TransferFunction.Sdr);
                }

                while (sw.Elapsed < TimeSpan.FromSeconds(1)) { Feed(); Thread.Sleep(100); }    // 10 fps
                while (sw.Elapsed < TimeSpan.FromSeconds(1.5)) { Feed(); Thread.Sleep(4); }    // ~200 fps burst
                Thread.Sleep(700);                                                               // nothing
                stats = ticker.Stats;
            }

            output.WriteLine($"{name}: {stats}");
            var all = new List<EncodedPacket>();
            ring.ReadFrom(ring.TailSequence, all, int.MaxValue, out long missed);
            Assert.Equal(0, missed);
            List<EncodedPacket> video = [.. all.Where(p => p.TrackId == 1)];
            Assert.True(video.Count >= 60, $"only {video.Count} packets");
            Assert.True(video[0].IsKeyframe);
            long period = clock.Frequency / fps;
            for (int k = 1; k < video.Count; k++)
            {
                long d = video[k].PtsQpc - video[k - 1].PtsQpc;
                Assert.InRange(d, period - 1, period + 1);
            }

            Assert.Equal(0, stats.Skipped);
            Assert.Equal(stats.Encoded, video.Count); // every encoded tick came out of the encoder
            Assert.True(stats.Repeated >= 25, $"expected repeats in the slow and idle parts, got {stats.Repeated}");
            Assert.True(stats.Captured > stats.Ticks / 2);
            Assert.Equal(0, stats.EncoderBusy);
        }
    }
}
