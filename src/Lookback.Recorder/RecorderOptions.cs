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
}
