using Lookback.Contracts.V1;
using Lookback.Detection.Matching;
using Lookback.Detection.Probe;
using Lookback.Profiles.Detection;
using Lookback.Profiles.Model;

namespace Lookback.Detection.Lifecycle;

/// <summary>One run of a game, as detection sees it at one moment. Immutable; each change produces a new one.</summary>
public sealed record GameInstance
{
    /// <summary>
    /// Stable for this run of the game, across focus changes, window changes
    /// and a relaunch with a new pid. Used as the session id of its
    /// <c>game.lifecycle</c> envelopes; the recorder can give its capture
    /// session the same id.
    /// </summary>
    public required string Id { get; init; }

    public required GameMatch Match { get; init; }

    public required uint Pid { get; init; }

    public required string ExeName { get; init; }

    public string? ExePath { get; init; }

    /// <summary><c>unity</c>, <c>unreal</c> or empty, from the files beside the executable.</summary>
    public string Engine { get; init; } = "";

    /// <summary>The window to capture: the process's largest visible top-level window.</summary>
    public required WindowEntry Window { get; init; }

    /// <summary>LUID of the adapter that drives the window's monitor, or 0 when unknown.</summary>
    public long AdapterLuid { get; init; }

    public bool Focused { get; init; }

    /// <summary>QPC tick when the game was announced.</summary>
    public long DetectedAt { get; init; }

    /// <summary>Facts for sub-game and mode hints (<c>ProfileRunner.ApplyHints</c>): launch arguments and window title.</summary>
    public DetectionContext Hints { get; init; } = new();

    public CapturePolicy Policy => Match.Policy;
}

/// <summary>A confirmed change, ready to publish.</summary>
public sealed record LifecycleChange(GameLifecycle.Types.Kind Kind, GameInstance Game, long At)
{
    /// <summary>The <see cref="GameLifecycle"/> payload for this change.</summary>
    public GameLifecycle ToProto() => new()
    {
        Kind = Kind,
        Pid = Game.Pid,
        Exe = Game.ExeName,
        ExePath = Game.ExePath ?? "",
        Engine = Game.Engine,
        Hwnd = Game.Window.Handle,
        Monitor = Game.Window.Monitor,
        AdapterLuid = (ulong)Game.AdapterLuid,
        WindowTitle = Game.Window.Title,
        WindowMode = Game.Window.Mode,
        Confidence = Game.Match.Confidence,
        GameProfile = Game.Match.ProfileId,
        Method = Game.Match.Method,
        AllowPresentHook = Game.Policy.AllowPresentHook,
        AllowMemoryReading = Game.Policy.AllowMemoryReading,
    };
}
