using Lookback.Replay.Packets;

namespace Lookback.Governor;

/// <summary>How the store's disk is attached, as the OS reports it (mirrors STORAGE_BUS_TYPE).</summary>
public enum StorageBus
{
    Unknown = 0,
    Scsi = 0x1,
    Atapi = 0x2,
    Ata = 0x3,
    Ieee1394 = 0x4,
    Ssa = 0x5,
    Fibre = 0x6,
    Usb = 0x7,
    Raid = 0x8,
    Iscsi = 0x9,
    Sas = 0xA,
    Sata = 0xB,
    Sd = 0xC,
    Mmc = 0xD,
    Virtual = 0xE,
    FileBackedVirtual = 0xF,
    Spaces = 0x10,
    Nvme = 0x11,
    Scm = 0x12,
    Ufs = 0x13,
}

/// <summary>The session store's volume as probed. Null fields were not readable.</summary>
public sealed record StoreProbe
{
    /// <summary>The path that was asked about.</summary>
    public required string Path { get; init; }

    /// <summary>The volume mount point, such as <c>D:\</c>.</summary>
    public string? Root { get; init; }

    public long? TotalBytes { get; init; }

    /// <summary>Free bytes available to this user (quotas applied).</summary>
    public long? FreeBytes { get; init; }

    /// <summary>False when the disk reports a seek penalty (spinning); null when it did not answer.</summary>
    public bool? IsSsd { get; init; }

    public StorageBus Bus { get; init; }

    /// <summary>The disk's product string, when the driver reports one.</summary>
    public string? Model { get; init; }
}

/// <summary>One desktop output (monitor) and its current mode.</summary>
public sealed record OutputProbe
{
    /// <summary>GDI device name, such as <c>\\.\DISPLAY1</c>.</summary>
    public required string DeviceName { get; init; }

    /// <summary>DXGI output index on its adapter.</summary>
    public required int Index { get; init; }

    /// <summary>The HMONITOR, as a number, so a capture target can be matched to it.</summary>
    public long Monitor { get; init; }

    public int Width { get; init; }

    public int Height { get; init; }

    /// <summary>Refresh rate of the current mode; null when the mode query failed.</summary>
    public int? RefreshHz { get; init; }

    public bool IsPrimary { get; init; }
}

/// <summary>A graphics adapter, the outputs it scans out and its Media Foundation encoders.</summary>
public sealed record AdapterProbe
{
    public required string Name { get; init; }

    public uint VendorId { get; init; }

    public string Vendor => MachineProfileMapper.VendorName(VendorId);

    /// <summary>Adapter LUID (low part | high part &lt;&lt; 32), the key the encoder and capture device are chosen by.</summary>
    public required long Luid { get; init; }

    public long DedicatedVramBytes { get; init; }

    /// <summary>A software adapter (the Microsoft Basic Render Driver); never used for capture.</summary>
    public bool IsSoftware { get; init; }

    public IReadOnlyList<OutputProbe> Outputs { get; init; } = [];

    /// <summary>
    /// Hardware encoder MFTs on this adapter, by codec (friendly names). AV1 is
    /// recorded but the ladder has no AV1 tier until the muxer writes <c>av1C</c>.
    /// Null when the encoder query failed for this adapter.
    /// </summary>
    public IReadOnlyDictionary<CodecKind, IReadOnlyList<string>>? HardwareEncoders { get; init; }
}

/// <summary>
/// Everything the machine probe could read about this PC. Probing never
/// throws: a query that fails leaves its field null and adds a line to
/// <see cref="Unknowns"/>. <see cref="MachineProfileMapper"/> turns it into
/// the <see cref="MachineProfile"/> the policy consumes.
/// </summary>
public sealed record MachineProbeResult
{
    public long? TotalRamBytes { get; init; }

    public long? AvailableRamBytes { get; init; }

    public int CpuLogicalCores { get; init; }

    public StoreProbe? Store { get; init; }

    public IReadOnlyList<AdapterProbe> Adapters { get; init; } = [];

    /// <summary>Queries that failed, one line each.</summary>
    public IReadOnlyList<string> Unknowns { get; init; } = [];

    /// <summary>Time spent per query, for the startup budget.</summary>
    public IReadOnlyDictionary<string, TimeSpan> Timings { get; init; } = new Dictionary<string, TimeSpan>();

    public TimeSpan Elapsed { get; init; }
}

/// <summary>Which output is being captured. Both null means the primary monitor.</summary>
public readonly record struct ProbeTarget(long? AdapterLuid = null, long? Monitor = null);

/// <summary>The profile for one target, plus what the mapping picked and assumed.</summary>
public sealed record ProfileMapping(MachineProfile Profile, AdapterProbe? Adapter, OutputProbe? Output, IReadOnlyList<string> Notes);
