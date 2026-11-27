namespace Lookback.Signals.Dsp;

/// <summary>
/// In-place iterative radix-2 FFT with precomputed twiddles and bit-reversal
/// table. Sized once; <see cref="Forward"/> allocates nothing.
/// </summary>
public sealed class Fft
{
    private readonly int[] _bitReverse;
    private readonly float[] _cos;
    private readonly float[] _sin;

    public Fft(int size)
    {
        if (size < 2 || (size & (size - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(size), "FFT size must be a power of two.");
        }

        Size = size;
        int bits = System.Numerics.BitOperations.Log2((uint)size);
        _bitReverse = new int[size];
        for (int i = 0; i < size; i++)
        {
            int r = 0;
            for (int b = 0; b < bits; b++)
            {
                r |= ((i >> b) & 1) << (bits - 1 - b);
            }

            _bitReverse[i] = r;
        }

        _cos = new float[size / 2];
        _sin = new float[size / 2];
        for (int k = 0; k < size / 2; k++)
        {
            double a = -2 * Math.PI * k / size;
            _cos[k] = (float)Math.Cos(a);
            _sin[k] = (float)Math.Sin(a);
        }
    }

    public int Size { get; }

    /// <summary>Forward transform of (<paramref name="re"/>, <paramref name="im"/>), in place.</summary>
    public void Forward(Span<float> re, Span<float> im)
    {
        if (re.Length != Size || im.Length != Size)
        {
            throw new ArgumentException("Buffers must match the FFT size.");
        }

        for (int i = 0; i < Size; i++)
        {
            int j = _bitReverse[i];
            if (j > i)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }

        for (int len = 2; len <= Size; len <<= 1)
        {
            int half = len >> 1;
            int step = Size / len;
            for (int start = 0; start < Size; start += len)
            {
                for (int k = 0; k < half; k++)
                {
                    float wr = _cos[k * step], wi = _sin[k * step];
                    int a = start + k, b = a + half;
                    float tr = (wr * re[b]) - (wi * im[b]);
                    float ti = (wr * im[b]) + (wi * re[b]);
                    re[b] = re[a] - tr;
                    im[b] = im[a] - ti;
                    re[a] += tr;
                    im[a] += ti;
                }
            }
        }
    }
}
