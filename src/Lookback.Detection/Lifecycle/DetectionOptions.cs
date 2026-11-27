namespace Lookback.Detection.Lifecycle;

/// <summary>Timing for game detection. The holds keep launchers, splash screens and alt-tabs from flapping.</summary>
public sealed record DetectionOptions
{
    /// <summary>How often the runner looks at the process list and windows.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>A matched process must keep a window this long before it is announced as started.</summary>
    public TimeSpan StartHold { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>Focus must stay on (or off) the game this long before it counts.</summary>
    public TimeSpan FocusHold { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>A new window, monitor or window mode must last this long before the capture target changes.</summary>
    public TimeSpan WindowHold { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// After the process ends, how long to wait before announcing that the
    /// game stopped. A new process of the same profile with a window in this
    /// time (a game that relaunches itself, or an anti-cheat bootstrapper
    /// handing over) continues the same run.
    /// </summary>
    public TimeSpan ExitGrace { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Windows smaller than this (tool tips, tray pop-ups, tiny launchers) are never a game's capture window.</summary>
    public int MinWindowWidth { get; init; } = 320;

    public int MinWindowHeight { get; init; } = 200;

    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (PollInterval <= TimeSpan.Zero)
        {
            problems.Add("PollInterval must be positive.");
        }

        if (StartHold < TimeSpan.Zero || FocusHold < TimeSpan.Zero || WindowHold < TimeSpan.Zero || ExitGrace < TimeSpan.Zero)
        {
            problems.Add("holds and grace must not be negative.");
        }

        return problems;
    }
}
