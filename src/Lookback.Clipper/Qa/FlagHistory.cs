namespace Lookback.Clipper.Qa;

/// <summary>
/// A bounded, time-ordered history of up to two yes/no flags per sample
/// (black and frozen per video frame; silent per audio hop), with running
/// counts so the share of flagged samples in any window is two binary
/// searches. Preallocated; adding a sample allocates nothing.
/// </summary>
internal sealed class FlagHistory
{
    private readonly long[] _t;
    private readonly long[] _cumA;
    private readonly long[] _cumB;
    private readonly byte[] _flags;
    private long _total; // samples ever added; sample k lives at k % capacity
    private long _sumA;
    private long _sumB;

    public FlagHistory(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _t = new long[capacity];
        _cumA = new long[capacity];
        _cumB = new long[capacity];
        _flags = new byte[capacity];
    }

    public long Total => _total;

    /// <summary>Adds a sample; times must not go backwards.</summary>
    public void Add(long t, bool a, bool b)
    {
        int i = (int)(_total % _t.Length);
        if (_total > 0 && t < _t[(int)((_total - 1) % _t.Length)])
        {
            return; // out of order (a stalled stream resuming); the history stays sorted
        }

        _sumA += a ? 1 : 0;
        _sumB += b ? 1 : 0;
        _t[i] = t;
        _cumA[i] = _sumA;
        _cumB[i] = _sumB;
        _flags[i] = (byte)((a ? 1 : 0) | (b ? 2 : 0));
        _total++;
    }

    /// <summary>Samples with <paramref name="start"/> ≤ t ≤ <paramref name="end"/> still held, and how many had each flag.</summary>
    public (long Samples, long A, long B) Count(long start, long end)
    {
        long oldest = Math.Max(0, _total - _t.Length);
        long first = LowerBound(oldest, start);
        long last = LowerBound(oldest, end + 1) - 1;
        if (last < first)
        {
            return (0, 0, 0);
        }

        int f = (int)(first % _t.Length), l = (int)(last % _t.Length);
        long a = _cumA[l] - _cumA[f] + (_flags[f] & 1);
        long b = _cumB[l] - _cumB[f] + ((_flags[f] >> 1) & 1);
        return (last - first + 1, a, b);
    }

    /// <summary>The first sample index in [oldest, total) whose time is ≥ <paramref name="t"/>.</summary>
    private long LowerBound(long oldest, long t)
    {
        long lo = oldest, hi = _total;
        while (lo < hi)
        {
            long mid = lo + ((hi - lo) / 2);
            if (_t[(int)(mid % _t.Length)] < t)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }
}
