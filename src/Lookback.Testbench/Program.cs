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
                                        [--profile <id-or-path>] [--profiles <file-or-folder>]
      lookback-testbench dataset <manifest.json> [--out <dir>] [--tolerance <seconds>] [--profile <id-or-path>]
                                        [--profiles <file-or-folder>]
      lookback-testbench fingerprint <manifest.json> [--out <dir>] [--profiles <file-or-folder>]
      lookback-testbench profiles [--id <kind>=<value>] [--tag <tag>]
      lookback-testbench roblox-ids [--dir profiles/roblox] [--write true]
      lookback-testbench browse [--datasets <dir>] [--results <dir>[,<dir>...]] [--profiles <file-or-folder>] [--out <file>]
      lookback-testbench report <session-path-without-extension> [--video <file>]
      lookback-testbench live <file> [--profile <id-or-path>] [--game <name>] [--speed <x>] [--loop]
                                     [--port <n>] [--no-wait] [--pipe <name>]
                                     [--retention ...] [--permission-ref <ref>] [--title <text>]

    Analyze and live always run the clipper: the excitement curve over the
    signals, fused with the profile's events when there is a profile, into clip
    candidates, each with a QA report.

    Live plays the footage in real time, runs the profile's rules and the
    clipper on it, and shows every event in the live profiler as it fires: it prints a link to
    open in a browser and starts the clip when the page connects. With --pipe it sends to a running recorder instead
    (start the recorder with --Recorder:Profiler=true and open its link).
    Nothing is written to disk.

    Profiles:
      Profiles form a tree: game, then sub-game (a Roblox experience), then mode.
      Start from a game (--profile roblox) and the sub-game and mode are detected;
      or start lower (--profile roblox.arsenal) to choose them by hand. A path to
      a profile JSON file also works. --profiles adds profile files to the tree,
      for example the learned fingerprints that `fingerprint` writes.
      `profiles` prints the tree.
      With a profile, the report gains an events lane, rule-margin lanes and a
      calibration overlay of the profile's HUD regions and the detected HUD mask.

    Browse:
      Writes one page for sifting the profile tree and the datasets: what each
      profile identifies and from which signals and regions, how it is detected,
      which events the labelled footage tests, and (with --results, the --out
      folders of `dataset` runs) how well each event scores. Reads only.

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
        ["fingerprint", string manifest, .. var rest] => await FingerprintAsync(manifest, ParseFlags(rest), cts.Token),
        ["profiles", .. var rest] => PrintProfiles(ParseFlags(rest)),
        ["roblox-ids", .. var rest] => await RobloxIdsAsync(ParseFlags(rest), cts.Token),
        ["browse", .. var rest] => await BrowseAsync(ParseFlags(rest), cts.Token),
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
        ExtraProfiles = Extra(flags),
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

    foreach (ProfileDetected d in r.Detections)
    {
        double at = d.AtQpc / (double)TestbenchRunner.Frequency;
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"profile  {at,7:0.0} s  {d.ProfileId} ({d.Method.ToString().ToLowerInvariant()}, {d.Confidence:P0})"));
    }

    if (options.Profile is not null)
    {
        Console.WriteLine($"events   {r.Events.Count} from profile '{options.Profile}'");
        foreach (var group in r.Events.GroupBy(e => e.Name, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"         {group.Key,-24} {group.Count(),5}"));
        }

    }

    int passed = r.Candidates.Count(c => r.Qa.TryGetValue(c.CandidateId, out QaReport? q) && q.Passed);
    Console.WriteLine($"clips    {r.Candidates.Count} candidates, {passed} pass QA");
    foreach (ClipCandidate c in r.Candidates.OrderByDescending(c => c.HeuristicScore).Take(10))
    {
        string qa = r.Qa.TryGetValue(c.CandidateId, out QaReport? q)
            ? q.Passed ? "QA ok  " : "QA fail (" + string.Join(", ", q.Checks.Where(k => !k.Passed).Select(k => k.Name)) + ")"
            : "";
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"         {c.StartQpc / (double)TestbenchRunner.Frequency,7:0.0}-{c.EndQpc / (double)TestbenchRunner.Frequency,-7:0.0} s  score {c.HeuristicScore,4:0.00}  {qa}  {string.Join("; ", c.Reasons)}"));
    }

    return 0;
}

static async Task<int> DatasetAsync(string manifest, Dictionary<string, string> flags, CancellationToken ct)
{
    string output = flags.GetValueOrDefault("out", Path.Combine(Environment.CurrentDirectory, "dataset-out"));
    double tolerance = double.Parse(flags.GetValueOrDefault("tolerance", "1.0"), CultureInfo.InvariantCulture);
    DatasetResult result = await DatasetRunner.RunAsync(manifest, output, tolerance, flags.GetValueOrDefault("profile"), ct, Extra(flags));
    IReadOnlyList<EventScore> overall = result.Overall;

    foreach (DatasetEntryResult e in result.Entries)
    {
        string detection = e.ExpectedProfile is null ? "" : string.Create(CultureInfo.InvariantCulture, $", detected {e.FinalProfile} (expected {e.ExpectedProfile}, right {e.DetectionAccuracy:P0} of the time)");
        Console.WriteLine($"{e.File}: {e.Events} events{detection}, report {e.Report}");
    }

    if (result.DetectionAccuracy is { } accuracy)
    {
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"\ndetection  right profile {accuracy:P0} of labelled time"));
    }

    if (result.Candidates is { } c)
    {
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"candidates {c.TruePositives} match labelled clips, {c.FalsePositives} extra, {c.FalseNegatives} missed: precision {Pct(c.Precision)}, recall {Pct(c.Recall)}, mean overlap {c.MeanIoU:0.00}"));
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

static async Task<int> FingerprintAsync(string manifest, Dictionary<string, string> flags, CancellationToken ct)
{
    string output = flags.GetValueOrDefault("out", Path.Combine(Environment.CurrentDirectory, "fingerprint-out"));
    IReadOnlyList<LearnedFingerprint> learned = await FingerprintLearner.RunAsync(manifest, output, Extra(flags), ct);
    foreach (LearnedFingerprint f in learned)
    {
        Console.WriteLine($"{f.Profile}  ({f.PositiveFrames} frames of it, {f.NegativeFrames} of others)");
        if (f.Problem is not null)
        {
            Console.WriteLine($"  not learned: {f.Problem}");
            continue;
        }

        foreach (LearnedCondition c in f.Conditions)
        {
            Console.WriteLine("  " + FingerprintLearner.Describe(c));
        }

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  minScore {f.MinScore:0.00}"));
    }

    Console.WriteLine($"\nprofiles  {Path.Combine(output, "learned")}  (use with --profiles, then check with the dataset command)");
    return 0;
}

static int PrintProfiles(Dictionary<string, string> flags)
{
    Lookback.Profiles.Model.ProfileCatalog catalog = Lookback.Profiles.Model.ProfileCatalog.BuiltIn;
    if (flags.TryGetValue("id", out string? id))
    {
        string[] kv = id.Split('=', 2);
        var hit = kv.Length == 2 ? catalog.FindById(kv[0], kv[1]) : null;
        Console.WriteLine(hit is null ? $"no profile lists {id}" : $"{hit.Id}  {hit.DisplayName}  [{string.Join(", ", hit.Tags)}]");
        return hit is null ? 1 : 0;
    }

    if (flags.TryGetValue("tag", out string? tag))
    {
        foreach (var p in catalog.Tagged(tag))
        {
            Console.WriteLine($"{p.Id,-40} {p.DisplayName}");
        }

        return 0;
    }

    foreach (var game in catalog.All.Where(p => string.IsNullOrEmpty(p.Parent)).OrderBy(p => p.Id, StringComparer.Ordinal))
    {
        foreach (var p in catalog.Subtree(game.Id))
        {
            int depth = catalog.PathTo(p.Id).Count - 1;
            var resolved = catalog.Resolve(p.Id);
            string how = p.Level == Lookback.Profiles.Model.ProfileLevelKind.Game
                ? string.Join(", ", p.Executables)
                : $"{p.Detect.Hints.Count} hints, {(p.Detect.Visual.Count == 0 ? "no fingerprint yet" : $"{p.Detect.Visual.Count}-condition fingerprint")}";
            Console.WriteLine($"{new string(' ', depth * 2)}{p.Id.PadRight(28 - (depth * 2))} {p.Level.ToString().ToLowerInvariant(),-8} {resolved.Events.Count,2} events  {how}");
        }
    }

    return 0;
}

static async Task<int> RobloxIdsAsync(Dictionary<string, string> flags, CancellationToken ct)
{
    using var http = new HttpClient();
    http.DefaultRequestHeaders.UserAgent.ParseAdd("lookback-testbench/1.0");
    bool write = flags.GetValueOrDefault("write") is "true" or "yes";
    var results = await Lookback.Testbench.Catalog.RobloxIdResolver.RunAsync(flags.GetValueOrDefault("dir", Path.Combine("profiles", "roblox")), write, http, ct);
    Console.WriteLine($"{"profile",-42} {"universe",-12} {"root place",-16} {"playing",9}  name on Roblox (how found)");
    foreach (var r in results)
    {
        string flag = r.Problem is not null ? $"  PROBLEM: {r.Problem}" : r.NameMatches ? "" : "  CHECK: name differs";
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{r.Profile,-42} {r.UniverseId?.ToString(CultureInfo.InvariantCulture) ?? "-",-12} {r.RootPlaceId?.ToString(CultureInfo.InvariantCulture) ?? "-",-16} {r.Playing,9}  {r.RobloxName} ({r.Source}){flag}"));
    }

    Console.WriteLine(write ? "\nIds written for every profile whose name matched." : "\nDry run. Add --write true to write ids where the name matches.");
    return results.Any(r => r.Problem is not null) ? 1 : 0;
}

static async Task<int> BrowseAsync(Dictionary<string, string> flags, CancellationToken ct)
{
    string output = Path.GetFullPath(flags.GetValueOrDefault("out", Path.Combine(Environment.CurrentDirectory, "browse-out", "index.html")));
    string? datasets = flags.GetValueOrDefault("datasets") ?? Lookback.Testbench.Browse.BrowserBuilder.FindDatasets(Environment.CurrentDirectory);
    string[] results = flags.TryGetValue("results", out string? r) ? r.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : [];
    string[] extra = [.. Extra(flags).SelectMany(TestbenchRunner.ExpandProfilePath)];
    Lookback.Profiles.Model.ProfileCatalog catalog = extra.Length > 0 ? Lookback.Profiles.Model.ProfileCatalog.WithFiles(extra) : Lookback.Profiles.Model.ProfileCatalog.BuiltIn;

    var data = Lookback.Testbench.Browse.BrowserBuilder.Build(catalog, datasets, results, output);
    await Lookback.Testbench.Browse.BrowserWriter.WriteAsync(data, output, ct);
    Console.WriteLine($"profiles {data.Profiles.Count} in {data.Games.Count} games");
    Console.WriteLine($"datasets {data.Datasets.Count} manifests, {data.Datasets.Sum(d => d.Entries.Count)} entries" + (datasets is null ? "" : $" from {datasets}"));
    foreach (string note in data.Notes)
    {
        Console.WriteLine($"note     {note}");
    }

    Console.WriteLine($"browser  {output}");
    return 0;
}

static IReadOnlyList<string> Extra(Dictionary<string, string> flags) =>
    flags.TryGetValue("profiles", out string? p) ? [p] : [];

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
