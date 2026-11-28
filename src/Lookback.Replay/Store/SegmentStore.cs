using System.Globalization;
using System.Text.Json;
using Lookback.Contracts;
using Lookback.Contracts.V1;
using Lookback.Core.Time;
using Lookback.Replay.Mp4;
using Lookback.Replay.Packets;

namespace Lookback.Replay.Store;

/// <summary>
/// The disk session store: every persisted session as 2 s fMP4 segments
/// under one byte budget, oldest evicted first, pinned ranges and saved
/// clips untouched. Only sessions that <see cref="RetentionRules"/> allows
/// to persist can get a folder at all. Thread-safe.
/// </summary>
public sealed class SegmentStore
{
    private readonly Lock _lock = new();
    private readonly List<StoredSegment> _segments = []; // oldest first
    private readonly Dictionary<string, SessionManifest> _manifests = new(StringComparer.Ordinal);
    private readonly HashSet<string> _open = new(StringComparer.Ordinal);
    private long _totalBytes;

    private SegmentStore(StoreOptions options)
    {
        Options = options;
    }

    public StoreOptions Options { get; private set; }

    public string SessionsRoot => Path.Combine(Options.Root, "sessions");

    public string ClipsRoot => Path.Combine(Options.Root, "clips");

    /// <summary>Temporary cuts for watching a candidate without saving it (see <see cref="WritePreview"/>).</summary>
    public string PreviewsRoot => Path.Combine(Options.Root, "previews");

    public long TotalBytes
    {
        get { lock (_lock) { return _totalBytes; } }
    }

    public int SegmentCount
    {
        get { lock (_lock) { return _segments.Count; } }
    }

    /// <summary>Opens (or creates) a store and indexes what is already on disk.</summary>
    public static SegmentStore Open(StoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var store = new SegmentStore(options);
        store.Rescan();
        return store;
    }

    /// <summary>Applies a new budget from the governor and evicts if needed.</summary>
    public void Reconfigure(long budgetBytes, long freeSpaceFloorBytes)
    {
        lock (_lock)
        {
            Options = Options with { BudgetBytes = budgetBytes, FreeSpaceFloorBytes = freeSpaceFloorBytes };
        }

        Enforce();
    }

    /// <summary>
    /// Starts recording a session from <paramref name="ring"/>.
    /// Throws <see cref="InvalidOperationException"/> before creating anything
    /// if the session may not be persisted.
    /// </summary>
    public SessionRecording BeginSession(string sessionId, SessionStarted started, long originQpc, ReplayRing ring, TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        ArgumentNullException.ThrowIfNull(started);
        ArgumentNullException.ThrowIfNull(ring);
        string? problem = RetentionRules.Validate(started);
        if (problem is not null || !RetentionRules.CanPersist(started))
        {
            throw new InvalidOperationException(problem ?? $"Session {sessionId} is ephemeral; nothing may be written to disk.");
        }

        if (sessionId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || sessionId.Contains("..", StringComparison.Ordinal))
        {
            throw new ArgumentException("Session id must be a valid file name.", nameof(sessionId));
        }

        string dir = Path.Combine(SessionsRoot, sessionId);
        if (Directory.Exists(dir))
        {
            throw new InvalidOperationException($"Session {sessionId} already exists.");
        }

        Directory.CreateDirectory(dir);
        var manifest = new SessionManifest
        {
            Id = sessionId,
            Title = started.Title,
            SourceKind = started.SourceKind,
            Retention = started.Retention,
            PermissionRef = started.PermissionRef,
            GameProfile = started.GameProfile,
            QpcFrequency = (long)started.QpcFrequency,
            OriginQpc = originQpc,
            StartedUtc = (time ?? TimeProvider.System).GetUtcNow(),
        };
        manifest.Save(dir);
        lock (_lock)
        {
            _manifests[sessionId] = manifest;
            _open.Add(sessionId);
        }

        return new SessionRecording(this, dir, manifest, ring, time ?? TimeProvider.System);
    }

    /// <summary>The sessions on disk, newest first.</summary>
    public IReadOnlyList<SessionManifest> Sessions()
    {
        lock (_lock)
        {
            return [.. _manifests.Values.OrderByDescending(m => m.StartedUtc)];
        }
    }

    /// <summary>QPC range covered by a session's segments that are still on disk.</summary>
    public QpcRange? Coverage(string sessionId)
    {
        lock (_lock)
        {
            List<StoredSegment> mine = [.. _segments.Where(s => s.SessionId == sessionId)];
            return mine.Count == 0 ? null : new QpcRange(mine.Min(s => s.Record.StartQpc), mine.Max(s => s.Record.EndQpc));
        }
    }

    /// <summary>
    /// Reads the packets for [<paramref name="fromQpc"/>, <paramref name="toQpc"/>)
    /// back from disk, starting at the segment (and so the keyframe) at or
    /// before the start. Returns false if that part of the session is gone.
    /// </summary>
    public bool TryReadClip(string sessionId, long fromQpc, long toQpc, out ClipSource clip)
    {
        clip = new ClipSource([], []);
        SessionManifest manifest;
        List<StoredSegment> segs;
        lock (_lock)
        {
            if (!_manifests.TryGetValue(sessionId, out manifest!))
            {
                return false;
            }

            List<StoredSegment> mine = [.. _segments.Where(s => s.SessionId == sessionId).OrderBy(s => s.Record.StartQpc)];
            StoredSegment? first = mine.LastOrDefault(s => s.Record.StartQpc <= fromQpc && s.Record.EndQpc > fromQpc)
                ?? mine.FirstOrDefault(s => s.Record.StartQpc >= fromQpc && s.Record.StartQpc < toQpc);
            if (first is null)
            {
                return false;
            }

            segs = [.. mine.Where(s => s.Record.Run == first.Record.Run && s.Record.StartQpc >= first.Record.StartQpc && s.Record.StartQpc < toQpc)];
        }

        RunInfo run = manifest.Runs.First(r => r.Run == segs[0].Record.Run);
        Dictionary<int, TrackInfo> tracks = run.Tracks.ToDictionary(t => t.Id, t => t.ToTrackInfo());
        long freq = manifest.QpcFrequency;
        long start = segs[0].Record.StartQpc;
        var packets = new List<EncodedPacket>();
        string dir = Path.Combine(SessionsRoot, sessionId);
        foreach (StoredSegment seg in segs)
        {
            byte[] data;
            try
            {
                data = File.ReadAllBytes(Path.Combine(dir, seg.Record.File));
            }
            catch (FileNotFoundException)
            {
                break; // evicted between the index read and now; return what is contiguous
            }

            foreach (FragmentSample s in Mp4FragmentReader.ReadSamples(data))
            {
                TrackInfo t = tracks[s.TrackId];
                long dts = manifest.OriginQpc + QpcTime.Rescale(s.DecodeTime, t.Timescale, freq);
                long pts = manifest.OriginQpc + QpcTime.Rescale(s.DecodeTime + s.CompositionOffset, t.Timescale, freq);
                long dur = QpcTime.Rescale(s.Duration, t.Timescale, freq);
                bool keep = t.IsVideo ? dts < toQpc : pts >= start && pts < toQpc;
                if (keep)
                {
                    packets.Add(new EncodedPacket(new PacketInfo(s.TrackId, pts, dts, dur, s.IsSync), data.AsSpan(s.Offset, s.Size).ToArray()));
                }
            }
        }

        clip = new ClipSource([.. tracks.Values.OrderBy(t => t.Id)], packets);
        return packets.Count > 0;
    }

    /// <summary>
    /// Writes a clip file and pins its range in the session so the sweeper
    /// keeps those segments. Returns the clip path.
    /// </summary>
    public string SaveClip(string sessionId, ClipSource clip, long fromQpc, long toQpc)
    {
        ArgumentNullException.ThrowIfNull(clip);
        SessionManifest manifest;
        lock (_lock)
        {
            if (!_manifests.TryGetValue(sessionId, out manifest!))
            {
                throw new InvalidOperationException($"Unknown session {sessionId}.");
            }
        }

        if (!RetentionRules.CanPersist(manifest.ToSessionStarted()))
        {
            throw new InvalidOperationException($"Session {sessionId} may not be persisted.");
        }

        byte[] bytes = ClipWriter.Mux(clip, manifest.QpcFrequency);
        Directory.CreateDirectory(ClipsRoot);
        string path;
        for (int n = 1; ; n++)
        {
            path = Path.Combine(ClipsRoot, string.Create(CultureInfo.InvariantCulture, $"{sessionId}-{n:D3}.mp4"));
            try
            {
                using var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
                fs.Write(bytes);
                break;
            }
            catch (IOException) when (File.Exists(path))
            {
            }
        }

        lock (_lock)
        {
            manifest.Pins.Add(new QpcRange(fromQpc, toQpc));
            manifest.Save(Path.Combine(SessionsRoot, sessionId));
        }

        return path;
    }

    /// <summary>
    /// Writes a temporary preview of a candidate's window, so it can be watched
    /// without saving it, and returns its path. A preview is not a saved clip: it
    /// pins no segments, uses no clips budget, and is reused while it exists.
    /// Previews not watched for <see cref="StoreOptions.PreviewMaxAge"/>, and the
    /// least recently watched past <see cref="StoreOptions.PreviewBudgetBytes"/>,
    /// are deleted whenever one is written.
    /// </summary>
    public string WritePreview(string sessionId, string candidateId, ClipSource clip)
    {
        ArgumentNullException.ThrowIfNull(clip);
        SessionManifest manifest;
        lock (_lock)
        {
            if (!_manifests.TryGetValue(sessionId, out manifest!))
            {
                throw new InvalidOperationException($"Unknown session {sessionId}.");
            }
        }

        if (!RetentionRules.CanPersist(manifest.ToSessionStarted()))
        {
            throw new InvalidOperationException($"Session {sessionId} may not be persisted.");
        }

        Directory.CreateDirectory(PreviewsRoot);
        string path = PreviewPath(sessionId, candidateId);
        if (File.Exists(path))
        {
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow); // watched again: kept longest
            return path;
        }

        byte[] bytes = ClipWriter.Mux(clip, manifest.QpcFrequency);
        string part = path + ".part";
        File.WriteAllBytes(part, bytes);
        File.Move(part, path, overwrite: true); // whole or not at all
        TrimPreviews(path, DateTime.UtcNow);
        return path;
    }

    /// <summary>Deletes old previews and the least recently watched past the budget, keeping <paramref name="keep"/>.</summary>
    internal void TrimPreviews(string? keep, DateTime nowUtc)
    {
        if (!Directory.Exists(PreviewsRoot))
        {
            return;
        }

        string? kept = keep is null ? null : Path.GetFullPath(keep);
        long total = 0;
        foreach (FileInfo f in new DirectoryInfo(PreviewsRoot).GetFiles("*.mp4").OrderByDescending(f => f.LastWriteTimeUtc))
        {
            bool isKept = string.Equals(f.FullName, kept, StringComparison.OrdinalIgnoreCase);
            if (!isKept && (nowUtc - f.LastWriteTimeUtc > Options.PreviewMaxAge || total + f.Length > Options.PreviewBudgetBytes))
            {
                TryDelete(f.FullName);
                continue;
            }

            total += f.Length;
        }

        foreach (string part in Directory.EnumerateFiles(PreviewsRoot, "*.part"))
        {
            TryDelete(part); // previews are written one at a time; a leftover is from a crash
        }
    }

    /// <summary>Letters, digits, dot, dash and underscore; anything else becomes an underscore.</summary>
    private static string SafeName(string id)
    {
        Span<char> chars = id.Length <= 96 ? stackalloc char[id.Length] : new char[96];
        for (int i = 0; i < chars.Length; i++)
        {
            char c = id[i];
            chars[i] = char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_';
        }

        return new string(chars);
    }

    /// <summary>A session's own envelopes, one JSON line each (see <see cref="AppendEvent"/>).</summary>
    public const string EventsFileName = "events.jsonl";

    /// <summary>
    /// Appends one line to <c>sessions/&lt;id&gt;/events.jsonl</c>: the session's
    /// own envelopes (its start and end, candidates, QA reports, ratings,
    /// saved clips; never signal frames), so its candidates can be listed,
    /// previewed and saved after a restart. False when the session has no
    /// folder (not recorded, or already swept).
    /// </summary>
    public bool AppendEvent(string sessionId, string jsonLine)
    {
        ArgumentNullException.ThrowIfNull(jsonLine);
        string dir = Path.Combine(SessionsRoot, sessionId);
        lock (_lock)
        {
            if (!_manifests.ContainsKey(sessionId) || !Directory.Exists(dir))
            {
                return false;
            }

            File.AppendAllText(Path.Combine(dir, EventsFileName), jsonLine.ReplaceLineEndings(" ") + "\n");
            return true;
        }
    }

    /// <summary>The lines <see cref="AppendEvent"/> wrote for a session, in order; empty when there are none.</summary>
    public IReadOnlyList<string> ReadEvents(string sessionId)
    {
        string path = Path.Combine(SessionsRoot, sessionId, EventsFileName);
        lock (_lock)
        {
            return File.Exists(path) ? [.. File.ReadLines(path).Where(l => l.Length > 0)] : [];
        }
    }

    /// <summary>The cached preview of a candidate, when one exists.</summary>
    public string? CachedPreview(string sessionId, string candidateId)
    {
        string path = PreviewPath(sessionId, candidateId);
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// Keeps a candidate's cached preview as a saved clip: moves it into
    /// <c>clips/</c> and pins its range, like <see cref="SaveClip"/> without
    /// cutting it again. Null when there is no cached preview.
    /// </summary>
    public string? KeepPreview(string sessionId, string candidateId, long fromQpc, long toQpc)
    {
        string source = PreviewPath(sessionId, candidateId);
        SessionManifest manifest;
        lock (_lock)
        {
            if (!File.Exists(source) || !_manifests.TryGetValue(sessionId, out manifest!))
            {
                return null;
            }
        }

        if (!RetentionRules.CanPersist(manifest.ToSessionStarted()))
        {
            throw new InvalidOperationException($"Session {sessionId} may not be persisted.");
        }

        Directory.CreateDirectory(ClipsRoot);
        string stem = ClipStem(sessionId, candidateId);
        string path = Path.Combine(ClipsRoot, stem + ".mp4");
        for (int n = 2; File.Exists(path); n++)
        {
            path = Path.Combine(ClipsRoot, string.Create(CultureInfo.InvariantCulture, $"{stem}-{n}.mp4"));
        }

        File.Move(source, path);
        lock (_lock)
        {
            manifest.Pins.Add(new QpcRange(fromQpc, toQpc));
            manifest.Save(Path.Combine(SessionsRoot, sessionId));
        }

        return path;
    }

    /// <summary>Marks a session to keep (the sweeper and the budget leave it alone) or to sweep as usual.</summary>
    public bool SetKeep(string sessionId, bool keep)
    {
        lock (_lock)
        {
            if (!_manifests.TryGetValue(sessionId, out SessionManifest? m))
            {
                return false;
            }

            m.Keep = keep;
            m.Save(Path.Combine(SessionsRoot, sessionId));
            return true;
        }
    }

    /// <summary>
    /// Deletes a recorded session: its footage, its candidates' cached
    /// previews, and its folder. Saved clips stay. Refused (false) while the
    /// session is still recording.
    /// </summary>
    public bool DeleteSession(string sessionId)
    {
        lock (_lock)
        {
            if (_open.Contains(sessionId) || !_manifests.ContainsKey(sessionId))
            {
                return false;
            }
        }

        string prefix = SafeName(sessionId) + "-";
        if (Directory.Exists(PreviewsRoot))
        {
            foreach (string f in Directory.EnumerateFiles(PreviewsRoot, prefix + "*.mp4"))
            {
                TryDelete(f);
            }
        }

        try
        {
            Directory.Delete(Path.Combine(SessionsRoot, sessionId), recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Rescan();
            return false;
        }

        Rescan();
        return true;
    }

    /// <summary>
    /// Deletes a file the store made: a saved clip in <c>clips/</c> (and the
    /// pin it held, when its range is given) or a cached preview. Anything
    /// else is refused (false).
    /// </summary>
    public bool DeleteFile(string path, string? sessionId = null, QpcRange? pin = null)
    {
        string full = Path.GetFullPath(path);
        string? dir = Path.GetDirectoryName(full);
        bool ours = string.Equals(dir, Path.GetFullPath(ClipsRoot), StringComparison.OrdinalIgnoreCase)
            || string.Equals(dir, Path.GetFullPath(PreviewsRoot), StringComparison.OrdinalIgnoreCase);
        if (!ours || !full.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
        {
            return false;
        }

        TryDelete(full);
        if (sessionId is not null && pin is { } range)
        {
            lock (_lock)
            {
                if (_manifests.TryGetValue(sessionId, out SessionManifest? m) && m.Pins.Remove(range))
                {
                    m.Save(Path.Combine(SessionsRoot, sessionId));
                }
            }
        }

        return !File.Exists(full);
    }

    /// <summary>Bytes on disk of one session's folder (footage and its own files).</summary>
    public long SessionBytes(string sessionId)
    {
        string dir = Path.Combine(SessionsRoot, sessionId);
        return Directory.Exists(dir) ? new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) : 0;
    }

    private string PreviewPath(string sessionId, string candidateId) =>
        Path.Combine(PreviewsRoot, ClipStem(sessionId, candidateId) + ".mp4");

    /// <summary>A clip's file name: the candidate id, prefixed with the session's unless it already starts with it.</summary>
    private static string ClipStem(string sessionId, string candidateId) =>
        candidateId.StartsWith(sessionId, StringComparison.Ordinal) ? SafeName(candidateId) : $"{SafeName(sessionId)}-{SafeName(candidateId)}";

    /// <summary>Re-reads every session folder. Called at open; cheap enough to call after a sweep.</summary>
    public void Rescan()
    {
        lock (_lock)
        {
            _segments.Clear();
            _manifests.Clear();
            _totalBytes = 0;
            if (!Directory.Exists(SessionsRoot))
            {
                return;
            }

            foreach (string dir in Directory.EnumerateDirectories(SessionsRoot))
            {
                SessionManifest manifest;
                try
                {
                    manifest = SessionManifest.Load(dir);
                }
                catch (Exception e) when (e is IOException or JsonException or InvalidDataException)
                {
                    continue; // not ours, or torn; the sweeper reports it
                }

                _manifests[manifest.Id] = manifest;
                foreach (SegmentRecord rec in ReadIndex(dir))
                {
                    var info = new FileInfo(Path.Combine(dir, rec.File));
                    if (info.Exists)
                    {
                        _segments.Add(new StoredSegment(manifest.Id, manifest.StartedUtc, rec));
                        _totalBytes += info.Length;
                    }
                }
            }

            _segments.Sort((a, b) => a.Started != b.Started ? a.Started.CompareTo(b.Started) : a.Record.Seq.CompareTo(b.Record.Seq));
        }
    }

    internal static IEnumerable<SegmentRecord> ReadIndex(string dir)
    {
        string path = Path.Combine(dir, SessionRecording.IndexFile);
        if (!File.Exists(path))
        {
            yield break;
        }

        foreach (string line in File.ReadLines(path))
        {
            SegmentRecord? rec = null;
            try
            {
                rec = JsonSerializer.Deserialize(line, StoreLineJson.Default.SegmentRecord);
            }
            catch (JsonException)
            {
                // a torn last line after a crash
            }

            if (rec is not null)
            {
                yield return rec;
            }
        }
    }

    internal void Added(string sessionId, DateTimeOffset started, SegmentRecord rec)
    {
        lock (_lock)
        {
            _segments.Add(new StoredSegment(sessionId, started, rec));
            _totalBytes += rec.Bytes;
        }

        Enforce();
    }

    internal void Closed(string sessionId)
    {
        lock (_lock)
        {
            _open.Remove(sessionId);
        }
    }

    /// <summary>Evicts the oldest unpinned segments until the budget and free-space floor hold.</summary>
    public int Enforce()
    {
        int evicted = 0;
        while (true)
        {
            StoredSegment? victim;
            lock (_lock)
            {
                bool over = _totalBytes > Options.BudgetBytes || FreeSpace() < Options.FreeSpaceFloorBytes;
                if (!over)
                {
                    return evicted;
                }

                victim = _segments.FirstOrDefault(s => !IsPinned(s) && !IsNewestOfOpenSession(s));
                if (victim is null)
                {
                    return evicted;
                }

                _segments.Remove(victim);
                _totalBytes -= victim.Record.Bytes;
            }

            TryDelete(Path.Combine(SessionsRoot, victim.SessionId, victim.Record.File));
            evicted++;
        }
    }

    internal bool IsPinned(StoredSegment s) =>
        _manifests.TryGetValue(s.SessionId, out SessionManifest? m)
        && (m.Keep || m.Pins.Any(p => p.Overlaps(s.Record.StartQpc, s.Record.EndQpc)));

    private bool IsNewestOfOpenSession(StoredSegment s) =>
        _open.Contains(s.SessionId) && ReferenceEquals(s, _segments.LastOrDefault(x => x.SessionId == s.SessionId));

    private long FreeSpace()
    {
        if (Options.FreeSpaceProbe is not null)
        {
            return Options.FreeSpaceProbe();
        }

        try
        {
            return new DriveInfo(Path.GetFullPath(Options.Root)).AvailableFreeSpace;
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return long.MaxValue;
        }
    }

    internal static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // In use by a reader; the next sweep gets it.
        }
    }

    internal sealed record StoredSegment(string SessionId, DateTimeOffset Started, SegmentRecord Record);
}
