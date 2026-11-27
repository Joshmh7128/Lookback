namespace Lookback.Profiles.Detection;

/// <summary>
/// Merges several timestamped streams (video frames, audio hops) into one
/// stream in time order. Each stream arrives in order, but the streams run
/// on different threads and decode at different speeds, so an item is only
/// released once every active stream has moved past its time: the
/// <b>watermark</b> is the minimum of each stream's latest timestamp.
/// <para>
/// The output depends only on timestamps, never on how far apart the streams
/// arrive: a replay at 8x, where audio may run seconds of footage behind
/// video, releases the same sequence as one at 1x. The only exception is a
/// stream that has delivered nothing for <c>stallTimeout</c> of <i>wall</i>
/// time (for example a game with no audio device): it stops holding the
/// others back until it sends again.
/// </para>
/// </summary>
public sealed class TimeOrderedMerger<T>
{
    private readonly PriorityQueue<(T Item, long Seq), (long Qpc, long Seq)> _pending = new();
    private readonly Dictionary<int, (long Latest, long LastArrival)> _streams = [];
    private readonly long _stallTimeout;
    private readonly Func<long> _wallClock;
    private long _seq;

    /// <param name="stallTimeout">Wall-clock ticks (of <paramref name="wallClock"/>) after which a silent stream no longer holds back the watermark.</param>
    /// <param name="wallClock">Wall-clock source; defaults to <see cref="System.Diagnostics.Stopwatch.GetTimestamp"/>.</param>
    public TimeOrderedMerger(long stallTimeout, Func<long>? wallClock = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(stallTimeout);
        _stallTimeout = stallTimeout;
        _wallClock = wallClock ?? System.Diagnostics.Stopwatch.GetTimestamp;
    }

    /// <summary>A stall timeout of the given wall-clock duration, measured with <see cref="System.Diagnostics.Stopwatch"/>.</summary>
    public TimeOrderedMerger(TimeSpan stallTimeout)
        : this((long)(stallTimeout.TotalSeconds * System.Diagnostics.Stopwatch.Frequency))
    {
    }

    public int Pending => _pending.Count;

    /// <summary>Declares a stream before its first item, so it holds the watermark from the start.</summary>
    public void Register(int stream)
    {
        _streams.TryAdd(stream, (long.MinValue, _wallClock()));
    }

    public void Add(int stream, long qpc, T item)
    {
        long latest = _streams.TryGetValue(stream, out var s) ? Math.Max(s.Latest, qpc) : qpc;
        _streams[stream] = (latest, _wallClock());
        long seq = _seq++;
        _pending.Enqueue((item, seq), (qpc, seq)); // ties keep arrival order
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
        long now = _wallClock();
        long watermark = long.MaxValue;
        foreach ((long latest, long lastArrival) in _streams.Values)
        {
            if (now - lastArrival > _stallTimeout)
            {
                continue; // stalled stream
            }

            watermark = Math.Min(watermark, latest);
        }

        return watermark == long.MaxValue ? long.MaxValue : watermark;
    }
}
