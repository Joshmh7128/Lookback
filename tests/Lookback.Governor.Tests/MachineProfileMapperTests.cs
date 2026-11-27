using Lookback.Replay.Packets;

namespace Lookback.Governor.Tests;

public class MachineProfileMapperTests
{
    private const long GB = 1L << 30;
    private const long Rtx = 0x16D1D;
    private const long Igpu = 0x17DC8;

    private static Dictionary<CodecKind, IReadOnlyList<string>> Enc(string h264, string hevc, string? av1 = null) => new()
    {
        [CodecKind.H264] = h264.Length == 0 ? [] : [h264],
        [CodecKind.Hevc] = hevc.Length == 0 ? [] : [hevc],
        [CodecKind.Av1] = av1 is null ? [] : [av1],
    };

    /// <summary>What the probe reads on AETHER: RTX on the 4K144 primary and a 1440p100, the iGPU on another 1440p100, WARP.</summary>
    private static MachineProbeResult Aether() => new()
    {
        TotalRamBytes = 63 * GB,
        AvailableRamBytes = 39 * GB,
        CpuLogicalCores = 16,
        Store = new StoreProbe { Path = @"D:\Lookback", Root = @"D:\", TotalBytes = 1863 * GB, FreeBytes = 1078 * GB, IsSsd = true, Bus = StorageBus.Nvme },
        Adapters =
        [
            new AdapterProbe
            {
                Name = "NVIDIA GeForce RTX 4080 SUPER", VendorId = 0x10DE, Luid = Rtx, DedicatedVramBytes = 16 * GB,
                Outputs =
                [
                    new OutputProbe { DeviceName = @"\\.\DISPLAY1", Index = 0, Monitor = 0x100, Width = 3840, Height = 2160, RefreshHz = 144, IsPrimary = true },
                    new OutputProbe { DeviceName = @"\\.\DISPLAY2", Index = 1, Monitor = 0x200, Width = 2560, Height = 1440, RefreshHz = 100 },
                ],
                HardwareEncoders = Enc("NVIDIA H.264 Encoder MFT", "NVIDIA HEVC Encoder MFT", "NVIDIA AV1 Encoder MFT"),
            },
            new AdapterProbe
            {
                Name = "AMD Radeon(TM) Graphics", VendorId = 0x1002, Luid = Igpu, DedicatedVramBytes = GB / 2,
                Outputs = [new OutputProbe { DeviceName = @"\\.\DISPLAY5", Index = 0, Monitor = 0x500, Width = 2560, Height = 1440, RefreshHz = 100 }],
                HardwareEncoders = Enc("AMDh264Encoder", "AMDh265Encoder"),
            },
            new AdapterProbe { Name = "Microsoft Basic Render Driver", VendorId = 0x1414, Luid = 0x17D8A, IsSoftware = true },
        ],
    };

    [Fact]
    public void Primary_monitor_on_the_rtx_gets_4k_hevc_and_never_av1()
    {
        ProfileMapping m = MachineProfileMapper.Map(Aether());
        Assert.Equal(Rtx, m.Adapter!.Luid);
        Assert.Equal(@"\\.\DISPLAY1", m.Output!.DeviceName);
        Assert.Equal((3840, 2160, 144), (m.Profile.DisplayWidth, m.Profile.DisplayHeight, m.Profile.DisplayRefreshHz));
        Assert.Equal("NVIDIA", m.Profile.Gpu!.Vendor);
        Assert.Contains(CodecKind.Av1, m.Profile.Gpu.HardwareEncoders);
        Assert.Contains(m.Notes, n => n.Contains("av1C", StringComparison.Ordinal));
        Assert.Equal(16, m.Profile.CpuCores);
        Assert.True(m.Profile.StoreVolume.IsSsd);

        RecordingPlan p = GovernorPolicy.Decide(m.Profile);
        Assert.Equal("2160p60-hevc", p.Tier.Name);
        Assert.True(p.HardwareEncode);
        Assert.DoesNotContain(QualityTier.Ladder, t => t.Codec == CodecKind.Av1);
    }

    [Fact]
    public void Captured_monitor_picks_the_adapter_that_scans_it_out()
    {
        ProfileMapping igpu = MachineProfileMapper.Map(Aether(), new ProbeTarget(Igpu, 0x500));
        Assert.Equal(Igpu, igpu.Adapter!.Luid);
        Assert.Equal("AMD", igpu.Profile.Gpu!.Vendor);
        Assert.Equal((2560, 1440, 100), (igpu.Profile.DisplayWidth, igpu.Profile.DisplayHeight, igpu.Profile.DisplayRefreshHz));
        Assert.Equal("1440p60-hevc", GovernorPolicy.Decide(igpu.Profile).Tier.Name);

        ProfileMapping second = MachineProfileMapper.Map(Aether(), new ProbeTarget(Monitor: 0x200));
        Assert.Equal(Rtx, second.Adapter!.Luid);
        Assert.Equal(@"\\.\DISPLAY2", second.Output!.DeviceName);
    }

    [Fact]
    public void Adapter_luid_alone_uses_that_adapter()
    {
        ProfileMapping m = MachineProfileMapper.Map(Aether(), new ProbeTarget(AdapterLuid: Igpu));
        Assert.Equal(Igpu, m.Adapter!.Luid);
        Assert.Equal(@"\\.\DISPLAY5", m.Output!.DeviceName);
    }

    [Fact]
    public void Unknown_monitor_falls_back_to_its_adapter_then_the_primary()
    {
        ProfileMapping m = MachineProfileMapper.Map(Aether(), new ProbeTarget(Igpu, 0x999));
        Assert.Equal(Igpu, m.Adapter!.Luid);
        Assert.Contains(m.Notes, n => n.Contains("0x999", StringComparison.Ordinal));

        ProfileMapping none = MachineProfileMapper.Map(Aether(), new ProbeTarget(0x42, 0x999));
        Assert.Equal(Rtx, none.Adapter!.Luid);
        Assert.Equal(2, none.Notes.Count(n => n.Contains("probed", StringComparison.Ordinal)));
    }

    [Fact]
    public void Hybrid_laptop_uses_the_igpu_that_drives_the_panel()
    {
        MachineProbeResult laptop = Aether() with
        {
            Adapters =
            [
                new AdapterProbe { Name = "Intel UHD", VendorId = 0x8086, Luid = 1, Outputs = [new OutputProbe { DeviceName = "P", Index = 0, Monitor = 1, Width = 1920, Height = 1080, RefreshHz = 60, IsPrimary = true }], HardwareEncoders = Enc("Intel QSV H.264", "") },
                new AdapterProbe { Name = "NVIDIA RTX 3060 Laptop", VendorId = 0x10DE, Luid = 2, HardwareEncoders = Enc("NVIDIA H.264 Encoder MFT", "NVIDIA HEVC Encoder MFT") },
            ],
        };
        ProfileMapping m = MachineProfileMapper.Map(laptop);
        Assert.Equal("Intel", m.Profile.Gpu!.Vendor);
        Assert.Equal(CodecKind.H264, GovernorPolicy.Decide(m.Profile).Tier.Codec);
    }

    [Fact]
    public void No_usable_hardware_encoder_means_software()
    {
        MachineProbeResult warpOnly = Aether() with { Adapters = [Aether().Adapters[2]] };
        ProfileMapping w = MachineProfileMapper.Map(warpOnly);
        Assert.Null(w.Adapter);
        Assert.Null(w.Profile.Gpu);
        Assert.Equal(QualityTier.Software, GovernorPolicy.Decide(w.Profile).Tier);

        AdapterProbe rtx = Aether().Adapters[0];
        Assert.Null(MachineProfileMapper.Map(Aether() with { Adapters = [rtx with { HardwareEncoders = null }] }).Profile.Gpu);
        ProfileMapping av1Only = MachineProfileMapper.Map(Aether() with { Adapters = [rtx with { HardwareEncoders = Enc("", "", "AV1") }] });
        Assert.Null(av1Only.Profile.Gpu);
        Assert.False(GovernorPolicy.Decide(av1Only.Profile).HardwareEncode);
    }

    [Fact]
    public void Unknown_memory_and_display_get_conservative_defaults()
    {
        AdapterProbe rtx = Aether().Adapters[0];
        MachineProbeResult p = Aether() with
        {
            TotalRamBytes = null,
            AvailableRamBytes = null,
            CpuLogicalCores = 0,
            Adapters = [rtx with { Outputs = [rtx.Outputs[0] with { Width = 0, Height = 0, RefreshHz = null }] }],
        };
        ProfileMapping m = MachineProfileMapper.Map(p);
        Assert.Equal(MachineProfileMapper.FallbackTotalRamBytes, m.Profile.TotalRamBytes);
        Assert.Equal(MachineProfileMapper.FallbackAvailableRamBytes, m.Profile.AvailableRamBytes);
        Assert.Equal(1, m.Profile.CpuCores);
        Assert.Equal((1920, 1080, 60), (m.Profile.DisplayWidth, m.Profile.DisplayHeight, m.Profile.DisplayRefreshHz));
        Assert.Equal(3, m.Notes.Count(n => n.Contains("unknown", StringComparison.Ordinal)));
        Assert.Equal((long)(2 * GB * GovernorPolicy.RingRamShare), GovernorPolicy.Decide(m.Profile).RingBytes);
    }

    [Theory]
    [InlineData(true, StorageBus.Sata, true)]
    [InlineData(false, StorageBus.Nvme, false)]
    [InlineData(null, StorageBus.Nvme, true)]
    [InlineData(null, StorageBus.Scm, true)]
    [InlineData(null, StorageBus.Sata, false)]
    [InlineData(null, StorageBus.Usb, false)]
    [InlineData(null, StorageBus.Unknown, false)]
    public void Seek_penalty_decides_ssd_and_the_bus_fills_in_when_it_is_unknown(bool? isSsd, StorageBus bus, bool expected)
    {
        MachineProbeResult p = Aether() with { Store = Aether().Store! with { IsSsd = isSsd, Bus = bus } };
        ProfileMapping m = MachineProfileMapper.Map(p);
        Assert.Equal(expected, m.Profile.StoreVolume.IsSsd);
        Assert.Equal(isSsd is null, m.Notes.Any(n => n.Contains("seek penalty", StringComparison.Ordinal)));
        Assert.Equal(!expected, GovernorPolicy.Decide(m.Profile).Tier.BitrateBps <= GovernorPolicy.HddMaxBitrate);
    }

    [Fact]
    public void Unknown_store_gives_no_disk_budget()
    {
        ProfileMapping none = MachineProfileMapper.Map(Aether() with { Store = null });
        Assert.Equal(0, GovernorPolicy.Decide(none.Profile).DiskBudgetBytes);

        ProfileMapping noSpace = MachineProfileMapper.Map(Aether() with { Store = new StoreProbe { Path = @"\\server\share", IsSsd = true } });
        Assert.Equal(@"\\server\share", noSpace.Profile.StoreVolume.Root);
        Assert.Equal(0, GovernorPolicy.Decide(noSpace.Profile).DiskBudgetBytes);
        Assert.Contains(noSpace.Notes, n => n.Contains("free space", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0x10DEu, "NVIDIA")]
    [InlineData(0x1002u, "AMD")]
    [InlineData(0x8086u, "Intel")]
    [InlineData(0x1414u, "Microsoft")]
    [InlineData(0x1234u, "0x1234")]
    public void Vendor_names(uint id, string name) => Assert.Equal(name, MachineProfileMapper.VendorName(id));
}
