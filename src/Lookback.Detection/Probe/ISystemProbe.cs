using Lookback.Contracts.V1;

namespace Lookback.Detection.Probe;

/// <summary>
/// What the operating system tells any program about the others, and nothing
/// more: the process list, an executable's path, and the top-level windows.
/// An implementation never opens a process's memory, injects or hooks; on
/// Windows it uses Toolhelp snapshots, <c>QueryFullProcessImageName</c> and
/// <c>EnumWindows</c>. Called from one thread at a time.
/// </summary>
public interface ISystemProbe
{
    /// <summary>The processes and visible top-level windows right now. Cheap enough to call every second.</summary>
    SystemSnapshot Snapshot();

    /// <summary>
    /// Path and start identity of one process, or null when it has exited or
    /// the OS will not say. Called only for processes whose name matters.
    /// </summary>
    ProcessDetails? Details(uint pid);

    /// <summary>
    /// The process's launch arguments, or null when unavailable. Called only
    /// for a game whose profile tree has <c>launchArgs</c> hints.
    /// </summary>
    string? CommandLine(uint pid);

    /// <summary>LUID of the adapter that drives <paramref name="monitor"/>, or 0 when unknown.</summary>
    long AdapterLuid(ulong monitor);
}

/// <summary>One running process, as the process list shows it.</summary>
public readonly record struct ProcessEntry(uint Pid, string ExeName);

/// <summary>Facts about a process that need a (query-only) handle to it.</summary>
/// <param name="ExePath">Full path of the executable.</param>
/// <param name="StartKey">Identifies this run of the pid (the creation time on Windows), so a reused pid is a new process.</param>
public sealed record ProcessDetails(string ExePath, long StartKey);

/// <summary>A visible, unowned top-level window.</summary>
public sealed record WindowEntry
{
    public required ulong Handle { get; init; }

    public required uint Pid { get; init; }

    public string Title { get; init; } = "";

    public int Width { get; init; }

    public int Height { get; init; }

    public ulong Monitor { get; init; }

    public WindowMode Mode { get; init; } = WindowMode.Windowed;

    public bool Minimized { get; init; }

    public long Area => (long)Width * Height;
}

/// <summary>One look at the machine.</summary>
public sealed record SystemSnapshot
{
    public IReadOnlyList<ProcessEntry> Processes { get; init; } = [];

    public IReadOnlyList<WindowEntry> Windows { get; init; } = [];

    /// <summary>The window with keyboard focus, or 0.</summary>
    public ulong Foreground { get; init; }

    /// <summary>The process that owns <see cref="Foreground"/>, or 0.</summary>
    public uint ForegroundPid { get; init; }
}
