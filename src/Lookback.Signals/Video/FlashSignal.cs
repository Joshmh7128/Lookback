using Lookback.Media.Frames;

namespace Lookback.Signals.Video;

/// <summary>
/// Sudden brightening against a short rolling baseline (explosions, muzzle
/// flashes, ability effects, "victory" screens) and a red-edge score for the
/// red vignette many games draw when the player takes damage. Both are
/// relative measures, so they work regardless of a game's overall palette.
/// </summary>
public sealed class FlashSignal : IVideoSignal
{
    private const float BaselineAlpha = 0.1f; // ~10-frame memory at 30 fps
    private const int BorderFraction = 8;     // outer 1/8 of each side is "edge"

    private float _baseline = -1;

    public IReadOnlyList<string> Names { get; } = ["flash.brighten", "flash.red_edge"];

    public void Process(VideoFrame frame, LumaPlane luma, Span<float> output)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(luma);

        if (_baseline < 0)
        {
            _baseline = luma.Mean;
        }

        output[0] = Math.Max(0, luma.Mean - _baseline);
        _baseline += BaselineAlpha * (luma.Mean - _baseline);
        output[1] = RedEdge(frame);
    }

    /// <summary>Redness of the border minus redness of the centre, in [-1, 1].</summary>
    internal static float RedEdge(VideoFrame frame)
    {
        int w = frame.Width, h = frame.Height;
        int bx = Math.Max(1, w / BorderFraction), by = Math.Max(1, h / BorderFraction);
        ReadOnlySpan<byte> px = frame.Pixels;

        double edge = 0, centre = 0;
        int edgeCount = 0, centreCount = 0;
        for (int y = 0; y < h; y++)
        {
            bool edgeRow = y < by || y >= h - by;
            int row = y * w * 4;
            for (int x = 0; x < w; x++)
            {
                int p = row + (x * 4);
                // Redness: how far red exceeds the mean of green and blue.
                int redness = px[p + 2] - ((px[p] + px[p + 1]) >> 1);
                if (edgeRow || x < bx || x >= w - bx)
                {
                    edge += redness;
                    edgeCount++;
                }
                else
                {
                    centre += redness;
                    centreCount++;
                }
            }
        }

        double e = edgeCount == 0 ? 0 : edge / edgeCount;
        double c = centreCount == 0 ? 0 : centre / centreCount;
        return (float)((e - c) / 255.0);
    }

    public void Reset() => _baseline = -1;
}
