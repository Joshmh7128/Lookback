using System.Globalization;

namespace Lookback.Media.Frames;

/// <summary>
/// The fixed format every analysis input is converted to, whatever the source.
/// Live capture produces it with a GPU downscale; FileSource with the decoder's
/// scaler. Signals only ever see this format, so they behave the same on a
/// 4K game and a 720p stream recording.
/// <para>
/// 384x216 is the default and what every profile's thresholds were set
/// against. Other sizes are for experiments (<c>--analysis 768x432</c> in the
/// testbench, <c>Recorder:AnalysisSize</c> in the recorder); see the wiki page
/// Analysis-Resolution for what they cost and change.
/// </para>
/// </summary>
public sealed record AnalysisFormat
{
    /// <summary>Smallest accepted frame: anything smaller leaves HUD regions a pixel or two wide.</summary>
    public const int MinWidth = 64;

    public const int MinHeight = 36;

    /// <summary>Largest accepted frame (8K); one BGRA frame is then 132 MB.</summary>
    public const int MaxWidth = 7680;

    public const int MaxHeight = 4320;

    public static AnalysisFormat Default { get; } = new();

    public int Width { get; init; } = 384;

    public int Height { get; init; } = 216;

    public int FramesPerSecond { get; init; } = 30;

    public int SampleRate { get; init; } = 48_000;

    /// <summary>Audio hop length; 480 samples at 48 kHz is 10 ms.</summary>
    public int HopSamples { get; init; } = 480;

    /// <summary>Pixels per analysis frame.</summary>
    public int PixelCount => Width * Height;

    /// <summary>The frame size as <c>WxH</c>, the form <see cref="Parse"/> reads.</summary>
    public string Size => string.Create(CultureInfo.InvariantCulture, $"{Width}x{Height}");

    /// <summary>True when the frame size is the default one the profiles were tuned at.</summary>
    public bool IsDefaultSize => Width == Default.Width && Height == Default.Height;

    /// <summary>This format with another frame size (validated); rate and audio stay as they are.</summary>
    public AnalysisFormat WithSize(int width, int height)
    {
        string? problem = SizeProblem(width, height);
        if (problem is not null)
        {
            throw new ArgumentException(problem, nameof(width));
        }

        return this with { Width = width, Height = height };
    }

    /// <summary>
    /// Reads a frame size: <c>WxH</c> (for example <c>768x432</c>) or
    /// <c>default</c>. Width and height must be even and within the limits.
    /// </summary>
    public static AnalysisFormat Parse(string text)
    {
        if (!TryParse(text, out AnalysisFormat? format, out string? error))
        {
            throw new ArgumentException(error);
        }

        return format!;
    }

    public static bool TryParse(string? text, out AnalysisFormat? format, out string? error)
    {
        format = null;
        string t = (text ?? "").Trim();
        if (t.Length == 0 || t.Equals("default", StringComparison.OrdinalIgnoreCase))
        {
            format = Default;
            error = null;
            return true;
        }

        int x = t.IndexOfAny(['x', 'X']);
        if (x <= 0
            || !int.TryParse(t.AsSpan(0, x), NumberStyles.None, CultureInfo.InvariantCulture, out int w)
            || !int.TryParse(t.AsSpan(x + 1), NumberStyles.None, CultureInfo.InvariantCulture, out int h))
        {
            error = $"Analysis size '{text}' is not WIDTHxHEIGHT (for example 768x432) or 'default'.";
            return false;
        }

        error = SizeProblem(w, h);
        if (error is not null)
        {
            return false;
        }

        format = w == Default.Width && h == Default.Height ? Default : Default with { Width = w, Height = h };
        return true;
    }

    /// <summary>Why a frame size is refused, or null when it is fine.</summary>
    public static string? SizeProblem(int width, int height)
    {
        if (width < MinWidth || height < MinHeight || width > MaxWidth || height > MaxHeight)
        {
            return string.Create(CultureInfo.InvariantCulture, $"Analysis size {width}x{height} is outside {MinWidth}x{MinHeight} to {MaxWidth}x{MaxHeight}.");
        }

        // Odd sizes would make the GPU and ffmpeg letterbox round differently.
        return width % 2 != 0 || height % 2 != 0
            ? string.Create(CultureInfo.InvariantCulture, $"Analysis size {width}x{height} must have an even width and height.")
            : null;
    }
}
