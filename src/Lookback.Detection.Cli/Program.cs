using System.Globalization;
using Lookback.Capture.Targets;
using Lookback.Contracts.V1;
using Lookback.Core.Bus;
using Lookback.Core.Time;
using Lookback.Detection;
using Lookback.Detection.Lifecycle;
using Lookback.Detection.Matching;
using Lookback.Detection.Probe;
using Lookback.Detection.Windows;
using Lookback.Profiles.Model;

// lookback-detect: watch for games on this machine, from the process list and
// top-level windows only (no memory reading, no hooks). Writes nothing except
// the user's own allow/deny settings, and only for allow/deny/forget.
//
//   lookback-detect [--seconds 60] [--interval 1000] [--start-hold 3] [--settings <path>] [--profiles a.json,b.json]
//   lookback-detect windows [--all]
//   lookback-detect settings | allow <exe> [--profile generic] | deny <exe> | forget <exe>

static string? Arg(string[] a, string name)
{
    int i = Array.IndexOf(a, name);
    return i >= 0 && i + 1 < a.Length ? a[i + 1] : null;
}

static double Num(string[] a, string name, double fallback) =>
    Arg(a, name) is string s ? double.Parse(s, CultureInfo.InvariantCulture) : fallback;

Console.OutputEncoding = System.Text.Encoding.UTF8;

if (args.Contains("--help") || args.Contains("-h"))
{
    Console.WriteLine("""
        lookback-detect [--seconds N] [--interval ms] [--start-hold s] [--settings path] [--profiles a.json,b.json]
            Prints game lifecycle changes live (Ctrl+C to stop), with the capture target and policy.
        lookback-detect windows [--all]
            One look: every capture-sized window, its process, mode and monitor, and why it is or is not a game.
        lookback-detect settings | allow <exe or path> [--profile id] | deny <exe or path> | forget <exe or path>
            Shows or edits the user's allow and deny lists.
        """);
    return 0;
}

string settingsPath = Arg(args, "--settings") ?? DetectionSettings.DefaultPath;
ProfileCatalog catalog = Arg(args, "--profiles") is string extra ? ProfileCatalog.WithFiles(extra.Split(',', StringSplitOptions.RemoveEmptyEntries)) : ProfileCatalog.BuiltIn;
string verb = args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal) ? args[0] : "watch";

switch (verb)
{
    case "settings":
        PrintSettings(DetectionSettings.Load(settingsPath), settingsPath);
        return 0;
    case "allow" or "deny" or "forget":
        if (args.Length < 2)
        {
            Console.Error.WriteLine($"{verb} needs an executable name or path.");
            return 2;
        }

        DetectionSettings s = DetectionSettings.Load(settingsPath);
        string exe = args[1];
        string profile = Arg(args, "--profile") ?? "generic";
        if (verb == "allow" && !catalog.Contains(profile))
        {
            Console.Error.WriteLine($"No profile '{profile}'.");
            return 2;
        }

        s = verb switch
        {
            "allow" => s.WithAllowed(exe, profile),
            "deny" => s.WithDenied(exe),
            _ => s with
            {
                Allow = [.. s.Allow.Where(a => !string.Equals(a.Exe, exe, StringComparison.OrdinalIgnoreCase))],
                Deny = [.. s.Deny.Where(d => !string.Equals(d, exe, StringComparison.OrdinalIgnoreCase))],
            },
        };
        s.Save(settingsPath);
        PrintSettings(s, settingsPath);
        return 0;
    case "windows":
        return ListWindows(catalog, DetectionSettings.Load(settingsPath), args.Contains("--all"));
    case "watch":
        break;
    default:
        Console.Error.WriteLine($"Unknown command '{verb}'; see --help.");
        return 2;
}

var options = new DetectionOptions
{
    PollInterval = TimeSpan.FromMilliseconds(Num(args, "--interval", 1000)),
    StartHold = TimeSpan.FromSeconds(Num(args, "--start-hold", 3)),
};
var matcher = new GameMatcher(catalog, DetectionSettings.Load(settingsPath));
foreach (string w in matcher.Warnings)
{
    Console.Error.WriteLine($"warning: {w}");
}

using var bus = new InProcessEventBus();
using IEventSubscription lifecycle = bus.Subscribe(Lookback.Contracts.Topics.GameLifecycle);
long t0 = SystemClock.Instance.Now;
double Secs(long qpc) => QpcTime.ToTimeSpan(qpc - t0, SystemClock.Instance.Frequency).TotalSeconds;

var runner = new DetectionRunner(bus, new WindowsSystemProbe(), matcher, SystemClock.Instance, options);
await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable runnerScope = runner.ConfigureAwait(false);
runner.Changed += c =>
{
    GameInstance g = c.Game;
    Console.WriteLine($"{Secs(c.At),8:0.00}s  {c.Kind,-14} {g.Match.ProfileId} ({g.Match.Method}) pid {g.Pid} {g.ExeName}  [{g.Id}]");
    if (c.Kind is GameLifecycle.Types.Kind.Detected or GameLifecycle.Types.Kind.WindowChanged)
    {
        CaptureTarget? target = WindowsDetection.TryCaptureTarget(g);
        Console.WriteLine($"           window 0x{g.Window.Handle:X} \"{g.Window.Title}\" {g.Window.Width}x{g.Window.Height} {g.Window.Mode}, adapter {g.AdapterLuid:X}");
        Console.WriteLine($"           capture: {target?.Description ?? "window unavailable; capture its monitor"}");
        Console.WriteLine($"           policy: presentHook={g.Policy.AllowPresentHook} memoryReading={g.Policy.AllowMemoryReading}  engine: {(g.Engine.Length > 0 ? g.Engine : "?")}  why: {g.Match.Reason}");
        if (g.Hints.LaunchArgs.Length > 0)
        {
            Console.WriteLine($"           launch args: {g.Hints.LaunchArgs}");
        }
    }
};
runner.HintsChanged += g => Console.WriteLine($"{Secs(SystemClock.Instance.Now),8:0.00}s  title          {g.Match.ProfileId} \"{g.Window.Title}\"");

Console.WriteLine($"Watching for games every {options.PollInterval.TotalMilliseconds:0} ms ({catalog.All.Count(p => p.Level == ProfileLevelKind.Game)} game profiles, settings {settingsPath}). Ctrl+C to stop.");
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};
double seconds = Num(args, "--seconds", 0);
if (seconds > 0)
{
    cts.CancelAfter(TimeSpan.FromSeconds(seconds));
}

var pendingShown = new Dictionary<uint, string>();
try
{
    while (!cts.IsCancellationRequested)
    {
        await Task.Delay(options.PollInterval, cts.Token).ConfigureAwait(false);
        foreach ((uint pid, string exe, string note) in runner.Pending)
        {
            if (note.Length > 0 && (!pendingShown.TryGetValue(pid, out string? last) || last != note))
            {
                pendingShown[pid] = note;
                Console.WriteLine($"{Secs(SystemClock.Instance.Now),8:0.00}s  (not yet)      pid {pid} {exe}: {note}");
            }
        }

        if (runner.LastError is Exception ex)
        {
            Console.Error.WriteLine($"poll error: {ex.Message}");
        }
    }
}
catch (OperationCanceledException)
{
}

Console.WriteLine($"{runner.Polls} polls, {runner.Published} lifecycle envelopes on the bus ({lifecycle.Count} queued for this tool), {runner.Games.Count} game(s) running.");
return 0;

static int ListWindows(ProfileCatalog catalog, DetectionSettings settings, bool all)
{
    var probe = new WindowsSystemProbe();
    var matcher = new GameMatcher(catalog, settings);
    SystemSnapshot snap = probe.Snapshot();
    var names = snap.Processes.GroupBy(p => p.Pid).ToDictionary(g => g.Key, g => g.First().ExeName);
    var options = new DetectionOptions();
    Console.WriteLine($"{"pid",7} {"exe",-32} {"mode",-10} {"size",-11} {"monitor",-10} fg  verdict / title");
    foreach (WindowEntry w in snap.Windows.OrderBy(w => w.Pid))
    {
        bool big = w.Width >= options.MinWindowWidth && w.Height >= options.MinWindowHeight;
        if (!all && !big && !w.Minimized)
        {
            continue;
        }

        string exe = names.GetValueOrDefault(w.Pid, "?");
        string verdict;
        if (exe == "?")
        {
            verdict = "process gone";
        }
        else
        {
            string? path = matcher.IsCandidate(exe) ? probe.Details(w.Pid)?.ExePath : null;
            GameMatch? m = matcher.Match(exe, path, w.Title, out string note);
            verdict = m is not null ? $"GAME {m.ProfileId} ({m.Method})" : note;
        }

        string fg = w.Handle == snap.Foreground ? "*" : " ";
        Console.WriteLine($"{w.Pid,7} {Trim(exe, 32),-32} {(w.Minimized ? "minimized" : w.Mode.ToString()),-10} {w.Width + "x" + w.Height,-11} {w.Monitor,-10:X} {fg}   {verdict}");
        Console.WriteLine($"{"",7} \"{Trim(w.Title, 100)}\"");
    }

    return 0;
}

static string Trim(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";

static void PrintSettings(DetectionSettings s, string path)
{
    Console.WriteLine($"settings: {path}");
    Console.WriteLine(s.Allow.Count == 0 ? "allow: (none)" : "allow:");
    foreach (AllowEntry a in s.Allow)
    {
        Console.WriteLine($"  {a.Exe} -> {a.Profile}");
    }

    Console.WriteLine(s.Deny.Count == 0 ? "deny: (none)" : "deny:");
    foreach (string d in s.Deny)
    {
        Console.WriteLine($"  {d}");
    }
}
