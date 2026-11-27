using Lookback.Replay.Packets;

namespace Lookback.Governor;

/// <summary>A GPU and the hardware encoders Media Foundation exposes on it.</summary>
public sealed record GpuInfo(string Name, string Vendor, long Luid, long DedicatedVramBytes, IReadOnlySet<CodecKind> HardwareEncoders);

/// <summary>The volume the session store lives on.</summary>
public sealed record VolumeInfo(string Root, long TotalBytes, long FreeBytes, bool IsSsd);

/// <summary>What the governor knows about this PC. Probed at startup and on change.</summary>
public sealed record MachineProfile
{
    public required long TotalRamBytes { get; init; }

    public required long AvailableRamBytes { get; init; }

    public int CpuCores { get; init; } = Environment.ProcessorCount;

    public required VolumeInfo StoreVolume { get; init; }

    /// <summary>The adapter that owns the captured output. Null means software encoding only.</summary>
    public GpuInfo? Gpu { get; init; }

    /// <summary>Resolution and refresh of the captured output; the tier never exceeds it.</summary>
    public int DisplayWidth { get; init; } = 1920;

    public int DisplayHeight { get; init; } = 1080;

    public int DisplayRefreshHz { get; init; } = 60;
}

/// <summary>User settings that bound the policy. Null means "let the governor choose".</summary>
public sealed record GovernorOverrides
{
    public long? DiskCapBytes { get; init; }

    public TimeSpan? ReplayLength { get; init; }

    public string? Tier { get; init; }
}
