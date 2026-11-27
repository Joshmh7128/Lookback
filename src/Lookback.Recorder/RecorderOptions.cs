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

    /// <summary>The ratings folder in effect.</summary>
    public string ResolvedDatasetsDirectory => DatasetsDirectory is { Length: > 0 } d
        ? Path.GetFullPath(d)
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lookback", "datasets");
}
