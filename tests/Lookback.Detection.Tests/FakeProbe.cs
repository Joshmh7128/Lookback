using Lookback.Contracts.V1;
using Lookback.Detection.Probe;

namespace Lookback.Detection.Tests;

/// <summary>A machine the test edits between polls.</summary>
internal sealed class FakeProbe : ISystemProbe
{
    private readonly Dictionary<uint, (string Exe, string Path)> _processes = [];
    private readonly Dictionary<ulong, WindowEntry> _windows = [];

    public uint ForegroundPid { get; set; }

    public Dictionary<uint, string> CommandLines { get; } = [];

    public int DetailCalls { get; private set; }

    public int CommandLineCalls { get; private set; }

    public bool Throw { get; set; }

    public void Start(uint pid, string exe, string? path = null) => _processes[pid] = (exe, path ?? Path.Combine("games", exe));

    public void Stop(uint pid)
    {
        _processes.Remove(pid);
        foreach (ulong h in _windows.Values.Where(w => w.Pid == pid).Select(w => w.Handle).ToList())
        {
            _windows.Remove(h);
        }
    }

    public void Window(uint pid, ulong hwnd, string title = "Game", int w = 1920, int h = 1080, ulong monitor = 1, WindowMode mode = WindowMode.Borderless, bool minimized = false) =>
        _windows[hwnd] = new WindowEntry { Handle = hwnd, Pid = pid, Title = title, Width = w, Height = h, Monitor = monitor, Mode = mode, Minimized = minimized };

    public void CloseWindow(ulong hwnd) => _windows.Remove(hwnd);

    public SystemSnapshot Snapshot()
    {
        if (Throw)
        {
            throw new InvalidOperationException("probe failed");
        }

        ulong fg = _windows.Values.FirstOrDefault(w => w.Pid == ForegroundPid)?.Handle ?? 0;
        return new SystemSnapshot
        {
            Processes = [.. _processes.Select(p => new ProcessEntry(p.Key, p.Value.Exe))],
            Windows = [.. _windows.Values],
            Foreground = fg,
            ForegroundPid = ForegroundPid,
        };
    }

    public ProcessDetails? Details(uint pid)
    {
        DetailCalls++;
        return _processes.TryGetValue(pid, out var p) ? new ProcessDetails(p.Path, pid * 1000L) : null;
    }

    public string? CommandLine(uint pid)
    {
        CommandLineCalls++;
        return CommandLines.GetValueOrDefault(pid);
    }

    public long AdapterLuid(ulong monitor) => monitor == 0 ? 0 : 0x1000 + (long)monitor;
}
