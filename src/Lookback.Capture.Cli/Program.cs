using System.Globalization;
using System.Text.Json;
using Lookback.Capture;
using Lookback.Capture.Targets;
using Lookback.Contracts.V1;
using Lookback.Core.Bus;
using Lookback.Media.Frames;
using Lookback.Profiler;
using Lookback.Profiles.Model;
using Lookback.Profiles.Regions;
using Lookback.Signals.Video;
using Lookback.Stream.WebSockets;

// lookback-capture: capture a window or monitor live and run the signal
// pipeline on it. Nothing is written to disk unless --encode/--out asks for a
// clip, and never for an official-player session.
//
//   lookback-capture --list
//   lookback-capture --window "Lookback Viewer" [--seconds 20] [--backend auto|wgc|dxgi] [--official-player] [--profiler]
//   lookback-capture --pid 1234 | --monitor 0
//   lookback-capture --monitor 0 --seconds 20 --encode 10 --out clip.mp4 [--codec hevc] [--fps 60] [--size 1920x1080] [--mbps 20]
//   lookback-capture probe [--store <path>] [--monitor 0 | --window <title part>]

static string? Arg(string[] a, string name) { int i = Array.IndexOf(a, name); return i >= 0 && i + 1 < a.Length ? a[i + 1] : null; }

if (await HookBench.TryRunAsync(args) is int hookExit)
{
    return hookExit; // the present hook on our own test apps (HookBench.cs)
}

if (args.Length > 0 && args[0] == "probe")
{
    return ProbeCommand.Run(args[1..]);
}

if (args.Length == 0 || args.Contains("--help") || args.Contains("-h"))
{
    Console.WriteLine($"""
        lookback-capture --list
        lookback-capture (--window <title part> | --pid <pid> | --monitor <n>) [--seconds 15] [--backend auto|wgc|dxgi] [--official-player] [--profiler]
                         [--analysis <WxH>] [--regions <profile id>]
                         {EncodeBench.Usage}
        {HookBench.Usage}
        Captures live and runs the signal pipeline. --analysis sets the analysis frame
        (default 384x216); --regions adds a built-in profile's HUD region signals.
        Each line shows the capture-thread cost of the analysis tap and readback and
        the worker's signal time, in ms per analysed frame. Nothing is written to disk unless
        --encode asks for a clip of the last N seconds (refused with --official-player).
        lookback-capture probe [--store <path>] [--monitor <n> | --window <title part>]
        Prints the governor's machine profile and the plan it picks.
        """);
    return 0;
}

// Live audio on its own (AudioBench.cs).
if (args[0] == "audio")
{
    return await AudioBench.RunAsync(args[1..]);
}

if (args.Contains("--list"))
{
    int n = 0;
    foreach ((long luid, int output, Vortice.DXGI.OutputDescription d) in CaptureTarget.Outputs())
    {
        Console.WriteLine($"monitor {n++}: {d.DeviceName} {d.DesktopCoordinates.Right - d.DesktopCoordinates.Left}x{d.DesktopCoordinates.Bottom - d.DesktopCoordinates.Top} adapter {luid:X} output {output}");
    }

    Console.WriteLine($"WGC supported: {Windows.Graphics.Capture.GraphicsCaptureSession.IsSupported()}, borderless: {Lookback.Capture.Wgc.WgcCaptureSource.CanHideBorder}");
    return 0;
}

CaptureTarget? target = Arg(args, "--monitor") is string m ? CaptureTarget.ForMonitor(int.Parse(m, CultureInfo.InvariantCulture))
    : Arg(args, "--pid") is string pid ? CaptureTarget.FindWindow(null, uint.Parse(pid, CultureInfo.InvariantCulture))
    : CaptureTarget.FindWindow(Arg(args, "--window") ?? throw new ArgumentException("Give --window, --pid or --monitor."));
if (target is null)
{
    Console.Error.WriteLine("No matching window.");
    return 2;
}

bool official = args.Contains("--official-player");
if (official && args.Contains("--encode"))
{
    Console.Error.WriteLine("--encode is refused for --official-player: official-player sessions are ephemeral and nothing is encoded or written.");
    return 2;
}

double seconds = double.Parse(Arg(args, "--seconds") ?? "15", CultureInfo.InvariantCulture);
CaptureBackend backend = Enum.Parse<CaptureBackend>(Arg(args, "--backend") ?? "auto", ignoreCase: true);

AnalysisFormat analysis = AnalysisFormat.Parse(Arg(args, "--analysis") ?? "default");
IVideoSignal[] regionSignals = Arg(args, "--regions") is string regionProfile
    ? [new RegionSignal(ProfileCatalog.BuiltIn.SubtreeRegions(regionProfile), analysis)]
    : [];

var bus = new InProcessEventBus();
await using WebSocketBridge? bridge = args.Contains("--profiler") ? new WebSocketBridge(bus, new WebSocketBridgeOptions { Pages = [ProfilerPage.Page] }) : null;
if (bridge is not null)
{
    await bridge.StartAsync();
    Console.WriteLine($"profiler: {bridge.PageUrl(ProfilerPage.Path)}");
}

using IEventSubscription video = bus.Subscribe("signals.video", 1 << 16);
string[] names = [];
var sums = new List<double[]>();

await using (LiveCaptureSession session = LiveCaptureSession.Start(bus, new LiveSessionOptions
{
    Target = target,
    Backend = backend,
    SourceKind = official ? SourceKind.OfficialPlayer : SourceKind.LiveGame,
    Retention = official ? Retention.Ephemeral : Retention.Owned,
    Title = target.Description,
    Analysis = analysis,
    ExtraVideoSignals = regionSignals,
}))
{
    Console.WriteLine($"capturing {target.Description} with {session.Backend} on {session.AdapterName}; analysis {analysis.Size}; persist: {session.CanPersist}");
    using EncodeBench? encode = EncodeBench.TryStart(args, session);
    var until = DateTime.UtcNow.AddSeconds(seconds);
    SessionStats last = session.Stats;
    while (DateTime.UtcNow < until && session.StoppedReason is null)
    {
        await Task.Delay(1000);
        while (video.TryRead(out Envelope? e))
        {
            if (e!.SignalSchema is { } s)
            {
                names = [.. s.Names];
            }
            else if (e.SignalFrame is { } f)
            {
                sums.Add([.. f.Values.Select(v => (double)v)]);
            }
        }

        SessionStats st = session.Stats;
        double dt = (st.Elapsed - last.Elapsed).TotalSeconds;
        long analysed = Math.Max(1, st.Analysed - last.Analysed);
        Console.WriteLine($"{st.Elapsed.TotalSeconds,5:F1}s  capture {(st.Captured - last.Captured) / dt,6:F1} fps  analysis {(st.Analysed - last.Analysed) / dt,5:F1} fps  dropped {st.AnalysisDropped}"
            + $"  tap {(st.TapBusy - last.TapBusy).TotalMilliseconds / analysed,5:F2} ms (readback {(st.ReadbackBusy - last.ReadbackBusy).TotalMilliseconds / analysed,5:F2}, waits {st.ReadbackWaits})"
            + $"  signals {(st.AnalysisBusy - last.AnalysisBusy).TotalMilliseconds / analysed,6:F2} ms{encode?.Tick(dt)}");
        last = st;
    }

    encode?.Finish();
    SessionStats end = session.Stats;
    long total = Math.Max(1, end.Analysed);
    Console.WriteLine($"total  {end.Captured} captured, {end.Analysed} analysed, {end.AnalysisDropped} dropped; tap {end.TapBusy.TotalMilliseconds / total:F2} ms/frame (readback {end.ReadbackBusy.TotalMilliseconds / total:F2}, waits {end.ReadbackWaits}); signals {end.AnalysisBusy.TotalMilliseconds / total:F2} ms/frame, max {end.AnalysisBusyMax.TotalMilliseconds:F1} ms");
}

var summary = names.Select((n, i) => new
{
    signal = n,
    mean = sums.Count == 0 ? 0 : sums.Average(v => v[i]),
    max = sums.Count == 0 ? 0 : sums.Max(v => v[i]),
}).ToList();
Console.WriteLine(JsonSerializer.Serialize(new { frames = sums.Count, signals = summary }, new JsonSerializerOptions { WriteIndented = true }));
return 0;
