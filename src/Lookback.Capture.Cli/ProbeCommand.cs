using System.Globalization;
using Lookback.Capture.Targets;
using Lookback.Governor;
using Lookback.Governor.Windows;

/// <summary>
/// <c>lookback-capture probe [--store &lt;path&gt;] [--monitor &lt;n&gt; | --window &lt;title part&gt;]</c>:
/// runs the governor's machine probe, maps it for the capture target and
/// prints the profile and the plan the policy picks. Reads only; writes nothing.
/// </summary>
internal static class ProbeCommand
{
    private const double GB = 1L << 30;

    public static int Run(string[] args)
    {
        string store = Arg(args, "--store") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lookback");
        ProbeTarget target = default;
        string targetName = "primary monitor";
        if (Arg(args, "--monitor") is string m)
        {
            CaptureTarget t = CaptureTarget.ForMonitor(int.Parse(m, CultureInfo.InvariantCulture));
            (target, targetName) = (t.ToProbeTarget(), t.Description);
        }
        else if (Arg(args, "--window") is string w)
        {
            if (CaptureTarget.FindWindow(w) is not CaptureTarget t)
            {
                Console.Error.WriteLine("No matching window.");
                return 2;
            }

            (target, targetName) = (t.ToProbeTarget(), t.Description);
        }

        MachineProbeResult probe = WindowsMachineProbe.Run(store);
        ProfileMapping map = MachineProfileMapper.Map(probe, target);
        RecordingPlan plan = GovernorPolicy.Decide(map.Profile);

        Console.WriteLine($"probe: {probe.Elapsed.TotalMilliseconds:F0} ms ({string.Join(", ", probe.Timings.Select(kv => $"{kv.Key} {kv.Value.TotalMilliseconds:F0}"))})");
        Console.WriteLine($"memory: {Gb(probe.TotalRamBytes)} total, {Gb(probe.AvailableRamBytes)} available; {probe.CpuLogicalCores} logical cores");
        if (probe.Store is StoreProbe s)
        {
            string kind = s.IsSsd switch { true => "SSD", false => "HDD", null => "unknown" };
            Console.WriteLine($"store: {s.Path} on {s.Root ?? "?"}: {Gb(s.FreeBytes)} free of {Gb(s.TotalBytes)}, {kind}, {s.Bus} bus{(s.Model is null ? "" : $", {s.Model}")}");
        }

        foreach (AdapterProbe a in probe.Adapters)
        {
            Console.WriteLine($"adapter {a.Luid:X}: {a.Name} ({a.Vendor}{(a.IsSoftware ? ", software" : "")}), {a.DedicatedVramBytes / GB:F1} GB VRAM");
            foreach (OutputProbe o in a.Outputs)
            {
                Console.WriteLine($"  output {o.Index}: {o.DeviceName} {o.Width}x{o.Height}@{o.RefreshHz?.ToString(CultureInfo.InvariantCulture) ?? "?"}{(o.IsPrimary ? " primary" : "")}");
            }

            if (a.HardwareEncoders is { } enc)
            {
                foreach ((Lookback.Replay.Packets.CodecKind codec, IReadOnlyList<string> names) in enc)
                {
                    Console.WriteLine($"  {codec}: {(names.Count == 0 ? "none" : string.Join(", ", names))}");
                }
            }
        }

        foreach (string u in probe.Unknowns)
        {
            Console.WriteLine($"unknown: {u}");
        }

        MachineProfile p = map.Profile;
        Console.WriteLine();
        Console.WriteLine($"target: {targetName} -> {map.Adapter?.Name ?? "no adapter"} {map.Output?.DeviceName}");
        Console.WriteLine($"profile: {p.TotalRamBytes / GB:F1} GB RAM ({p.AvailableRamBytes / GB:F1} available), {p.CpuCores} cores, store {p.StoreVolume.Root} {p.StoreVolume.FreeBytes / GB:F0}/{p.StoreVolume.TotalBytes / GB:F0} GB {(p.StoreVolume.IsSsd ? "SSD" : "HDD")}, display {p.DisplayWidth}x{p.DisplayHeight}@{p.DisplayRefreshHz}, encoders {(p.Gpu is null ? "software only" : string.Join("/", p.Gpu.HardwareEncoders.Order()))}");
        foreach (string n in map.Notes)
        {
            Console.WriteLine($"  note: {n}");
        }

        Console.WriteLine($"plan: {plan.Tier.Name} ({(plan.HardwareEncode ? "hardware" : "software")}, {plan.Tier.BitrateBps / 1e6:F0} Mbps), ring {plan.RingBytes >> 20} MB = {plan.RingLength.TotalSeconds:F0} s, disk {plan.DiskBudgetBytes / GB:F0} GB = {plan.SessionLength.TotalHours:F1} h, floor {plan.FreeSpaceFloorBytes / GB:F0} GB");
        foreach (string r in plan.Reasons)
        {
            Console.WriteLine($"  why: {r}");
        }

        return 0;
    }

    private static string Gb(long? bytes) => bytes is long b ? $"{b / GB:F1} GB" : "?";

    private static string? Arg(string[] a, string name)
    {
        int i = Array.IndexOf(a, name);
        return i >= 0 && i + 1 < a.Length ? a[i + 1] : null;
    }
}
