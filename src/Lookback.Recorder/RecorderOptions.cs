namespace Lookback.Recorder;

/// <summary>Settings bound from the <c>Recorder</c> configuration section.</summary>
public sealed class RecorderOptions
{
    public string PipeName { get; set; } = "lookback.events";

    /// <summary>0 picks a free port.</summary>
    public int WebSocketPort { get; set; }
}
