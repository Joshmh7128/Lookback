namespace Lookback.Profiles.Detection;

/// <summary>
/// Merges several timestamped streams (video frames, audio hops) into one
/// stream in time order. Each stream arrives in order, but the streams run
/// on different threads and decode at different speeds, so an item is only
/// released once every active stream has moved past its time: the
/// <b>watermark</b> is the minimum of each stream's latest timestamp.
/// <para>
/// A stream that falls more than <c>maxLag</c> behind the newest stream (for
/// example a game with no audio device) stops holding the others back.
/// </para>
/// </summary>
public sealed class TimeOrderedMerger<T>
{
    private readonly PriorityQueue<(T Item, long Seq), (long Qpc, long Seq)> _pending = new();
    private readonly Dictionary<int, long> _latest = [];
    private readonly long _maxLag;
    private long _seq;
    private long _first = long.MinValue;

    /// <param name="maxLag">Ticks after which a silent stream no longer holds back the watermark.</param>
    public TimeOrderedMerger(long maxLag)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxLag);
        _maxLag = maxLag;
    }

    public int Pending => _pending.Count;

    /// <summary>Declares a stream before its first item, so it holds the watermark from the start.</summary>
    public void Register(int stream)
    {
        _latest.TryAdd(stream, long.MinValue);
    }

    public void Add(int stream, long qpc, T item)
    {
        _latest[stream] = _latest.TryGetValue(stream, out long prev) ? Math.Max(prev, qpc) : qpc;
        if (_first == long.MinValue)
        {
            _first = qpc;
        }

        long s = _seq++;
        _pending.Enqueue((item, s), (qpc, s)); // ties keep arrival order
    }

    /// <summary>Appends every item at or below the watermark, in time order.</summary>
    public void Release(List<T> output)
    {
        ArgumentNullException.ThrowIfNull(output);
        long watermark = Watermark();
        while (_pending.TryPeek(out (T Item, long Seq) entry, out (long Qpc, long Seq) key) && key.Qpc <= watermark)
        {
            _pending.Dequeue();
            output.Add(entry.Item);
        }
    }

    /// <summary>Appends everything still pending, in time order (end of a session).</summary>
    public void Flush(List<T> output)
    {
        ArgumentNullException.ThrowIfNull(output);
        while (_pending.TryDequeue(out (T Item, long Seq) entry, out _))
        {
            output.Add(entry.Item);
        }
    }

    private long Watermark()
    {
        if (_latest.Count == 0)
        {
            return long.MinValue;
        }

        long newest = long.MinValue;
        foreach (long v in _latest.Values)
        {
            newest = Math.Max(newest, v);
        }

        long watermark = long.MaxValue;
        foreach (long v in _latest.Values)
        {
            // A stream that has sent nothing counts as being at the first item seen
            // anywhere, so one that never sends (no audio device) also stalls out.
            long at = v == long.MinValue ? _first : v;
            if (newest - at > _maxLag)
            {
                continue; // stalled stream
            }

            watermark = Math.Min(watermark, v);
        }

        return watermark == long.MaxValue ? newest : watermark;
    }
}
