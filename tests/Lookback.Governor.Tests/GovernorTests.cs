using Lookback.Core.Time;
using Lookback.Replay.Packets;

namespace Lookback.Governor.Tests;

public class GovernorTests
{
    private const long GB = 1L << 30;

    /// <summary>AETHER: 64 GB, NVMe D:, RTX 4080 SUPER on a 4K144 display.</summary>
    private static MachineProfile Aether() => new()
    {
        TotalRamBytes = 63 * GB,
        AvailableRamBytes = 42 * GB,
        StoreVolume = new VolumeInfo("D:\\", 1863 * GB, 1079 * GB, IsSsd: true),
        Gpu = new GpuInfo("NVIDIA GeForce RTX 4080 SUPER", "NVIDIA", 1, 16 * GB, new HashSet<CodecKind> { CodecKind.H264, CodecKind.Hevc, CodecKind.Av1 }),
        DisplayWidth = 3840,
        DisplayHeight = 2160,
        DisplayRefreshHz = 144,
    };

    private static MachineProfile SmallLaptop() => new()
    {
        TotalRamBytes = 8 * GB,
        AvailableRamBytes = 3 * GB,
        StoreVolume = new VolumeInfo("C:\\", 500 * GB, 40 * GB, IsSsd: false),
        Gpu = new GpuInfo("iGPU", "Intel", 2, 0, new HashSet<CodecKind> { CodecKind.H264 }),
        DisplayWidth = 1920,
        DisplayHeight = 1080,
        DisplayRefreshHz = 60,
    };

    [Fact]
    public void Big_machine_gets_the_top_hevc_tier_and_a_full_replay()
    {
        RecordingPlan p = GovernorPolicy.Decide(Aether());
        Assert.Equal("2160p60-hevc", p.Tier.Name);
        Assert.True(p.HardwareEncode);
        Assert.Equal(GovernorPolicy.RingCeilingBytes, p.RingBytes);
        Assert.Equal(TimeSpan.FromMinutes(2), p.RingLength);
        Assert.True(p.SessionLength > TimeSpan.FromHours(10));
        Assert.True(p.FreeSpaceFloorBytes >= 93 * GB);
    }

    [Fact]
    public void Small_hdd_laptop_gets_h264_within_its_disk()
    {
        RecordingPlan p = GovernorPolicy.Decide(SmallLaptop());
        Assert.Equal(CodecKind.H264, p.Tier.Codec);
        Assert.True(p.Tier.BitrateBps <= GovernorPolicy.HddMaxBitrate);
        Assert.Equal((long)(3 * GB * 0.05), p.RingBytes);
        Assert.True(p.SessionLength >= GovernorPolicy.UsefulSession);
        Assert.Contains(p.Reasons, r => r.Contains("HDD", StringComparison.Ordinal));
    }

    [Fact]
    public void No_gpu_falls_back_to_software_720p30()
    {
        RecordingPlan p = GovernorPolicy.Decide(SmallLaptop() with { Gpu = null });
        Assert.False(p.HardwareEncode);
        Assert.Equal(QualityTier.Software, p.Tier);
    }

    [Fact]
    public void User_cap_and_tier_override_are_respected()
    {
        RecordingPlan p = GovernorPolicy.Decide(Aether(), new GovernorOverrides { DiskCapBytes = 20 * GB, Tier = "1080p60-hevc" });
        Assert.Equal(20 * GB, p.DiskBudgetBytes);
        Assert.Equal("1080p60-hevc", p.Tier.Name);
    }

    [Fact]
    public void Controller_steps_down_under_sustained_pressure_and_recovers_with_hysteresis()
    {
        var clock = new ManualClock();
        MachineProfile m = Aether();
        RecordingPlan plan = GovernorPolicy.Decide(m);
        var c = new GovernorController(clock, m, plan);
        var calm = new PressureSample(false, 1000 * GB, 0.3);
        var busy = calm with { EncoderLoad = 0.97 };

        Assert.Null(c.Observe(busy));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Null(c.Observe(busy)); // a 1 s spike is ignored
        clock.Advance(TimeSpan.FromSeconds(1.5));
        RecordingPlan? down = c.Observe(busy);
        Assert.Equal("1440p60-hevc", down?.Tier.Name);

        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Null(c.Observe(calm));
        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Null(c.Observe(calm)); // clear for 30 s only, measured from the first calm sample
        clock.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal("2160p60-hevc", c.Observe(calm)?.Tier.Name);
        clock.Advance(TimeSpan.FromSeconds(120));
        Assert.Null(c.Observe(calm)); // never above the planned tier
    }

    [Fact]
    public void Memory_pressure_halves_the_ring()
    {
        var clock = new ManualClock();
        MachineProfile m = Aether();
        RecordingPlan plan = GovernorPolicy.Decide(m);
        var c = new GovernorController(clock, m, plan);
        var low = new PressureSample(true, 1000 * GB, 0.1);
        c.Observe(low);
        clock.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal(plan.RingBytes / 2, c.Observe(low)?.RingBytes);
    }
}
