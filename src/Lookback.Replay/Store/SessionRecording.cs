using System.Globalization;
using System.Text.Json;
using Lookback.Core.Time;
using Lookback.Replay.Mp4;
using Lookback.Replay.Packets;

namespace Lookback.Replay.Store;

/// <summary>
/// Writes one session's segments. It reads the replay ring by sequence
/// number (so encoders only ever feed the ring), cuts a segment at the first
/// video keyframe once <see cref="StoreOptions.SegmentDuration"/> has
/// passed, and waits for audio to pass the cut so a segment's audio and
/// video line up. Call <see cref="Pump"/> from one thread (the store's
/// writer loop), or run <see cref="RunAsync"/>.
/// </summary>
public sealed class SessionRecording : IAsyncDisposable
{
    internal const string IndexFile = "segments.jsonl";

    private readonly SegmentStore _store;
    private readonly string _dir;
    private readonly SessionManifest _manifest;
    private readonly ReplayRing _ring;
    private readonly TimeProvider _time;
    private readonly long _segmentTicks;
    private readonly long _audioWaitTicks;
    private readonly List<EncodedPacket> _pending = [];
    private readonly List<EncodedPacket> _read = [];
    private readonly Dictionary<int, long> _lastDts = [];
    private Mp4Fragmenter? _fragmenter;
    private TrackInfo[] _runTracks = [];
    private long _cursor;
    private int _run;
    private int _seq;
    private bool _closed;
    private long _lastCut = long.MinValue;

    internal SessionRecording(SegmentStore store, string dir, SessionManifest manifest, ReplayRing ring, TimeProvider time)
    {
        _store = store;
        _dir = dir;
        _manifest = manifest;
        _ring = ring;
        _time = time;
        _segmentTicks = QpcTime.FromTimeSpan(store.Options.SegmentDuration, manifest.QpcFrequency);
        _audioWaitTicks = QpcTime.FromTimeSpan(TimeSpan.FromMilliseconds(500), manifest.QpcFrequency);
        _cursor = ring.HeadSequence;
    }

    public string SessionId => _manifest.Id;

    public string Folder => _dir;

    public int SegmentsWritten => _seq;

    /// <summary>Packets the ring evicted before this writer read them (should stay 0).</summary>
    public long Missed { get; private set; }

    /// <summary>Packets that arrived after the segment they belonged to was written, and were dropped.</summary>
    public long Late { get; private set; }

    /// <summary>Reads what the ring has gained and writes every segment that is complete.</summary>
    public void Pump() => PumpCore(final: false);

    /// <summary>Pumps every <paramref name="interval"/> until cancelled, then flushes and closes.</summary>
    public async Task RunAsync(TimeSpan interval, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(interval, _time);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                Pump();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Flushes the tail as a last segment and marks the session ended.</summary>
    public ValueTask DisposeAsync()
    {
        if (!_closed)
        {
            _closed = true;
            PumpCore(final: true);
            _manifest.EndedUtc = _time.GetUtcNow();
            _manifest.Save(_dir);
            _store.Closed(SessionId);
        }

        return ValueTask.CompletedTask;
    }

    private void PumpCore(bool final)
    {
        ObjectDisposedException.ThrowIf(_closed && !final, this);
        _read.Clear();
        _cursor = _ring.ReadFrom(_cursor, _read, int.MaxValue, out long missed);
        Missed += missed;
        Dictionary<int, TrackInfo> tracks = _ring.Tracks.ToDictionary(t => t.Id);

        foreach (EncodedPacket p in _read)
        {
            if (!tracks.TryGetValue(p.TrackId, out TrackInfo? track))
            {
                continue;
            }

            if (_fragmenter is null || TracksChanged(tracks))
            {
                // A run starts on a video keyframe once every track has its configuration.
                if (!track.IsVideo || !p.IsKeyframe || tracks.Values.Any(t => t.CodecConfig.Length == 0))
                {
                    if (_fragmenter is null)
                    {
                        continue;
                    }
                }
                else
                {
                    FlushAll();
                    StartRun(tracks);
                }
            }

            if (p.DtsQpc < _lastCut)
            {
                Late++;
                continue;
            }

            _pending.Add(p);
            _lastDts[p.TrackId] = p.DtsQpc;
            WriteReadySegments();
        }

        if (final)
        {
            FlushAll();
        }
    }

    private bool TracksChanged(Dictionary<int, TrackInfo> tracks) =>
        tracks.Count != _runTracks.Length || _runTracks.Any(t => !tracks.TryGetValue(t.Id, out TrackInfo? now) || !ReferenceEquals(now, t) && !Same(now, t));

    private static bool Same(TrackInfo a, TrackInfo b) =>
        a.Codec == b.Codec && a.Width == b.Width && a.Height == b.Height && a.SampleRate == b.SampleRate
        && a.Channels == b.Channels && a.CodecConfig.AsSpan().SequenceEqual(b.CodecConfig);

    private void StartRun(Dictionary<int, TrackInfo> tracks)
    {
        _run++;
        _runTracks = [.. tracks.Values.OrderBy(t => t.Id)];
        _fragmenter = new Mp4Fragmenter(_runTracks, _manifest.OriginQpc, _manifest.QpcFrequency);
        string init = string.Create(CultureInfo.InvariantCulture, $"init-{_run:D3}.mp4");
        File.WriteAllBytes(Path.Combine(_dir, init), _fragmenter.InitSegment());
        _manifest.Runs.Add(new RunInfo { Run = _run, InitFile = init, Tracks = [.. _runTracks.Select(TrackRecord.From)] });
        _manifest.Save(_dir);
    }

    private void WriteReadySegments()
    {
        while (_pending.Count > 0)
        {
            long start = _pending.Min(p => p.DtsQpc);
            HashSet<int> video = [.. _runTracks.Where(t => t.IsVideo).Select(t => t.Id)];
            EncodedPacket? cut = _pending
                .Where(p => video.Contains(p.TrackId) && p.IsKeyframe && p.DtsQpc - start >= _segmentTicks)
                .MinBy(p => p.DtsQpc);
            if (cut is null)
            {
                return;
            }

            long t = cut.DtsQpc;
            long newestVideo = _pending.Where(p => video.Contains(p.TrackId)).Max(p => p.DtsQpc);
            bool audioCaughtUp = _runTracks.Where(x => !x.IsVideo).All(x => _lastDts.TryGetValue(x.Id, out long d) && d >= t);
            if (!audioCaughtUp && newestVideo - t < _audioWaitTicks)
            {
                return;
            }

            WriteSegment(t);
            _lastCut = t;
        }
    }

    private void FlushAll()
    {
        if (_fragmenter is not null && _pending.Count > 0)
        {
            WriteSegment(long.MaxValue);
        }

        _pending.Clear();
    }

    private void WriteSegment(long cutQpc)
    {
        List<EncodedPacket> segment = [.. _pending.Where(p => p.DtsQpc < cutQpc)];
        _pending.RemoveAll(p => p.DtsQpc < cutQpc);
        if (segment.Count == 0)
        {
            return;
        }

        _seq++;
        string file = string.Create(CultureInfo.InvariantCulture, $"seg-{_seq:D6}.m4s");
        byte[] bytes = _fragmenter!.Fragment((uint)_seq, segment);
        File.WriteAllBytes(Path.Combine(_dir, file), bytes);

        long start = segment.Min(p => p.DtsQpc);
        long end = cutQpc != long.MaxValue ? cutQpc : segment.Max(p => p.DtsQpc + Math.Max(0, p.DurationQpc));
        var rec = new SegmentRecord(_run, _seq, file, start, end, bytes.Length);
        File.AppendAllText(Path.Combine(_dir, IndexFile), JsonSerializer.Serialize(rec, StoreLineJson.Default.SegmentRecord) + "\n");
        _store.Added(SessionId, _manifest.StartedUtc, rec);
    }
}
