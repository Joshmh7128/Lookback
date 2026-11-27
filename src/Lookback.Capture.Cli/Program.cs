using System.Globalization;
using System.Text.Json;
using Lookback.Capture;
using Lookback.Capture.Targets;
using Lookback.Contracts.V1;
using Lookback.Core.Bus;
using Lookback.Profiler;
using Lookback.Stream.WebSockets;

// lookback-capture: capture a window or monitor live and run the signal
// pipeline on it. Nothing is written to disk.
//
//   lookback-capture --list
//   lookback-capture --window "Lookback Viewer" [--seconds 20] [--backend auto|wgc|dxgi] [--official-player] [--profiler]
//   lookback-capture --pid 1234 | --monitor 0

static string? Arg(string[] a, string name) { int i = Array.IndexOf(a, name); return i >= 0 && i + 1 < a.Length ? a[i + 1] : null; }

if (args.Length == 0 || args.Contains("--help") || args.Contains("-h"))
{
    Console.WriteLine("""
        lookback-capture --list
        lookback-capture (--window <title part> | --pid <pid> | --monitor <n>) [--seconds 15] [--backend auto|wgc|dxgi] [--official-player] [--profiler]
        Captures live and runs the signal pipeline. Nothing is written to disk.
        """);
    return 0;
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
double seconds = double.Parse(Arg(args, "--seconds") ?? "15", CultureInfo.InvariantCulture);
CaptureBackend backend = Enum.Parse<CaptureBackend>(Arg(args, "--backend") ?? "auto", ignoreCase: true);

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
}))
{
    Console.WriteLine($"capturing {target.Description} with {session.Backend} on {session.AdapterName}; persist: {session.CanPersist}");
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
        Console.WriteLine($"{st.Elapsed.TotalSeconds,5:F1}s  capture {(st.Captured - last.Captured) / dt,6:F1} fps  analysis {(st.Analysed - last.Analysed) / dt,5:F1} fps  dropped {st.AnalysisDropped}");
        last = st;
    }
}

var summary = names.Select((n, i) => new
{
    signal = n,
    mean = sums.Count == 0 ? 0 : sums.Average(v => v[i]),
    max = sums.Count == 0 ? 0 : sums.Max(v => v[i]),
}).ToList();
Console.WriteLine(JsonSerializer.Serialize(new { frames = sums.Count, signals = summary }, new JsonSerializerOptions { WriteIndented = true }));
return 0;
