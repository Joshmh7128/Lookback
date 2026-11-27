using Lookback.Media.Frames;

namespace Lookback.Signals.Video;

/// <summary>
/// 8-bit luma (BT.709 weights) for one analysis frame, plus a 32-bin
/// histogram. Computed once per frame by <see cref="VideoSignalBank"/> and
/// shared by every extractor, so no extractor converts colour itself.
/// </summary>
public sealed class LumaPlane
{
    public const int HistogramBins = 32;

    private readonly byte[] _luma;
    private readonly int[] _histogram = new int[HistogramBins];

    public LumaPlane(int width, int height)
    {
        Width = width;
        Height = height;
        _luma = new byte[width * height];
    }

    public int Width { get; }

    public int Height { get; }

    public int PixelCount => _luma.Length;

    public ReadOnlySpan<byte> Values => _luma;

    public ReadOnlySpan<int> Histogram => _histogram;

    /// <summary>Mean luma in [0, 1].</summary>
    public float Mean { get; private set; }

    /// <summary>Standard deviation of luma in [0, 1].</summary>
    public float StdDev { get; private set; }

    public void Compute(VideoFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.Width != Width || frame.Height != Height)
        {
            throw new ArgumentException("Frame size does not match the luma plane.", nameof(frame));
        }

        Array.Clear(_histogram);
        ReadOnlySpan<byte> bgra = frame.Pixels;
        long sum = 0, sumSq = 0;
        for (int i = 0, p = 0; i < _luma.Length; i++, p += 4)
        {
            // BT.709 in 16.16 fixed point: 0.0722 B + 0.7152 G + 0.2126 R (weights sum to 65536).
            int y = ((4732 * bgra[p]) + (46871 * bgra[p + 1]) + (13933 * bgra[p + 2]) + 32768) >> 16;
            _luma[i] = (byte)y;
            _histogram[y >> 3]++;
            sum += y;
            sumSq += y * y;
        }

        double n = _luma.Length;
        double mean = sum / n;
        double variance = Math.Max(0, (sumSq / n) - (mean * mean));
        Mean = (float)(mean / 255.0);
        StdDev = (float)(Math.Sqrt(variance) / 255.0);
    }
}
