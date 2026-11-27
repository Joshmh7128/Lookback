namespace Lookback.Replay.Packets;

/// <summary>Elementary stream codecs the muxer can carry.</summary>
public enum CodecKind
{
    /// <summary>H.264 / AVC, Annex B in, length-prefixed in MP4.</summary>
    H264 = 1,

    /// <summary>H.265 / HEVC, Annex B in, length-prefixed in MP4.</summary>
    Hevc = 2,

    /// <summary>AV1. Encoded by the governor's top tier once the muxer carries it.</summary>
    Av1 = 3,

    /// <summary>AAC-LC, raw access units (no ADTS).</summary>
    Aac = 10,
}

/// <summary>
/// One elementary stream of a recording. <see cref="CodecConfig"/> is the
/// sample-entry payload (<c>avcC</c>, <c>hvcC</c> or the AAC
/// AudioSpecificConfig); for video it is derived from the first keyframe's
/// parameter sets, so it may be empty until then.
/// </summary>
public sealed record TrackInfo
{
    /// <summary>1-based MP4 track id; unique within a recording.</summary>
    public required int Id { get; init; }

    public required CodecKind Codec { get; init; }

    /// <summary>Media timescale in ticks per second (90 kHz for video, the sample rate for audio).</summary>
    public required int Timescale { get; init; }

    public int Width { get; init; }

    public int Height { get; init; }

    public int SampleRate { get; init; }

    public int Channels { get; init; }

    /// <summary>Optional label carried in the session manifest, such as "game" or "mic".</summary>
    public string Role { get; init; } = "";

    public byte[] CodecConfig { get; init; } = [];

    public bool IsVideo => Codec is CodecKind.H264 or CodecKind.Hevc or CodecKind.Av1;

    public static TrackInfo Video(int id, CodecKind codec, int width, int height) => new()
    {
        Id = id,
        Codec = codec,
        Timescale = 90_000,
        Width = width,
        Height = height,
        Role = "video",
    };

    public static TrackInfo AacAudio(int id, int sampleRate, int channels, string role = "game") => new()
    {
        Id = id,
        Codec = CodecKind.Aac,
        Timescale = sampleRate,
        SampleRate = sampleRate,
        Channels = channels,
        Role = role,
        CodecConfig = Mp4.AacConfig.AudioSpecificConfig(sampleRate, channels),
    };
}
