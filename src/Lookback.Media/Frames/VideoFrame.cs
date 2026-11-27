namespace Lookback.Media.Frames;

/// <summary>
/// One downscaled analysis frame in BGRA8 (the layout D3D11 and WGC produce).
/// Frames are <b>borrowed</b>: a source reuses the same buffer for the next
/// frame, so consumers must finish with a frame before asking for another and
/// must not keep a reference to <see cref="Pixels"/>.
/// </summary>
public sealed class VideoFrame
{
    public VideoFrame(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        Width = width;
        Height = height;
        Buffer = new byte[width * height * BytesPerPixel];
    }

    public const int BytesPerPixel = 4;

    public int Width { get; }

    public int Height { get; }

    public int Stride => Width * BytesPerPixel;

    /// <summary>Zero-based index of the frame in its stream.</summary>
    public long Index { get; set; }

    /// <summary>Presentation time in QPC ticks.</summary>
    public long Qpc { get; set; }

    /// <summary>Tightly packed BGRA rows.</summary>
    public Span<byte> Pixels => Buffer;

    internal byte[] Buffer { get; }
}
