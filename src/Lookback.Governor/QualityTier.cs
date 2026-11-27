using Lookback.Replay.Packets;

namespace Lookback.Governor;

/// <summary>One recording quality step.</summary>
public sealed record QualityTier(string Name, int Width, int Height, int Fps, CodecKind Codec, long BitrateBps)
{
    public long BytesPerSecond => BitrateBps / 8;

    /// <summary>
    /// The ladder, best first. AV1 tiers join once the muxer writes
    /// <c>av1C</c>. Bitrates are for high-motion game content at the
    /// codec's efficiency (HEVC needs about 40% less than H.264 for the same quality).
    /// </summary>
    public static IReadOnlyList<QualityTier> Ladder { get; } =
    [
        new("2160p60-hevc", 3840, 2160, 60, CodecKind.Hevc, 60_000_000),
        new("1440p60-hevc", 2560, 1440, 60, CodecKind.Hevc, 30_000_000),
        new("1080p60-hevc", 1920, 1080, 60, CodecKind.Hevc, 20_000_000),
        new("1080p60-h264", 1920, 1080, 60, CodecKind.H264, 25_000_000),
        new("720p60-h264", 1280, 720, 60, CodecKind.H264, 10_000_000),
        new("720p30-h264", 1280, 720, 30, CodecKind.H264, 6_000_000),
    ];

    /// <summary>The software fallback: what a CPU encoder sustains next to a game.</summary>
    public static QualityTier Software { get; } = Ladder[^1];
}

/// <summary>The governor's decision: what to record and how much room each tier gets.</summary>
public sealed record RecordingPlan
{
    public required QualityTier Tier { get; init; }

    /// <summary>Hardware encoder on the capture adapter, or software.</summary>
    public required bool HardwareEncode { get; init; }

    public required long RingBytes { get; init; }

    public required TimeSpan RingLength { get; init; }

    public required long DiskBudgetBytes { get; init; }

    public required long FreeSpaceFloorBytes { get; init; }

    /// <summary>How much session the disk budget holds at this tier (shown in the app).</summary>
    public TimeSpan SessionLength => TimeSpan.FromSeconds((double)DiskBudgetBytes / Tier.BytesPerSecond);

    /// <summary>Why the governor chose this, for the app and logs.</summary>
    public IReadOnlyList<string> Reasons { get; init; } = [];
}
