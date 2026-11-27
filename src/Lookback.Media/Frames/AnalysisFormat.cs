namespace Lookback.Media.Frames;

/// <summary>
/// The fixed format every analysis input is converted to, whatever the source.
/// Live capture produces it with a GPU downscale; FileSource with the decoder's
/// scaler. Signals only ever see this format, so they behave the same on a
/// 4K game and a 720p stream recording.
/// </summary>
public sealed record AnalysisFormat
{
    public static AnalysisFormat Default { get; } = new();

    public int Width { get; init; } = 384;

    public int Height { get; init; } = 216;

    public int FramesPerSecond { get; init; } = 30;

    public int SampleRate { get; init; } = 48_000;

    /// <summary>Audio hop length; 480 samples at 48 kHz is 10 ms.</summary>
    public int HopSamples { get; init; } = 480;
}
