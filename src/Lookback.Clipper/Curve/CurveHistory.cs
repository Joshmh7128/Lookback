namespace Lookback.Clipper.Curve;

/// <summary>
/// A bounded, time-ordered history of the curve: per video frame, the curve
/// value, the active <c>high</c> threshold and each component's clamped
/// z-score. Read once per finished candidate to summarise its window
/// (<see cref="CandidateFeatures"/>). Preallocated; adding allocates nothing.
/// </summary>
internal sealed class CurveHistory
{
    private readonly long[] _t;
    private readonly float[] _rows; // stride = 2 + components: curve, high, z…
    private readonly int _stride;
    private long _total;

    public CurveHistory(int capacity, int components)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _t = new long[capacity];
        _stride = 2 + components;
        _rows = new float[capacity * _stride];
    }

    public void Add(long t, float curve, float high, ReadOnlySpan<float> z)
    {
        if (_total > 0 && t < _t[(int)((_total - 1) % _t.Length)])
        {
            return; // keep it sorted
        }

        int i = (int)(_total % _t.Length);
        _t[i] = t;
        Span<float> row = _rows.AsSpan(i * _stride, _stride);
        row[0] = curve;
        row[1] = high;
        z.CopyTo(row[2..]);
        _total++;
    }

    /// <summary>
    /// Summarises the frames with <paramref name="start"/> ≤ t ≤ <paramref name="end"/>:
    /// the mean and maximum of the curve and of each component, and the share
    /// of frames at or above <c>high</c>. Returns the number of frames.
    /// </summary>
    public int Summarise(long start, long end, out float curveMean, out float curveMax, out float aboveHigh, Span<float> zMean, Span<float> zMax)
    {
        zMean.Clear();
        zMax.Clear();
        curveMean = curveMax = aboveHigh = 0;
        long oldest = Math.Max(0, _total - _t.Length);
        int n = 0;
        double sum = 0;
        int above = 0;
        for (long k = oldest; k < _total; k++)
        {
            int i = (int)(k % _t.Length);
            if (_t[i] < start)
            {
                continue;
            }

            if (_t[i] > end)
            {
                break;
            }

            ReadOnlySpan<float> row = _rows.AsSpan(i * _stride, _stride);
            sum += row[0];
            curveMax = n == 0 ? row[0] : Math.Max(curveMax, row[0]);
            above += row[0] >= row[1] ? 1 : 0;
            for (int c = 0; c < zMean.Length; c++)
            {
                zMean[c] += row[2 + c];
                zMax[c] = Math.Max(zMax[c], row[2 + c]);
            }

            n++;
        }

        if (n > 0)
        {
            curveMean = (float)(sum / n);
            aboveHigh = above / (float)n;
            for (int c = 0; c < zMean.Length; c++)
            {
                zMean[c] /= n;
            }
        }

        return n;
    }
}
