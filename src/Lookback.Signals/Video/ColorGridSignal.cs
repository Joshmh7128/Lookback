using System.Globalization;
using Lookback.Media.Frames;

namespace Lookback.Signals.Video;

/// <summary>
/// A coarse colour signature of the whole frame: the mean red, green and
/// blue of each cell of a <see cref="Columns"/> × <see cref="Rows"/> grid, in
/// 0..1. Gameplay changes it all the time; a menu, a loading screen or a
/// scoreboard holds a layout of its own (dark band across the middle, a
/// bright panel on the left, a blurred backdrop), so a game profile can name
/// those screens by their signatures (<c>menus</c> in the profile) without
/// any new kind of measurement. Cells are about square on a 16:9 frame (64 × 54
/// pixels of the 384 × 216 analysis frame).
/// <para>
/// Values are named <c>color.r&lt;row&gt;c&lt;column&gt;.&lt;r|g|b&gt;</c>, row-major
/// from the top left (<see cref="Names"/>). Allocation-free per frame.
/// </para>
/// </summary>
public sealed class ColorGridSignal : IVideoSignal
{
    public const int Columns = 6;
    public const int Rows = 4;
    public const int Cells = Columns * Rows;
    public const int Channels = 3;

    /// <summary>Values per frame: three channels per cell.</summary>
    public const int Length = Cells * Channels;

    private readonly int _width;
    private readonly int _height;
    private readonly int[] _cellOfColumn;
    private readonly int[] _cellOfRow;
    private readonly long[] _sums = new long[Length];
    private readonly int[] _pixelsPerCell = new int[Cells];

    public ColorGridSignal(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, Columns);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, Rows);
        _width = width;
        _height = height;
        _cellOfColumn = new int[width];
        _cellOfRow = new int[height];
        for (int x = 0; x < width; x++)
        {
            _cellOfColumn[x] = x * Columns / width;
        }

        for (int y = 0; y < height; y++)
        {
            _cellOfRow[y] = y * Rows / height * Columns;
        }

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                _pixelsPerCell[_cellOfRow[y] + _cellOfColumn[x]]++;
            }
        }
    }

    /// <summary>Every value's name, row-major, red, green, blue per cell.</summary>
    public static IReadOnlyList<string> AllNames { get; } = [.. Enumerable.Range(0, Cells).SelectMany(c => "rgb".Select(ch => Name(c, ch)))];

    public IReadOnlyList<string> Names => AllNames;

    /// <summary>The name of one value: <paramref name="cell"/> is row-major, <paramref name="channel"/> is <c>r</c>, <c>g</c> or <c>b</c>.</summary>
    public static string Name(int cell, char channel)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(cell);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(cell, Cells);
        return string.Create(CultureInfo.InvariantCulture, $"color.r{cell / Columns}c{cell % Columns}.{channel}");
    }

    public void Process(VideoFrame frame, LumaPlane luma, Span<float> output)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.Width != _width || frame.Height != _height)
        {
            throw new ArgumentException("Frame size does not match the colour grid.", nameof(frame));
        }

        Array.Clear(_sums);
        ReadOnlySpan<byte> px = frame.Pixels;
        for (int y = 0, p = 0; y < _height; y++)
        {
            int rowCell = _cellOfRow[y];
            for (int x = 0; x < _width; x++, p += 4)
            {
                int s = (rowCell + _cellOfColumn[x]) * Channels;
                _sums[s] += px[p + 2];     // BGRA: red
                _sums[s + 1] += px[p + 1]; // green
                _sums[s + 2] += px[p];     // blue
            }
        }

        for (int c = 0; c < Cells; c++)
        {
            double n = 255.0 * _pixelsPerCell[c];
            int s = c * Channels;
            output[s] = (float)(_sums[s] / n);
            output[s + 1] = (float)(_sums[s + 1] / n);
            output[s + 2] = (float)(_sums[s + 2] / n);
        }
    }

    public void Reset()
    {
    }
}
