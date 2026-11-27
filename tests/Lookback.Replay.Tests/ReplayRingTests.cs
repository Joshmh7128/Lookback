using Lookback.Replay.Packets;

namespace Lookback.Replay.Tests;

public class ReplayRingTests
{
    private const long Freq = 10_000_000;
    private static readonly TrackInfo Video = TrackInfo.Video(1, CodecKind.H264, 1920, 1080) with { CodecConfig = [1] };
    private static readonly TrackInfo Audio = TrackInfo.AacAudio(2, 48_000, 2);

    private static void Feed(ReplayRing ring, int frames, int size, int gop = 30, long start = 0)
    {
        byte[] payload = new byte[size];
        for (int i = 0; i < frames; i++)
        {
            long t = start + (i * Freq / 30);
            payload[0] = (byte)i;
            ring.OnPacket(new PacketInfo(1, t, t, Freq / 30, i % gop == 0), payload);
        }
    }

    [Fact]
    public void Evicts_oldest_when_bytes_run_out_and_keeps_data_intact_across_wraps()
    {
        using var ring = new ReplayRing(capacityBytes: 10_000, maxDurationQpc: 0);
        ring.OnTrack(Video);
        Feed(ring, frames: 100, size: 1000);

        Assert.True(ring.BytesUsed <= 10_000);
        Assert.Equal(100, ring.HeadSequence);
        var packets = new List<EncodedPacket>();
        ring.ReadFrom(0, packets, 1000, out long missed);
        Assert.Equal(ring.TailSequence, missed);
        Assert.All(packets, p => Assert.Equal((byte)Math.Round(p.DtsQpc * 30.0 / Freq), p.Payload[0]));
        Assert.Equal(99, packets[^1].Payload[0]);
    }

    [Fact]
    public void Odd_sizes_wrap_without_corruption()
    {
        using var ring = new ReplayRing(capacityBytes: 5000, maxDurationQpc: 0);
        ring.OnTrack(Video);
        var rng = new Random(7);
        for (int i = 0; i < 2000; i++)
        {
            byte[] p = new byte[rng.Next(1, 1500)];
            p.AsSpan().Fill((byte)i);
            ring.OnPacket(new PacketInfo(1, i, i, 1, i % 10 == 0), p);
        }

        var packets = new List<EncodedPacket>();
        ring.ReadFrom(0, packets, 10_000, out _);
        Assert.NotEmpty(packets);
        Assert.All(packets, p => Assert.All(p.Payload, b => Assert.Equal((byte)p.DtsQpc, b)));
        Assert.Equal(1999, packets[^1].DtsQpc);
    }

    [Fact]
    public void Duration_cap_bounds_the_span()
    {
        using var ring = new ReplayRing(capacityBytes: 1 << 24, maxDurationQpc: 5 * Freq);
        ring.OnTrack(Video);
        Feed(ring, frames: 30 * 20, size: 100);
        Assert.InRange(ring.SpanQpc, 4 * Freq, 5 * Freq);
    }

    [Fact]
    public void Snapshot_starts_at_the_keyframe_before_the_range_and_includes_audio()
    {
        using var ring = new ReplayRing(capacityBytes: 1 << 24, maxDurationQpc: 0);
        ring.OnTrack(Video);
        ring.OnTrack(Audio);
        Feed(ring, frames: 300, size: 100);
        long aacTicks = 1024 * Freq / 48_000;
        for (long t = 0; t < 10 * Freq; t += aacTicks)
        {
            ring.OnPacket(new PacketInfo(2, t, t, aacTicks, true), new byte[50]);
        }

        Assert.True(ring.TrySnapshot(fromQpc: (long)(4.5 * Freq), toQpc: 6 * Freq, out ClipSource clip));
        EncodedPacket first = clip.Packets.Where(p => p.TrackId == 1).MinBy(p => p.DtsQpc)!;
        Assert.True(first.IsKeyframe);
        Assert.Equal(4 * Freq, first.DtsQpc);
        Assert.All(clip.Packets, p => Assert.InRange(p.PtsQpc, 4 * Freq, (6 * Freq) - 1));
        Assert.Contains(clip.Packets, p => p.TrackId == 2);
    }

    [Fact]
    public void Snapshot_fails_when_the_start_has_left_ram()
    {
        using var ring = new ReplayRing(capacityBytes: 1 << 24, maxDurationQpc: 2 * Freq);
        ring.OnTrack(Video);
        Feed(ring, frames: 300, size: 100);
        Assert.False(ring.TrySnapshot(0, Freq, out _));
    }

    [Fact]
    public void Reconfigure_keeps_the_newest_packets()
    {
        using var ring = new ReplayRing(capacityBytes: 100_000, maxDurationQpc: 0);
        ring.OnTrack(Video);
        Feed(ring, frames: 90, size: 1000);
        ring.Reconfigure(20_000, 0);
        Assert.True(ring.BytesUsed <= 20_000);
        var packets = new List<EncodedPacket>();
        ring.ReadFrom(ring.TailSequence, packets, 1000, out _);
        Assert.Equal(89, packets[^1].Payload[0]);
        Feed(ring, frames: 10, size: 1000, start: 3 * Freq);
        Assert.True(ring.BytesUsed <= 20_000);
    }

    [Fact]
    public void Append_does_not_allocate()
    {
        using var ring = new ReplayRing(capacityBytes: 1 << 20, maxDurationQpc: Freq);
        ring.OnTrack(Video);
        byte[] payload = new byte[3000];
        for (int i = 0; i < 2000; i++)
        {
            ring.OnPacket(new PacketInfo(1, i, i, 1, i % 30 == 0), payload); // warm up / tier up
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 2000; i < 12_000; i++)
        {
            ring.OnPacket(new PacketInfo(1, i * 1000L, i * 1000L, 1000, i % 30 == 0), payload);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
