using Lookback.Media.Frames;
using Lookback.Profiles.Model;
using Lookback.Signals.Video;

namespace Lookback.Profiles.Regions;

/// <summary>
/// The game-agnostic measurements, taken inside each of a profile's HUD
/// regions. For a region named <c>kill_feed</c> it produces
/// <c>region.kill_feed.luma</c>, <c>.activity</c> (mean absolute luma change
/// since the last frame), <c>.saturation</c> and <c>.red</c>. Rules then
/// react to, say, activity rising in the kill feed. Allocation-free per frame.
/// </summary>
public sealed class RegionSignal : IVideoSignal
{
    public const int ValuesPerRegion = 4;

    private readonly (int X0, int Y0, int X1, int Y1)[] _rects;
    private readonly byte[][] _previous;
    private readonly int _width;
    private readonly int _height;
    private bool _hasPrevious;

    public RegionSignal(IReadOnlyList<HudRegion> regions, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(regions);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        _width = width;
        _height = height;
        _rects = new (int, int, int, int)[regions.Count];
        _previous = new byte[regions.Count][];
        var names = new List<string>(regions.Count * ValuesPerRegion);

        for (int i = 0; i < regions.Count; i++)
        {
            HudRegion r = regions[i];
            int x0 = Math.Clamp((int)Math.Floor(r.X * width), 0, width - 1);
            int y0 = Math.Clamp((int)Math.Floor(r.Y * height), 0, height - 1);
            int x1 = Math.Clamp((int)Math.Ceiling((r.X + r.W) * width), x0 + 1, width);
            int y1 = Math.Clamp((int)Math.Ceiling((r.Y + r.H) * height), y0 + 1, height);
            _rects[i] = (x0, y0, x1, y1);
            _previous[i] = new byte[(x1 - x0) * (y1 - y0)];
            names.Add($"region.{r.Name}.luma");
            names.Add($"region.{r.Name}.activity");
            names.Add($"region.{r.Name}.saturation");
            names.Add($"region.{r.Name}.red");
        }

        Names = names;
    }

    /// <summary>A region signal for frames of <paramref name="format"/>'s size.</summary>
    public RegionSignal(IReadOnlyList<HudRegion> regions, AnalysisFormat format)
        : this(regions, (format ?? throw new ArgumentNullException(nameof(format))).Width, format.Height)
    {
    }

    public IReadOnlyList<string> Names { get; }

    /// <summary>The frame size the rectangles were laid out for.</summary>
    public int Width => _width;

    public int Height => _height;

    public void Process(VideoFrame frame, LumaPlane luma, Span<float> output)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(luma);
        if (frame.Width != _width || frame.Height != _height)
        {
            // Rectangles are in pixels of the size given at construction; another size would read the wrong area.
            throw new ArgumentException($"Region signal laid out for {_width}x{_height} got a {frame.Width}x{frame.Height} frame.", nameof(frame));
        }

        ReadOnlySpan<byte> px = frame.Pixels;
        ReadOnlySpan<byte> y = luma.Values;
        int w = frame.Width;

        for (int i = 0; i < _rects.Length; i++)
        {
            (int x0, int y0, int x1, int y1) = _rects[i];
            byte[] prev = _previous[i];
            long lumaSum = 0, change = 0, satSum = 0, redSum = 0;
            int k = 0;
            for (int row = y0; row < y1; row++)
            {
                for (int col = x0; col < x1; col++, k++)
                {
                    int idx = (row * w) + col;
                    byte l = y[idx];
                    lumaSum += l;
                    change += Math.Abs(l - prev[k]);
                    prev[k] = l;

                    int p = idx * 4;
                    int b = px[p], g = px[p + 1], r = px[p + 2];
                    satSum += Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b));
                    redSum += Math.Max(0, r - ((g + b) >> 1));
                }
            }

            double n = 255.0 * k;
            Span<float> o = output.Slice(i * ValuesPerRegion, ValuesPerRegion);
            o[0] = (float)(lumaSum / n);
            o[1] = _hasPrevious ? (float)(change / n) : 0;
            o[2] = (float)(satSum / n);
            o[3] = (float)(redSum / n);
        }

        _hasPrevious = true;
    }

    public void Reset() => _hasPrevious = false;
}
