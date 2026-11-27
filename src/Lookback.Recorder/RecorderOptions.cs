namespace Lookback.Recorder;

/// <summary>Settings bound from the <c>Recorder</c> configuration section.</summary>
public sealed class RecorderOptions
{
    public string PipeName { get; set; } = "lookback.events";

    /// <summary>0 picks a free port.</summary>
    public int WebSocketPort { get; set; }

    /// <summary>
    /// Serve the live event profiler page next to the socket and print its
    /// link on stdout as <c>LOOKBACK_PROFILER &lt;url&gt;</c>. Off by default.
    /// </summary>
    public bool Profiler { get; set; }

    /// <summary>
    /// Accept official-player frames at <c>/ingest</c> and serve the
    /// calibration panel at <c>/calibrate</c>; both are announced in
    /// <c>LOOKBACK_READY</c> as <c>ingest</c> and <c>calibrate</c>. On by default.
    /// </summary>
    public bool OfficialPlayer { get; set; } = true;

    /// <summary>
    /// The checkout's <c>profiles</c> folder: calibration runs on the profiles
    /// in it and saves confirmed numbers back into them. Unset, it runs on the
    /// built-in profiles and saving is off.
    /// </summary>
    public string? ProfilesDirectory { get; set; }

    /// <summary>
    /// Where ratings are stored, as <c>&lt;dir&gt;/&lt;game&gt;/ratings.jsonl</c>
    /// (the checkout's <c>datasets</c> folder, so <c>ml/</c> trains on them).
    /// Unset: <c>%LOCALAPPDATA%\Lookback\datasets</c>.
    /// </summary>
    public string? DatasetsDirectory { get; set; }

    /// <summary>The shell's process id: the recorder stops when that process exits, however it exits.</summary>
    public int? ParentProcessId { get; set; }

    /// <summary>Accept control commands (<c>rate</c>, <c>analyze</c>, <c>save</c>) on <c>/events</c>. On by default.</summary>
    public bool Control { get; set; } = true;

    /// <summary>
    /// Record games automatically: detection starts a capture session for
    /// every allowed game it sees start (one at a time). On by default on Windows.
    /// </summary>
    public bool Capture { get; set; } = OperatingSystem.IsWindows();

    /// <summary>Session footage and saved clips: <c>&lt;dir&gt;/sessions</c> and <c>&lt;dir&gt;/clips</c>. Unset: <c>%LOCALAPPDATA%\Lookback\store</c>.</summary>
    public string? StoreDirectory { get; set; }

    /// <summary>Saved clips may use this much (never swept; saving is refused at the cap). 10 GB by default.</summary>
    public double ClipsBudgetGB { get; set; } = 10;

    /// <summary>Warn (in <c>recorder.storage</c>) once saved clips reach this fraction of their budget.</summary>
    public double ClipsWarnFraction { get; set; } = 0.9;

    /// <summary>Session footage (2 s segments) may use at most this much; the governor may pick less. 20 GB by default.</summary>
    public double FootageBudgetGB { get; set; } = 20;

    /// <summary>Record the microphone as its own track. Off by default.</summary>
    public bool Microphone { get; set; }

    /// <summary>Game detection's allow/deny list. Unset: <c>%LOCALAPPDATA%\Lookback\detection.json</c>.</summary>
    public string? DetectionSettings { get; set; }

    /// <summary>The store folder in effect.</summary>
    public string ResolvedStoreDirectory => StoreDirectory is { Length: > 0 } s
        ? Path.GetFullPath(s)
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lookback", "store");

    /// <summary>The ratings folder in effect.</summary>
    public string ResolvedDatasetsDirectory => DatasetsDirectory is { Length: > 0 } d
        ? Path.GetFullPath(d)
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lookback", "datasets");
}
