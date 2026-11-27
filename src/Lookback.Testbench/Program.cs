using System.Globalization;
using Lookback.Contracts.V1;
using Lookback.Stream.SessionLog;
using Lookback.Testbench;

const string Usage = """
    lookback-testbench: run footage through Lookback's signals and render a timeline.

    Usage:
      lookback-testbench analyze <file> [--out <dir>] [--retention owned|permissioned|open-license|ephemeral]
                                        [--permission-ref <ref>] [--title <text>] [--game <name>]
      lookback-testbench report <session-path-without-extension> [--video <file>]

    Retention:
      owned         your own footage (default)
      permissioned  someone else's footage used with permission; needs --permission-ref
      open-license  footage under a licence that allows this use; needs --permission-ref
      ephemeral     analyse and print a summary; nothing is written to disk
    """;

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

try
{
    return args switch
    {
        ["analyze", string file, .. var rest] => await AnalyzeAsync(file, ParseFlags(rest), cts.Token),
        ["report", string session, .. var rest] => await ReportAsync(session, ParseFlags(rest), cts.Token),
        _ => PrintUsage(),
    };
}
catch (RetentionViolationException ex)
{
    await Console.Error.WriteLineAsync($"Refused: {ex.Message}");
    return 2;
}
catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException or ArgumentException)
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
    return 0;
}

static async Task<int> ReportAsync(string session, Dictionary<string, string> flags, CancellationToken ct)
{
    string path = await TestbenchRunner.ReportAsync(session, flags.GetValueOrDefault("video"), ct);
    Console.WriteLine($"report   {path}");
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
        if (!args[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length)
        {
            throw new ArgumentException($"Unexpected argument '{args[i]}'.");
        }

        flags[args[i][2..]] = args[++i];
    }

    return flags;
}
