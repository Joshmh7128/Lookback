namespace Lookback.Replay.Store;

/// <summary>What a sweep removed.</summary>
public sealed record SweepResult(int SegmentsDeleted, int SessionsRemoved, long BytesFreed);

/// <summary>
/// Cleans the session store: unpinned segments of sessions that ended more
/// than <see cref="StoreOptions.Grace"/> ago are deleted, and a session
/// left with nothing pinned is removed entirely. A session that never
/// recorded its end (the app crashed) counts as ended at its last write.
/// Clips and sessions marked <c>Keep</c> are never touched. Run at launch
/// and periodically.
/// </summary>
public static class CacheSweeper
{
    public static SweepResult Sweep(SegmentStore store, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        DateTimeOffset now = (time ?? TimeProvider.System).GetUtcNow();
        int segments = 0, sessions = 0;
        long bytes = 0;
        if (!Directory.Exists(store.SessionsRoot))
        {
            return new SweepResult(0, 0, 0);
        }

        foreach (string dir in Directory.EnumerateDirectories(store.SessionsRoot))
        {
            SessionManifest m;
            try
            {
                m = SessionManifest.Load(dir);
            }
            catch (Exception e) when (e is IOException or System.Text.Json.JsonException or InvalidDataException)
            {
                continue;
            }

            DateTimeOffset ended = m.EndedUtc ?? new DateTimeOffset(Directory.GetLastWriteTimeUtc(dir), TimeSpan.Zero);
            if (m.Keep || now - ended < store.Options.Grace)
            {
                continue;
            }

            foreach (SegmentRecord rec in SegmentStore.ReadIndex(dir))
            {
                if (m.Pins.Any(p => p.Overlaps(rec.StartQpc, rec.EndQpc)))
                {
                    continue;
                }

                var file = new FileInfo(Path.Combine(dir, rec.File));
                if (file.Exists)
                {
                    long len = file.Length;
                    SegmentStore.TryDelete(file.FullName);
                    if (!File.Exists(file.FullName))
                    {
                        segments++;
                        bytes += len;
                    }
                }
            }

            if (m.Pins.Count == 0)
            {
                try
                {
                    Directory.Delete(dir, recursive: true);
                    sessions++;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                }
            }
        }

        store.Rescan();
        return new SweepResult(segments, sessions, bytes);
    }
}
