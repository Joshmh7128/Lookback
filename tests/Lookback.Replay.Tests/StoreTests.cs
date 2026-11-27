using Lookback.Contracts.V1;
using Lookback.Replay.Mp4;
using Lookback.Replay.Packets;
using Lookback.Replay.Store;
using Microsoft.Extensions.Time.Testing;

namespace Lookback.Replay.Tests;

public sealed class StoreTests : IDisposable
{
    private const long Freq = 10_000_000;
    private readonly string _root = Directory.CreateTempSubdirectory("lookback-store-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static SessionStarted Owned() => new() { SourceKind = SourceKind.LiveGame, Retention = Retention.Owned, QpcFrequency = Freq, Title = "t" };

    /// <summary>Synthetic 30 fps video with a keyframe every second, plus AAC-sized audio.</summary>
    private static void Feed(ReplayRing ring, double fromSeconds, double toSeconds)
    {
        // Interleaved in time order, as two encoders feeding one ring would be.
        long frame = Freq / 30;
        long aac = 1024 * Freq / 48_000;
        long v = (long)Math.Ceiling(fromSeconds * 30);
        long n = (long)Math.Ceiling(fromSeconds * 48_000 / 1024);
        long end = (long)(toSeconds * Freq);
        while (true)
        {
            long tv = v * Freq / 30, ta = n * 1024 * Freq / 48_000;
            if (tv >= end && ta >= end)
            {
                break;
            }

            if (tv <= ta && tv < end)
            {
                ring.OnPacket(new PacketInfo(1, tv, tv, frame, v % 30 == 0), new byte[1000]);
                v++;
            }
            else
            {
                ring.OnPacket(new PacketInfo(2, ta, ta, aac, true), new byte[100]);
                n++;
            }
        }
    }

    private static ReplayRing NewRing()
    {
        var ring = new ReplayRing(1 << 26, 0);
        ring.OnTrack(TrackInfo.Video(1, CodecKind.H264, 320, 180) with { CodecConfig = [1, 0x64, 0, 0x1F, 0xFF, 0xE0, 0, 0] });
        ring.OnTrack(TrackInfo.AacAudio(2, 48_000, 2));
        return ring;
    }

    [Fact]
    public void Ephemeral_sessions_create_nothing()
    {
        SegmentStore store = SegmentStore.Open(new StoreOptions { Root = _root });
        using ReplayRing ring = NewRing();
        var viewer = new SessionStarted { SourceKind = SourceKind.OfficialPlayer, Retention = Retention.Ephemeral, QpcFrequency = Freq };
        Assert.Throws<InvalidOperationException>(() => store.BeginSession("s1", viewer, 0, ring));
        var bad = new SessionStarted { SourceKind = SourceKind.OfficialPlayer, Retention = Retention.Owned, QpcFrequency = Freq };
        Assert.Throws<InvalidOperationException>(() => store.BeginSession("s2", bad, 0, ring));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_root, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Segments_are_cut_on_keyframes_and_clips_read_back_from_disk()
    {
        SegmentStore store = SegmentStore.Open(new StoreOptions { Root = _root });
        using ReplayRing ring = NewRing();
        SessionRecording rec = store.BeginSession("s1", Owned(), 0, ring);
        for (int s = 0; s < 10; s++)
        {
            Feed(ring, s, s + 1);
            rec.Pump();
        }

        await rec.DisposeAsync();
        Assert.Equal(5, rec.SegmentsWritten); // 2 s each
        Assert.Equal(0, rec.Missed);
        Assert.True(File.Exists(Path.Combine(rec.Folder, "init-001.mp4")));

        foreach (string seg in Directory.GetFiles(rec.Folder, "*.m4s"))
        {
            List<FragmentSample> samples = Mp4FragmentReader.ReadSamples(File.ReadAllBytes(seg));
            Assert.True(samples.First(x => x.TrackId == 1).IsSync);
            Assert.Equal(60, samples.Count(x => x.TrackId == 1));
        }

        Assert.True(store.TryReadClip("s1", (long)(3.5 * Freq), 6 * Freq, out ClipSource clip));
        EncodedPacket first = clip.Packets.Where(p => p.TrackId == 1).MinBy(p => p.DtsQpc)!;
        Assert.True(first.IsKeyframe);
        Assert.Equal(2 * Freq, first.DtsQpc, tolerance: 200); // segment start: 2 s keyframe
        Assert.Equal(120, clip.Packets.Count(p => p.TrackId == 1));

        string path = store.SaveClip("s1", clip, (long)(3.5 * Freq), 6 * Freq);
        Assert.True(File.Exists(path));
        Assert.Single(SessionManifest.Load(rec.Folder).Pins);
    }

    [Fact]
    public async Task Budget_evicts_oldest_but_keeps_pins()
    {
        long free = long.MaxValue;
        SegmentStore store = SegmentStore.Open(new StoreOptions { Root = _root, BudgetBytes = 300_000, FreeSpaceProbe = () => free });
        using ReplayRing ring = NewRing();
        SessionRecording rec = store.BeginSession("s1", Owned(), 0, ring);
        Feed(ring, 0, 4);
        rec.Pump();
        Assert.True(store.TryReadClip("s1", 0, Freq, out ClipSource clip));
        store.SaveClip("s1", clip, 0, Freq);

        Feed(ring, 4, 20);
        rec.Pump();
        await rec.DisposeAsync();

        Assert.True(store.TotalBytes <= 300_000);
        Assert.True(File.Exists(Path.Combine(rec.Folder, "seg-000001.m4s")), "the pinned first segment survives");
        Assert.False(File.Exists(Path.Combine(rec.Folder, "seg-000002.m4s")), "the oldest unpinned one goes first");
        Assert.True(File.Exists(Path.Combine(rec.Folder, "seg-000010.m4s")));

        SegmentStore reopened = SegmentStore.Open(store.Options);
        Assert.Equal(store.TotalBytes, reopened.TotalBytes);
    }

    [Fact]
    public async Task Sweeper_cleans_ended_sessions_after_the_grace_period()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-11-27T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        SegmentStore store = SegmentStore.Open(new StoreOptions { Root = _root, Grace = TimeSpan.FromHours(24) });
        using ReplayRing ring = NewRing();
        SessionRecording a = store.BeginSession("a", Owned(), 0, ring, time);
        Feed(ring, 0, 6);
        await a.DisposeAsync();

        using ReplayRing ring2 = NewRing();
        SessionRecording b = store.BeginSession("b", Owned(), 0, ring2, time);
        Feed(ring2, 0, 6);
        b.Pump();
        Assert.True(store.TryReadClip("b", 0, Freq, out ClipSource clip));
        store.SaveClip("b", clip, 0, Freq);
        await b.DisposeAsync();

        time.Advance(TimeSpan.FromHours(23));
        Assert.Equal(0, CacheSweeper.Sweep(store, time).SegmentsDeleted);

        time.Advance(TimeSpan.FromHours(2));
        SweepResult r = CacheSweeper.Sweep(store, time);
        Assert.Equal(1, r.SessionsRemoved);              // "a" had nothing pinned
        Assert.False(Directory.Exists(Path.Combine(store.SessionsRoot, "a")));
        Assert.True(File.Exists(Path.Combine(store.SessionsRoot, "b", "seg-000001.m4s")));
        Assert.False(File.Exists(Path.Combine(store.SessionsRoot, "b", "seg-000002.m4s")));
        Assert.Single(Directory.GetFiles(store.ClipsRoot)); // clips are never swept
    }
}
