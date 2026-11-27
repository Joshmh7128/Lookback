using Lookback.Capture.Targets;
using Lookback.Replay.Packets;

namespace Lookback.Governor.Windows.Tests;

/// <summary>
/// Runs the real probe on this machine. Windows only. GitHub's Windows
/// runners have no GPU (only the Basic Render Driver), so the hardware
/// assertions skip on CI and run on a dev box such as AETHER.
/// </summary>
public class WindowsMachineProbeTests(ITestOutputHelper output)
{
    private static bool OnCi => Environment.GetEnvironmentVariable("CI") is not null;

    private static MachineProbeResult Probe() => WindowsMachineProbe.Run(Path.Combine(Path.GetTempPath(), "lookback-probe-test", "not-created"));

    private void Log(MachineProbeResult p)
    {
        output.WriteLine($"probe {p.Elapsed.TotalMilliseconds:F0} ms: {string.Join(", ", p.Timings.Select(kv => $"{kv.Key} {kv.Value.TotalMilliseconds:F0}"))}");
        output.WriteLine($"store {p.Store?.Root} ssd={p.Store?.IsSsd} bus={p.Store?.Bus} model={p.Store?.Model}");
        foreach (AdapterProbe a in p.Adapters)
        {
            output.WriteLine($"{a.Luid:X} {a.Name} sw={a.IsSoftware} outputs={a.Outputs.Count} enc={string.Join(";", a.HardwareEncoders?.Select(kv => $"{kv.Key}:{string.Join("|", kv.Value)}") ?? ["?"])}");
        }

        foreach (string u in p.Unknowns)
        {
            output.WriteLine($"unknown: {u}");
        }
    }

    [Fact]
    public void Probe_reads_sane_values_on_this_machine()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "the machine probe is Windows only");
        MachineProbeResult p = Probe();
        Log(p);

        Assert.True(p.TotalRamBytes > 0);
        Assert.True(p.AvailableRamBytes > 0 && p.AvailableRamBytes <= p.TotalRamBytes);
        Assert.Equal(Environment.ProcessorCount, p.CpuLogicalCores);

        StoreProbe s = Assert.IsType<StoreProbe>(p.Store);
        Assert.Equal(Path.GetPathRoot(Path.GetTempPath()), s.Root, ignoreCase: true);
        Assert.True(s.TotalBytes > 0);
        Assert.True(s.FreeBytes > 0 && s.FreeBytes <= s.TotalBytes);

        Assert.NotEmpty(p.Adapters);
        Assert.All(p.Adapters, a => Assert.NotEqual(0, a.Luid));
        Assert.DoesNotContain(p.Unknowns, u => u.StartsWith("memory", StringComparison.Ordinal) || u.StartsWith("adapters", StringComparison.Ordinal));

        if (!p.Adapters.Any(a => !a.IsSoftware))
        {
            Assert.SkipWhen(OnCi, "CI runners have no hardware adapter");
            Assert.Fail("no hardware adapter on a dev machine");
        }

        Assert.Contains(p.Adapters, a => !a.IsSoftware && a.Outputs.Count > 0);
        Assert.Contains(p.Adapters.SelectMany(a => a.Outputs), o => o.IsPrimary);
        Assert.All(p.Adapters.SelectMany(a => a.Outputs), o => Assert.True(o.Width > 0 && o.Height > 0 && o.RefreshHz > 1));
    }

    [Fact]
    public void Probe_is_fast_once_media_foundation_is_up()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "the machine probe is Windows only");
        _ = Probe(); // first call pays for MFStartup and DLL loads
        MachineProbeResult p = Probe();
        Log(p);
        Assert.True(p.Elapsed < TimeSpan.FromMilliseconds(OnCi ? 2000 : 200), $"probe took {p.Elapsed.TotalMilliseconds:F0} ms");
    }

    [Fact]
    public void Aether_finds_nvenc_h264_and_hevc_on_the_rtx()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "the machine probe is Windows only");
        MachineProbeResult p = Probe();
        Log(p);
        AdapterProbe? rtx = p.Adapters.FirstOrDefault(a => a.Name.Contains("RTX 4080", StringComparison.Ordinal) && a.Outputs.Count > 0);
        Assert.SkipWhen(rtx is null, "not AETHER (no RTX 4080 driving a monitor)");

        Assert.Equal("NVIDIA", rtx!.Vendor);
        Assert.True(rtx.DedicatedVramBytes > 8L << 30);
        IReadOnlyDictionary<CodecKind, IReadOnlyList<string>> enc = Assert.IsAssignableFrom<IReadOnlyDictionary<CodecKind, IReadOnlyList<string>>>(rtx.HardwareEncoders);
        Assert.Contains(enc[CodecKind.H264], n => n.Contains("NVIDIA", StringComparison.Ordinal));
        Assert.Contains(enc[CodecKind.Hevc], n => n.Contains("NVIDIA", StringComparison.Ordinal));
        Assert.Contains(enc[CodecKind.Av1], n => n.Contains("NVIDIA", StringComparison.Ordinal));

        ProfileMapping m = MachineProfileMapper.Map(p, new ProbeTarget(rtx.Luid, rtx.Outputs[0].Monitor));
        Assert.Equal(rtx.Luid, m.Profile.Gpu!.Luid);
        RecordingPlan plan = GovernorPolicy.Decide(m.Profile);
        Assert.True(plan.HardwareEncode);
        Assert.Equal(CodecKind.Hevc, plan.Tier.Codec); // AV1 is found but the ladder tops out at HEVC
    }

    [Fact]
    public void Each_capture_target_maps_to_the_adapter_that_owns_its_monitor()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "the machine probe is Windows only");
        MachineProbeResult p = Probe();
        List<(long Luid, int Output, Vortice.DXGI.OutputDescription Description)> outputs = [.. CaptureTarget.Outputs()];
        HashSet<long> hardware = [.. p.Adapters.Where(a => !a.IsSoftware).Select(a => a.Luid)];
        Assert.SkipWhen(!outputs.Any(o => hardware.Contains(o.Luid)), "no desktop output on a hardware adapter (headless or GPU-less session)");

        for (int i = 0; i < outputs.Count; i++)
        {
            if (!hardware.Contains(outputs[i].Luid))
            {
                continue; // the mapping never picks a software adapter
            }

            CaptureTarget t = CaptureTarget.ForMonitor(i);
            ProfileMapping m = MachineProfileMapper.Map(p, t.ToProbeTarget());
            output.WriteLine($"{t.Description} -> {m.Adapter?.Name} {m.Output?.DeviceName} {m.Profile.DisplayWidth}x{m.Profile.DisplayHeight}@{m.Profile.DisplayRefreshHz}");
            Assert.Equal(t.AdapterLuid, m.Adapter!.Luid);
            Assert.Equal(t.Monitor, (nint)m.Output!.Monitor);
            Assert.Equal(t.OutputIndex, m.Output.Index);
        }
    }

    [Fact]
    public void Missing_drive_is_recorded_not_thrown()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "the machine probe is Windows only");
        var used = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
        char free = "QRSTUVWXYZ".First(c => !used.Contains(c));
        MachineProbeResult p = WindowsMachineProbe.Run($@"{free}:\Lookback");
        Log(p);
        Assert.NotNull(p.Store);
        Assert.Null(p.Store!.FreeBytes);
        Assert.Contains(p.Unknowns, u => u.StartsWith("store", StringComparison.Ordinal));
        Assert.True(p.TotalRamBytes > 0); // the other queries still ran
        Assert.Equal(0, GovernorPolicy.Decide(MachineProfileMapper.Map(p).Profile).DiskBudgetBytes);
    }
}
