using Lookback.Contracts;
using Lookback.Contracts.V1;
using Lookback.Core.Bus;
using Lookback.Core.Time;
using Lookback.Replay;
using Lookback.Replay.Packets;
using Lookback.Replay.Store;

namespace Lookback.Recorder.Tests;

/// <summary>
/// The clip cache's bookkeeping: a recorded live session's own envelopes are
/// kept in its store folder and read back after a restart, and the store keeps,
/// deletes and protects what the storage page asks for. No capture or GPU.
/// </summary>
public sealed class SessionHistoryTests : IDisposable
{
    private const long Freq = 10_000_000;
    private readonly string _root = Directory.CreateTempSubdirectory("lookback-history-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static SessionStarted Live() => new() { SourceKind = SourceKind.LiveGame, Retention = Retention.Owned, QpcFrequency = Freq, Title = "Overwatch", GameProfile = "overwatch" };

    private static Envelope Env(string session, string topic, Action<Envelope> fill)
    {
        Envelope e = new EnvelopeFactory(SystemClock.Instance, session, "test").Create(topic);
        fill(e);
        return e;
    }

    private static ReplayRing Ring()
    {
        var ring = new ReplayRing(1 << 24, 0);
        ring.OnTrack(TrackInfo.Video(1, CodecKind.H264, 320, 180) with { CodecConfig = [1, 0x64, 0, 0x1F, 0xFF, 0xE0, 0, 0] });
        return ring;
    }

    /// <summary>Six seconds of 30 fps video with a keyframe each second, pumped into the recording as it goes.</summary>
    private static void Record(ReplayRing ring, SessionRecording rec)
    {
        for (int i = 0; i < 180; i++)
        {
            long t = i * Freq / 30;
            ring.OnPacket(new PacketInfo(1, t, t, Freq / 30, i % 30 == 0), new byte[500]);
            if (i % 30 == 29)
            {
                rec.Pump();
            }
        }
    }

    [Fact]
    public async Task A_live_sessions_own_envelopes_are_kept_once_its_folder_exists_and_read_back()
    {
        SegmentStore store = SegmentStore.Open(new StoreOptions { Root = _root });
        await using var history = new SessionHistory(new InProcessEventBus(), store);
        var recorded = new List<string>();
        history.CandidateRecorded += (_, c) => recorded.Add(c.CandidateId);

        // Announced before the store has its folder: held, not lost.
        history.Observe(Env("live-1", Topics.Session, e => e.SessionStarted = Live()));
        Assert.Empty(store.ReadEvents("live-1"));

        using ReplayRing ring = Ring();
        SessionRecording rec = store.BeginSession("live-1", Live(), 0, ring);
        history.Observe(Env("live-1", Topics.ClipCandidate, e => e.ClipCandidate = new ClipCandidate { CandidateId = "c1", StartQpc = 0, EndQpc = (ulong)Freq, GameProfile = "overwatch" }));
        history.Observe(Env("live-1", Topics.SignalsVideo, e => e.SignalFrame = new SignalFrame())); // never kept
        history.Observe(Env("live-1", Topics.ClipRating, e => e.ClipRating = new ClipRating { SessionId = "live-1", CandidateId = "c1", Stars = 4 }));
        history.Observe(Env("live-1", Topics.ClipSaved, e => e.ClipSaved = new ClipSaved { CandidateId = "c1", Path = Path.Combine(_root, "clips", "x.mp4"), StartQpc = 0, EndQpc = (ulong)Freq }));
        await rec.DisposeAsync();

        IReadOnlyList<string> lines = store.ReadEvents("live-1");
        Assert.Equal(4, lines.Count);
        Assert.Contains("\"sessionStarted\"", lines[0], StringComparison.Ordinal);
        Assert.DoesNotContain(lines, l => l.Contains("signalFrame", StringComparison.Ordinal));
        Assert.Equal(["c1"], recorded);
        Assert.True(history.IsKept("live-1", "c1"));

        // A fresh history (a restarted recorder) reads them back as envelopes.
        await using var after = new SessionHistory(new InProcessEventBus(), SegmentStore.Open(new StoreOptions { Root = _root }));
        IReadOnlyList<Envelope> restored = after.Restore("live-1");
        Assert.Equal([Envelope.PayloadOneofCase.SessionStarted, Envelope.PayloadOneofCase.ClipCandidate, Envelope.PayloadOneofCase.ClipRating, Envelope.PayloadOneofCase.ClipSaved], restored.Select(e => e.PayloadCase));
        Assert.True(after.IsKept("live-1", "c1"));
        Assert.Equal("c1", after.SavedClip(Path.Combine(_root, "clips", "x.mp4"), out string? owner)!.CandidateId);
        Assert.Equal("live-1", owner);
    }

    [Fact]
    public async Task Official_player_and_footage_sessions_are_never_kept()
    {
        SegmentStore store = SegmentStore.Open(new StoreOptions { Root = _root });
        await using var history = new SessionHistory(new InProcessEventBus(), store);
        history.Observe(Env("viewer", Topics.Session, e => e.SessionStarted = new SessionStarted { SourceKind = SourceKind.OfficialPlayer, Retention = Retention.Ephemeral }));
        history.Observe(Env("viewer", Topics.ClipCandidate, e => e.ClipCandidate = new ClipCandidate { CandidateId = "c1" }));
        history.Observe(Env("file", Topics.Session, e => e.SessionStarted = new SessionStarted { SourceKind = SourceKind.File, Retention = Retention.Owned }));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_root, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task The_store_keeps_a_cached_clip_deletes_only_its_own_files_and_never_a_recording_session()
    {
        SegmentStore store = SegmentStore.Open(new StoreOptions { Root = _root });
        using ReplayRing ring = Ring();
        SessionRecording rec = store.BeginSession("live-1", Live(), 0, ring);
        Record(ring, rec);
        Assert.True(store.TryReadClip("live-1", 0, 2 * Freq, out ClipSource clip));

        // Cached, then kept: it moves to clips/ and pins its range.
        string cached = store.WritePreview("live-1", "c1", clip);
        Assert.Equal(cached, store.CachedPreview("live-1", "c1"));
        string kept = store.KeepPreview("live-1", "c1", 0, Freq)!;
        Assert.Equal(store.ClipsRoot, Path.GetDirectoryName(kept));
        Assert.False(File.Exists(cached));
        Assert.Null(store.KeepPreview("live-1", "c1", 0, Freq)); // nothing cached any more
        Assert.Single(SessionManifest.Load(rec.Folder).Pins);

        // A recording session cannot be deleted; files outside clips/ and previews/ are never touched.
        Assert.False(store.DeleteSession("live-1"));
        string outside = Path.Combine(_root, "notes.mp4");
        await File.WriteAllTextAsync(outside, "x", TestContext.Current.CancellationToken);
        Assert.False(store.DeleteFile(outside));
        Assert.True(File.Exists(outside));

        // Deleting the kept clip releases its pin.
        Assert.True(store.DeleteFile(kept, "live-1", new QpcRange(0, Freq)));
        Assert.Empty(SessionManifest.Load(rec.Folder).Pins);

        Assert.True(store.SetKeep("live-1", true));
        Assert.True(SessionManifest.Load(rec.Folder).Keep);
        await rec.DisposeAsync();

        // Once it has ended, a session goes with its cached clips; saved clips stay.
        store.WritePreview("live-1", "c2", clip);
        Assert.True(store.DeleteSession("live-1"));
        Assert.False(Directory.Exists(rec.Folder));
        Assert.Empty(Directory.EnumerateFiles(store.PreviewsRoot));
    }
}
