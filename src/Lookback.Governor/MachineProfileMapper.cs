using Lookback.Replay.Packets;

namespace Lookback.Governor;

/// <summary>
/// Turns a <see cref="MachineProbeResult"/> into the <see cref="MachineProfile"/>
/// for one capture target. Pure and platform-neutral. Anything the probe could
/// not read is filled with a conservative value and noted, so a half-failed
/// probe yields a smaller plan, never a crash.
/// </summary>
public static class MachineProfileMapper
{
    private const long GB = 1L << 30;

    /// <summary>Assumed when the memory query fails: a small machine, so the ring stays small.</summary>
    public const long FallbackTotalRamBytes = 8 * GB;

    public const long FallbackAvailableRamBytes = 2 * GB;

    public static ProfileMapping Map(MachineProbeResult probe, ProbeTarget target = default)
    {
        ArgumentNullException.ThrowIfNull(probe);
        var notes = new List<string>();

        long total = probe.TotalRamBytes ?? FallbackTotalRamBytes;
        long available = probe.AvailableRamBytes ?? Math.Min(FallbackAvailableRamBytes, total);
        if (probe.TotalRamBytes is null || probe.AvailableRamBytes is null)
        {
            notes.Add($"memory unknown: assuming {total / GB} GB total, {available / GB} GB available");
        }

        (AdapterProbe? adapter, OutputProbe? output) = Choose(probe.Adapters, target, notes);

        var profile = new MachineProfile
        {
            TotalRamBytes = total,
            AvailableRamBytes = available,
            CpuCores = probe.CpuLogicalCores > 0 ? probe.CpuLogicalCores : 1,
            StoreVolume = Volume(probe.Store, notes),
            Gpu = Gpu(adapter, notes),
        };

        if (output is { Width: > 0, Height: > 0 })
        {
            profile = profile with { DisplayWidth = output.Width, DisplayHeight = output.Height };
        }
        else
        {
            notes.Add($"display size unknown: assuming {profile.DisplayWidth}x{profile.DisplayHeight}");
        }

        if (output?.RefreshHz is int hz && hz > 1)
        {
            profile = profile with { DisplayRefreshHz = hz };
        }
        else
        {
            notes.Add($"refresh rate unknown: assuming {profile.DisplayRefreshHz} Hz");
        }

        return new ProfileMapping(profile, adapter, output, notes);
    }

    /// <summary>
    /// The adapter and output the pipeline runs on: the one that scans out the
    /// captured monitor, else the adapter asked for, else the primary monitor's.
    /// </summary>
    internal static (AdapterProbe? Adapter, OutputProbe? Output) Choose(IReadOnlyList<AdapterProbe> adapters, ProbeTarget target, List<string> notes)
    {
        List<AdapterProbe> hardware = [.. adapters.Where(a => !a.IsSoftware)];

        if (target.Monitor is long monitor)
        {
            foreach (AdapterProbe a in hardware)
            {
                if (a.Outputs.FirstOrDefault(o => o.Monitor == monitor) is OutputProbe o)
                {
                    return (a, o);
                }
            }

            notes.Add($"monitor 0x{monitor:X} is not on any probed adapter");
        }

        if (target.AdapterLuid is long luid)
        {
            if (hardware.FirstOrDefault(a => a.Luid == luid) is AdapterProbe a)
            {
                return (a, a.Outputs.FirstOrDefault(o => o.IsPrimary) ?? (a.Outputs.Count > 0 ? a.Outputs[0] : null));
            }

            notes.Add($"adapter {luid:X} was not probed");
        }

        foreach (AdapterProbe a in hardware)
        {
            if (a.Outputs.FirstOrDefault(o => o.IsPrimary) is OutputProbe o)
            {
                return (a, o);
            }
        }

        if (hardware.FirstOrDefault(a => a.Outputs.Count > 0) is AdapterProbe withOutput)
        {
            notes.Add("no primary output found: using the first adapter with a monitor");
            return (withOutput, withOutput.Outputs[0]);
        }

        if (hardware.Count > 0)
        {
            notes.Add("no adapter drives a monitor: using the first hardware adapter");
            return (hardware[0], null);
        }

        notes.Add("no hardware adapter");
        return (null, null);
    }

    internal static VolumeInfo Volume(StoreProbe? store, List<string> notes)
    {
        if (store is null)
        {
            notes.Add("store volume unknown: no disk budget");
            return new VolumeInfo("", 0, 0, IsSsd: false);
        }

        string root = store.Root ?? store.Path;
        if (store.TotalBytes is null || store.FreeBytes is null)
        {
            notes.Add($"free space on {root} unknown: no disk budget");
        }

        bool ssd;
        if (store.IsSsd is bool known)
        {
            ssd = known;
        }
        else if (IsFlashBus(store.Bus))
        {
            ssd = true;
            notes.Add($"seek penalty unknown: {store.Bus} bus, treated as SSD");
        }
        else
        {
            ssd = false;
            notes.Add($"seek penalty unknown ({store.Bus} bus): treated as HDD");
        }

        return new VolumeInfo(root, store.TotalBytes ?? 0, store.FreeBytes ?? 0, ssd);
    }

    /// <summary>Buses that only carry solid-state storage fast enough to record to.</summary>
    public static bool IsFlashBus(StorageBus bus) => bus is StorageBus.Nvme or StorageBus.Scm or StorageBus.Ufs;

    internal static GpuInfo? Gpu(AdapterProbe? adapter, List<string> notes)
    {
        if (adapter is null)
        {
            return null;
        }

        if (adapter.HardwareEncoders is null)
        {
            notes.Add($"encoder query failed on {adapter.Name}: software encoding");
            return null;
        }

        var codecs = new HashSet<CodecKind>(adapter.HardwareEncoders.Where(kv => kv.Value.Count > 0).Select(kv => kv.Key));
        if (codecs.Contains(CodecKind.Av1))
        {
            // Kept in the set (it is the truth); the ladder has no AV1 tier until the muxer writes av1C.
            notes.Add($"{adapter.Name} has an AV1 encoder; not used until the muxer writes av1C");
        }

        if (!codecs.Contains(CodecKind.H264) && !codecs.Contains(CodecKind.Hevc))
        {
            notes.Add($"no H.264 or HEVC hardware encoder on {adapter.Name}: software encoding");
            return null;
        }

        return new GpuInfo(adapter.Name, adapter.Vendor, adapter.Luid, adapter.DedicatedVramBytes, codecs);
    }

    /// <summary>PCI vendor id to a name.</summary>
    public static string VendorName(uint vendorId) => vendorId switch
    {
        0x10DE => "NVIDIA",
        0x1002 or 0x1022 => "AMD",
        0x8086 => "Intel",
        0x1414 => "Microsoft",
        0x5143 => "Qualcomm",
        0x0 => "Unknown",
        _ => $"0x{vendorId:X4}",
    };
}
