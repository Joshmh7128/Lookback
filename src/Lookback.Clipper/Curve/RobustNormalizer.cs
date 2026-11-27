namespace Lookback.Clipper.Curve;

/// <summary>
/// Rolling robust statistics of one signal: the median and the MAD (median
/// absolute deviation) of samples taken every <c>interval</c> over a fixed
/// window, so a z-score is relative to this session's own recent past. Median
/// and MAD ignore the very spikes the clipper looks for, where a mean and
/// standard deviation would chase them.
/// <para>
/// Warm-up: until <c>warmupSamples</c> have been taken the statistics are not
/// <see cref="Ready"/> and the signal contributes nothing. After that the
/// window grows until it is full, then rolls.
/// </para>
/// Allocation-free after construction: one ring and one scratch buffer.
/// </summary>
internal sealed class RobustNormalizer
{
    /// <summary>MAD × this estimates the standard deviation of normally distributed data.</summary>
    public const float MadToSigma = 1.4826f;

    private readonly float[] _ring;
    private readonly float[] _scratch;
    private readonly long _interval;
    private readonly int _warmup;
    private int _count;
    private int _head;
    private long _next = long.MinValue;

    /// <param name="capacity">Samples in a full window.</param>
    /// <param name="warmupSamples">Samples needed before <see cref="Ready"/>.</param>
    /// <param name="interval">Ticks between samples.</param>
    public RobustNormalizer(int capacity, int warmupSamples, long interval)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(interval);
        _ring = new float[capacity];
        _scratch = new float[capacity];
        _warmup = Math.Clamp(warmupSamples, 1, capacity);
        _interval = interval;
    }

    public bool Ready => _count >= _warmup;

    public int Count => _count;

    public float Median { get; private set; }

    /// <summary>MAD scaled to a standard deviation (× 1.4826).</summary>
    public float Spread { get; private set; }

    /// <summary>The robust z-score of <paramref name="value"/>; the spread is never below <paramref name="minSpread"/>.</summary>
    public float Z(float value, float minSpread) => (value - Median) / Math.Max(Spread, minSpread);

    /// <summary>Offers a value at time <paramref name="t"/>; it is sampled if an interval has passed since the last sample.</summary>
    public void Observe(long t, float value)
    {
        if (!float.IsFinite(value) || t < _next)
        {
            return;
        }

        _next = _next == long.MinValue || t - _next >= _interval ? t + _interval : _next + _interval;
        _ring[_head] = value;
        _head = (_head + 1) % _ring.Length;
        _count = Math.Min(_count + 1, _ring.Length);
        Recompute();
    }

    private void Recompute()
    {
        Span<float> s = _scratch.AsSpan(0, _count);
        _ring.AsSpan(0, _count).CopyTo(s); // the order of the ring does not matter for a median
        float median = MedianOf(s);
        for (int i = 0; i < s.Length; i++)
        {
            s[i] = Math.Abs(_ring[i] - median);
        }

        Median = median;
        Spread = MadToSigma * MedianOf(s);
    }

    /// <summary>Median of <paramref name="s"/> (the mean of the two middle values when even); reorders it.</summary>
    internal static float MedianOf(Span<float> s)
    {
        int n = s.Length;
        if (n == 0)
        {
            return 0;
        }

        float hi = Select(s, n / 2);
        if ((n & 1) == 1)
        {
            return hi;
        }

        // After selection everything left of n/2 is ≤ hi; the lower middle is their maximum.
        float lo = s[0];
        for (int i = 1; i < n / 2; i++)
        {
            lo = Math.Max(lo, s[i]);
        }

        return (lo + hi) / 2;
    }

    /// <summary>The k-th smallest value (quickselect); partially reorders <paramref name="s"/>.</summary>
    internal static float Select(Span<float> s, int k)
    {
        int left = 0, right = s.Length - 1;
        while (left < right)
        {
            // Median of three as the pivot keeps sorted or constant input from going quadratic.
            int mid = left + ((right - left) / 2);
            float a = s[left], b = s[mid], c = s[right];
            float pivot = a < b ? (b < c ? b : (a < c ? c : a)) : (a < c ? a : (b < c ? c : b));
            int i = left, j = right;
            while (i <= j)
            {
                while (s[i] < pivot)
                {
                    i++;
                }

                while (s[j] > pivot)
                {
                    j--;
                }

                if (i <= j)
                {
                    (s[i], s[j]) = (s[j], s[i]);
                    i++;
                    j--;
                }
            }

            if (k <= j)
            {
                right = j;
            }
            else if (k >= i)
            {
                left = i;
            }
            else
            {
                return s[k];
            }
        }

        return s[k];
    }
}
