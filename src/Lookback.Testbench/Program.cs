using System.Globalization;
using System.Runtime.InteropServices;
using Lookback.Contracts.V1;
using Lookback.Stream.SessionLog;
using Lookback.Testbench;
using Lookback.Testbench.Datasets;
using Lookback.Testbench.Live;

const string Usage = """
    lookback-testbench: run footage through Lookback's signals and render a timeline.

    Usage:
      lookback-testbench analyze <file> [--out <dir>] [--retention owned|permissioned|open-license|ephemeral]
                                        [--permission-ref <ref>] [--title <text>] [--game <name>]
                                        [--profile <id-or-path>]
      lookback-testbench dataset <manifest.json> [--out <dir>] [--tolerance <seconds>] [--profile <id-or-path>]
      lookback-testbench report <session-path-without-extension> [--video <file>]
      lookback-testbench live <file> [--profile <id-or-path>] [--game <name>] [--speed <x>] [--loop]
                                     [--port <n>] [--no-wait] [--pipe <name>]
                                     [--retention ...] [--permission-ref <ref>] [--title <text>]

    Live plays the footage in real time, runs the profile's rules on it, and
    shows every event in the live profiler as it fires: it prints a link to
    open in a browser and starts the clip when the page connects. With --pipe it sends to a running recorder instead
    (start the recorder with --Recorder:Profiler=true and open its link).
    Nothing is written to disk.

    Profiles:
      Built in: generic, overwatch. A path to a profile JSON file also works.
      With a profile, the report gains an events lane, rule-margin lanes and a
      calibration overlay of the profile's HUD regions and the detected HUD mask.

    Retention:
      owned         your own footage (default)
      permissioned  someone else's footage used with permission; needs --permission-ref
      open-license  footage under a licence that allows this use; needs --permission-ref
      ephemeral     analyse and print a summary; nothing is written to disk
    """;

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
// `live` keeps serving until stopped; treat a termination request like Ctrl+C.
using PosixSignalRegistration term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, c => { c.Cancel = true; cts.Cancel(); });

try
{
    return args switch
    {
        ["analyze", string file, .. var rest] => await AnalyzeAsync(file, ParseFlags(rest), cts.Token),
        ["dataset", string manifest, .. var rest] => await DatasetAsync(manifest, ParseFlags(rest), cts.Token),
        ["report", string session, .. var rest] => await ReportAsync(session, ParseFlags(rest), cts.Token),
        ["live", string file, .. var rest] => await LiveAsync(file, ParseFlags(rest), cts.Token),
        _ => PrintUsage(),
    };
}
catch (RetentionViolationException ex)
{
    await Console.Error.WriteLineAsync($"Refused: {ex.Message}");
    return 2;
}
catch (OperationCanceledException)
{
    return 130;
}
catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException or ArgumentException or TimeoutException)
{
    await Console.Error.WriteLineAsync($"Error: {ex.Message}");
    return 1;
}

static int PrintUsage()
{
    Console.Error.WriteLine(Usage);
    return 64;
}

static async Task<int> AnalyzeAsync(string file, Dictionary<string, string> flags, CancellationToken ct)
{
    var options = new AnalyzeOptions
    {
        File = file,
        OutputDirectory = flags.GetValueOrDefault("out", Path.Combine(Environment.CurrentDirectory, "testbench-out")),
        Retention = ParseRetention(flags.GetValueOrDefault("retention", "owned")),
        PermissionRef = flags.GetValueOrDefault("permission-ref", ""),
        Title = flags.GetValueOrDefault("title"),
        Game = flags.GetValueOrDefault("game"),
        Profile = flags.GetValueOrDefault("profile"),
    };

    var progress = new Progress<TimeSpan>(t => Console.Error.Write(string.Create(CultureInfo.InvariantCulture, $"\r  analysed {t:hh\\:mm\\:ss}")));
    AnalyzeResult r = await TestbenchRunner.AnalyzeAsync(options, progress, ct);
    Console.Error.WriteLine();

    double speed = r.Media.Duration.TotalSeconds / Math.Max(0.001, r.Analysis.Elapsed.TotalSeconds);
    Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
        $"session  {r.SessionId}\nmedia    {r.Media.Width}x{r.Media.Height} @ {r.Media.FramesPerSecond:0.##} fps, {r.Media.Duration:hh\\:mm\\:ss}, audio: {(r.Media.HasAudio ? "yes" : "no")}\nsignals  {r.Analysis.VideoFrames} video frames, {r.Analysis.AudioHops} audio hops in {r.Analysis.Elapsed.TotalSeconds:0.0} s ({speed:0.0}x real time)"));
    Console.WriteLine(r.ReportPath is null
        ? "stored   nothing (ephemeral)"
        : $"log      {r.SessionBasePath}.lbklog\nreport   {r.ReportPath}");
    foreach (string warning in r.Warnings)
    {
        Console.WriteLine($"warning  {warning}");
    }

    if (options.Profile is not null)
    {
        Console.WriteLine($"events   {r.Events.Count} from profile '{options.Profile}'");
        foreach (var group in r.Events.GroupBy(e => e.Name, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"         {group.Key,-24} {group.Count(),5}"));
        }
    }

    return 0;
}

static async Task<int> DatasetAsync(string manifest, Dictionary<string, string> flags, CancellationToken ct)
{
    string output = flags.GetValueOrDefault("out", Path.Combine(Environment.CurrentDirectory, "dataset-out"));
    double tolerance = double.Parse(flags.GetValueOrDefault("tolerance", "1.0"), CultureInfo.InvariantCulture);
    var (entries, overall) = await DatasetRunner.RunAsync(manifest, output, tolerance, flags.GetValueOrDefault("profile"), ct);

    foreach (DatasetEntryResult e in entries)
    {
        Console.WriteLine($"{e.File}: {e.Events} events, report {e.Report}");
    }

    Console.WriteLine();
    Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{"event",-24} {"TP",5} {"FP",5} {"FN",5} {"precision",10} {"recall",8}  (tolerance {tolerance:0.##} s)"));
    foreach (EventScore s in overall)
    {
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{s.Event,-24} {s.TruePositives,5} {s.FalsePositives,5} {s.FalseNegatives,5} {Pct(s.Precision),10} {Pct(s.Recall),8}"));
    }

    Console.WriteLine($"\nscores   {Path.Combine(output, "scores.json")}");
    return 0;

    static string Pct(double v) => double.IsNaN(v) ? "-" : v.ToString("P0", CultureInfo.InvariantCulture);
}

static async Task<int> ReportAsync(string session, Dictionary<string, string> flags, CancellationToken ct)
{
    string path = await TestbenchRunner.ReportAsync(session, flags.GetValueOrDefault("video"), ct);
    Console.WriteLine($"report   {path}");
    return 0;
}

static async Task<int> LiveAsync(string file, Dictionary<string, string> flags, CancellationToken ct)
{
    var options = new LiveOptions
    {
        File = file,
        Retention = ParseRetention(flags.GetValueOrDefault("retention", "owned")),
        PermissionRef = flags.GetValueOrDefault("permission-ref", ""),
        Title = flags.GetValueOrDefault("title"),
        Game = flags.GetValueOrDefault("game"),
        Profile = flags.GetValueOrDefault("profile"),
        Speed = double.Parse(flags.GetValueOrDefault("speed", "1"), CultureInfo.InvariantCulture),
        PipeName = flags.GetValueOrDefault("pipe"),
        Port = int.Parse(flags.GetValueOrDefault("port", "0"), CultureInfo.InvariantCulture),
        WaitForViewer = !flags.ContainsKey("no-wait"),
        Loop = flags.ContainsKey("loop"),
        KeepServing = true,
    };

    LiveResult r = await LiveRunner.RunAsync(options, Console.WriteLine, ct);
    Console.WriteLine($"sent     {r.Released} events in {r.Plays} play(s)");
    return 0;
}

static Retention ParseRetention(string value) => value.ToLowerInvariant() switch
{
    "owned" => Retention.Owned,
    "permissioned" => Retention.Permissioned,
    "open-license" => Retention.OpenLicense,
    "ephemeral" => Retention.Ephemeral,
    _ => throw new ArgumentException($"Unknown retention '{value}'."),
};

static Dictionary<string, string> ParseFlags(string[] args)
{
    var flags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (int i = 0; i < args.Length; i++)
    {
        if (args[i] is "--loop" or "--no-wait")
        {
            flags[args[i][2..]] = "true";
            continue;
        }

        if (!args[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length)
        {
            throw new ArgumentException($"Unexpected argument '{args[i]}'.");
        }

        flags[args[i][2..]] = args[++i];
    }

    return flags;
}
