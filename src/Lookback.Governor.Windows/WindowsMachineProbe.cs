using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Lookback.Capture.Gpu;
using Lookback.Capture.Targets;
using Lookback.Encode;
using Lookback.Replay.Packets;
using Microsoft.Win32.SafeHandles;
using Vortice.DXGI;

namespace Lookback.Governor.Windows;

/// <summary>
/// Fills a <see cref="MachineProbeResult"/> on Windows. Reads only what the OS
/// exposes to a normal user: no admin, no drivers, no WMI, nothing that opens
/// another process. The volume is opened with zero access rights, which is
/// enough for <c>IOCTL_STORAGE_QUERY_PROPERTY</c>. Each query is independent
/// and never throws; a failure is recorded in <see cref="MachineProbeResult.Unknowns"/>.
/// </summary>
public static class WindowsMachineProbe
{
    /// <summary>Encoder codecs asked about per adapter. AV1 is recorded for the log; the ladder stops at HEVC.</summary>
    private static readonly CodecKind[] Codecs = [CodecKind.H264, CodecKind.Hevc, CodecKind.Av1];

    /// <summary>Probes everything. <paramref name="storePath"/> is the session store root (it need not exist yet).</summary>
    public static MachineProbeResult Run(string storePath)
    {
        ArgumentNullException.ThrowIfNull(storePath);
        var total = Stopwatch.StartNew();
        var unknowns = new List<string>();
        var timings = new Dictionary<string, TimeSpan>();

        (long? ram, long? avail) = Timed("memory", timings, unknowns, Memory);
        StoreProbe store = Timed("store", timings, unknowns, () => Store(storePath, unknowns)) ?? new StoreProbe { Path = storePath };
        List<AdapterProbe> adapters = Timed("adapters", timings, unknowns, () => Adapters(unknowns)) ?? [];
        adapters = Timed("encoders", timings, unknowns, () => Encoders(adapters, unknowns)) ?? adapters;

        return new MachineProbeResult
        {
            TotalRamBytes = ram,
            AvailableRamBytes = avail,
            CpuLogicalCores = Environment.ProcessorCount,
            Store = store,
            Adapters = adapters,
            Unknowns = unknowns,
            Timings = timings,
            Elapsed = total.Elapsed,
        };
    }

    /// <summary>Probes and maps for one capture target, ready for <see cref="GovernorPolicy.Decide"/>.</summary>
    public static ProfileMapping Profile(string storePath, ProbeTarget target = default) => MachineProfileMapper.Map(Run(storePath), target);

    /// <summary>The probe target for a capture target: its monitor and the adapter that owns it.</summary>
    public static ProbeTarget ToProbeTarget(this CaptureTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return new ProbeTarget(target.AdapterLuid, target.Monitor);
    }

    private static T? Timed<T>(string what, Dictionary<string, TimeSpan> timings, List<string> unknowns, Func<T> query)
    {
        long start = Stopwatch.GetTimestamp();
        try
        {
            return query();
        }
#pragma warning disable CA1031 // the probe records a failed query and carries on
        catch (Exception e)
#pragma warning restore CA1031
        {
            unknowns.Add($"{what}: {e.GetType().Name}: {e.Message}");
            return default;
        }
        finally
        {
            timings[what] = Stopwatch.GetElapsedTime(start);
        }
    }

    private static (long?, long?) Memory()
    {
        var s = new Native.MemoryStatusEx { Length = (uint)Marshal.SizeOf<Native.MemoryStatusEx>() };
        if (!Native.GlobalMemoryStatusEx(ref s))
        {
            throw new InvalidOperationException($"GlobalMemoryStatusEx failed ({Marshal.GetLastPInvokeError()})");
        }

        return ((long)s.TotalPhys, (long)s.AvailPhys);
    }

    internal static unsafe StoreProbe Store(string path, List<string> unknowns)
    {
        string existing = ExistingAncestor(Path.GetFullPath(path));
        char* buf = stackalloc char[1024];
        string? root = Native.GetVolumePathName(existing, buf, 1024) ? new string(buf) : null;
        if (root is null)
        {
            unknowns.Add($"store: no volume for {existing} ({Marshal.GetLastPInvokeError()})");
            return new StoreProbe { Path = path };
        }

        long? totalBytes = null, freeBytes = null;
        if (Native.GetDiskFreeSpaceEx(root, out ulong freeToCaller, out ulong total, out _))
        {
            totalBytes = (long)total;
            freeBytes = (long)freeToCaller;
        }
        else
        {
            unknowns.Add($"store: free space on {root} ({Marshal.GetLastPInvokeError()})");
        }

        bool? ssd = null;
        StorageBus bus = StorageBus.Unknown;
        string? model = null;

        // \\?\Volume{guid}\ works for drive letters and folder mount points alike; CreateFile wants it without the slash.
        string? volume = Native.GetVolumeNameForVolumeMountPoint(root, buf, 1024) ? new string(buf).TrimEnd('\\') : null;
        if (volume is null)
        {
            unknowns.Add($"store: {root} is not a local volume ({Marshal.GetLastPInvokeError()})");
        }
        else
        {
            // No "insert a disk" dialog for an empty card reader.
            Native.SetThreadErrorMode(Native.SemFailCriticalErrors, out uint oldMode);
            try
            {
                using SafeFileHandle h = Native.CreateFile(volume, 0, Native.FileShareReadWrite, 0, Native.OpenExisting, 0, 0);
                if (h.IsInvalid)
                {
                    unknowns.Add($"store: open {root} ({Marshal.GetLastPInvokeError()})");
                }
                else
                {
                    ssd = SeekPenalty(h) is bool penalty ? !penalty : null;
                    if (ssd is null)
                    {
                        unknowns.Add($"store: seek penalty on {root} ({Marshal.GetLastPInvokeError()})");
                    }

                    (bus, model) = Device(h);
                }
            }
            finally
            {
                Native.SetThreadErrorMode(oldMode, out _);
            }
        }

        return new StoreProbe { Path = path, Root = root, TotalBytes = totalBytes, FreeBytes = freeBytes, IsSsd = ssd, Bus = bus, Model = model };
    }

    private static string ExistingAncestor(string path)
    {
        string? p = path;
        while (p is not null && !Directory.Exists(p))
        {
            p = Path.GetDirectoryName(p);
        }

        return p ?? path;
    }

    private static unsafe bool? SeekPenalty(SafeFileHandle h)
    {
        var q = new Native.StoragePropertyQuery { PropertyId = Native.StorageDeviceSeekPenaltyProperty, QueryType = Native.PropertyStandardQuery };
        byte* outBuf = stackalloc byte[12]; // DEVICE_SEEK_PENALTY_DESCRIPTOR: Version, Size, BOOLEAN IncursSeekPenalty
        if (!Native.DeviceIoControl(h, Native.IoctlStorageQueryProperty, &q, (uint)sizeof(Native.StoragePropertyQuery), outBuf, 12, out uint got, 0) || got < 9)
        {
            return null;
        }

        return outBuf[8] != 0;
    }

    private static unsafe (StorageBus, string?) Device(SafeFileHandle h)
    {
        var q = new Native.StoragePropertyQuery { PropertyId = Native.StorageDeviceProperty, QueryType = Native.PropertyStandardQuery };
        const int Size = 1024;
        byte* outBuf = stackalloc byte[Size];
        if (!Native.DeviceIoControl(h, Native.IoctlStorageQueryProperty, &q, (uint)sizeof(Native.StoragePropertyQuery), outBuf, Size, out uint got, 0) || got < 32)
        {
            return (StorageBus.Unknown, null);
        }

        var d = new ReadOnlySpan<byte>(outBuf, (int)got);
        var bus = (StorageBus)BinaryPrimitives.ReadInt32LittleEndian(d[28..]);
        uint productOffset = BinaryPrimitives.ReadUInt32LittleEndian(d[16..]);
        string? model = null;
        if (productOffset > 0 && productOffset < got)
        {
            ReadOnlySpan<byte> s = d[(int)productOffset..];
            int end = s.IndexOf((byte)0);
            model = System.Text.Encoding.ASCII.GetString(end < 0 ? s : s[..end]).Trim();
            model = model.Length == 0 ? null : model;
        }

        // The serial number is in the same descriptor; it is deliberately not read.
        return (bus, model);
    }

    internal static List<AdapterProbe> Adapters(List<string> unknowns)
    {
        var adapters = new List<AdapterProbe>();
        using IDXGIFactory1 factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        for (uint a = 0; factory.EnumAdapters1(a, out IDXGIAdapter1 adapter).Success; a++)
        {
            using (adapter)
            {
                try
                {
                    adapters.Add(Adapter(adapter, unknowns));
                }
#pragma warning disable CA1031 // one odd adapter (a virtual display driver, say) must not hide the others
                catch (Exception e)
#pragma warning restore CA1031
                {
                    unknowns.Add($"adapter {a}: {e.GetType().Name}: {e.Message}");
                }
            }
        }

        return adapters;
    }

    private static AdapterProbe Adapter(IDXGIAdapter1 adapter, List<string> unknowns)
    {
        AdapterDescription1 d = adapter.Description1;
        var outputs = new List<OutputProbe>();
        for (uint o = 0; adapter.EnumOutputs(o, out IDXGIOutput output).Success; o++)
        {
            using (output)
            {
                OutputDescription od = output.Description;
                if (od.AttachedToDesktop)
                {
                    outputs.Add(Output(od, (int)o, unknowns));
                }
            }
        }

        return new AdapterProbe
        {
            Name = d.Description.Trim(),
            VendorId = d.VendorId,
            Luid = GpuDevice.ToLong(d.Luid),
            DedicatedVramBytes = (long)(ulong)d.DedicatedVideoMemory, // nuint; a direct (long) goes through a checked uint conversion
            IsSoftware = (d.Flags & AdapterFlags.Software) != 0,
            Outputs = outputs,
        };
    }

    private static unsafe OutputProbe Output(OutputDescription od, int index, List<string> unknowns)
    {
        var r = od.DesktopCoordinates;
        int width = r.Right - r.Left, height = r.Bottom - r.Top;
        int? hz = null;
        byte* dm = stackalloc byte[Native.DevModeSize];
        new Span<byte>(dm, Native.DevModeSize).Clear();
        BinaryPrimitives.WriteUInt16LittleEndian(new Span<byte>(dm + Native.DevModeSizeOffset, 2), Native.DevModeSize);
        if (Native.EnumDisplaySettings(od.DeviceName, Native.EnumCurrentSettings, dm))
        {
            // The mode is in physical pixels; desktop coordinates can be DPI-virtualised.
            width = (int)BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(dm + Native.DevModePelsWidthOffset, 4));
            height = (int)BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(dm + Native.DevModePelsHeightOffset, 4));
            uint f = BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(dm + Native.DevModeFrequencyOffset, 4));
            hz = f > 1 ? (int)f : null; // 0 and 1 mean "hardware default"
        }
        else
        {
            unknowns.Add($"display mode of {od.DeviceName}");
        }

        return new OutputProbe
        {
            DeviceName = od.DeviceName,
            Index = index,
            Monitor = od.Monitor,
            Width = width,
            Height = height,
            RefreshHz = hz,
            IsPrimary = r.Left == 0 && r.Top == 0,
        };
    }

    internal static List<AdapterProbe> Encoders(List<AdapterProbe> adapters, List<string> unknowns)
    {
        MediaFoundation.Startup();
        var result = new List<AdapterProbe>(adapters.Count);
        foreach (AdapterProbe a in adapters)
        {
            if (a.IsSoftware)
            {
                result.Add(a);
                continue;
            }

            var found = new Dictionary<CodecKind, IReadOnlyList<string>>();
            bool failed = false;
            foreach (CodecKind codec in Codecs)
            {
                try
                {
                    found[codec] = MfVideoEncoder.List(codec, a.Luid, hardware: true);
                }
#pragma warning disable CA1031 // one codec failing must not hide the others
                catch (Exception e)
#pragma warning restore CA1031
                {
                    failed = true;
                    unknowns.Add($"encoders: {codec} on {a.Name}: {e.Message}");
                }
            }

            result.Add(a with { HardwareEncoders = failed && found.Count == 0 ? null : found });
        }

        return result;
    }
}
