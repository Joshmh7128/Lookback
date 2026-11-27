using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Lookback.Capture.Targets;
using Lookback.Contracts.V1;
using Lookback.Detection.Probe;
using static Lookback.Detection.Windows.NativeMethods;

namespace Lookback.Detection.Windows;

/// <summary>
/// <see cref="ISystemProbe"/> on Windows, unelevated, with nothing inside any
/// other process:
/// <list type="bullet">
/// <item>Processes from a Toolhelp snapshot (pid and executable name).</item>
/// <item>Path and creation time from a query-limited handle
/// (<c>QueryFullProcessImageName</c>, <c>GetProcessTimes</c>).</item>
/// <item>Launch arguments from <c>NtQueryInformationProcess(ProcessCommandLineInformation)</c>
/// on the same handle, asked only for games whose profiles read them.</item>
/// <item>Windows from <c>EnumWindows</c>: visible, unowned, not cloaked, not
/// tool windows; title, size, monitor, and whether the window is framed,
/// borderless over its monitor, or exclusive fullscreen
/// (<c>SHQueryUserNotificationState</c>, foreground only).</item>
/// </list>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed unsafe class WindowsSystemProbe : ISystemProbe
{
    private readonly List<ProcessEntry> _processes = new(512);
    private readonly List<WindowEntry> _windows = new(256);
    private readonly Dictionary<ulong, long> _luids = [];

    public SystemSnapshot Snapshot()
    {
        _processes.Clear();
        _windows.Clear();
        ReadProcesses(_processes);
        ReadWindows(_windows);
        nint fg = GetForegroundWindow();
        uint fgPid = 0;
        if (fg != 0)
        {
            GetWindowThreadProcessId(fg, out fgPid);
        }

        // Exclusive fullscreen is only reported for the foreground window.
        if (fg != 0 && SHQueryUserNotificationState(out int state) == 0 && state == QunsRunningD3dFullScreen)
        {
            for (int i = 0; i < _windows.Count; i++)
            {
                if (_windows[i].Handle == (ulong)fg)
                {
                    _windows[i] = _windows[i] with { Mode = WindowMode.Fullscreen };
                }
            }
        }

        return new SystemSnapshot { Processes = [.. _processes], Windows = [.. _windows], Foreground = (ulong)fg, ForegroundPid = fgPid };
    }

    public ProcessDetails? Details(uint pid)
    {
        nint h = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (h == 0)
        {
            return null;
        }

        try
        {
            char* buf = stackalloc char[1024];
            uint size = 1024;
            string path = QueryFullProcessImageName(h, 0, buf, ref size) ? new string(buf, 0, (int)size) : "";
            long start = GetProcessTimes(h, out long creation, out _, out _, out _) ? creation : 0;
            return path.Length == 0 ? null : new ProcessDetails(path, start);
        }
        finally
        {
            CloseHandle(h);
        }
    }

    public string? CommandLine(uint pid)
    {
        nint h = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (h == 0)
        {
            return null;
        }

        try
        {
            uint length = 2048;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                byte* buf = (byte*)NativeMemory.Alloc(length);
                try
                {
                    int status = NtQueryInformationProcess(h, ProcessCommandLineInformation, buf, length, out uint returned);
                    if (status == StatusInfoLengthMismatch && returned > length)
                    {
                        length = returned;
                        continue;
                    }

                    if (status < 0)
                    {
                        return null;
                    }

                    // UNICODE_STRING { USHORT Length; USHORT MaximumLength; PWSTR Buffer; }, Buffer pointing into buf.
                    ushort bytes = *(ushort*)buf;
                    char* text = *(char**)(buf + IntPtr.Size);
                    return text == null ? "" : new string(text, 0, bytes / 2);
                }
                finally
                {
                    NativeMemory.Free(buf);
                }
            }

            return null;
        }
        finally
        {
            CloseHandle(h);
        }
    }

    public long AdapterLuid(ulong monitor)
    {
        if (monitor == 0)
        {
            return 0;
        }

        if (_luids.TryGetValue(monitor, out long luid))
        {
            return luid;
        }

        try
        {
            _luids.Clear(); // monitors come and go: rebuild the map when one is missing
            foreach ((long l, int _, Vortice.DXGI.OutputDescription d) in CaptureTarget.Outputs())
            {
                _luids[(ulong)d.Monitor] = l;
            }
        }
        catch (Exception ex) when (ex is COMException or SharpGen.Runtime.SharpGenException or DllNotFoundException)
        {
            return 0;
        }

        return _luids.GetValueOrDefault(monitor);
    }

    private static void ReadProcesses(List<ProcessEntry> into)
    {
        nint snap = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
        if (snap == InvalidHandle || snap == 0)
        {
            throw new InvalidOperationException($"CreateToolhelp32Snapshot failed ({Marshal.GetLastPInvokeError()}).");
        }

        try
        {
            ProcessEntry32 e = default;
            e.DwSize = (uint)sizeof(ProcessEntry32);
            for (bool ok = Process32First(snap, &e); ok; ok = Process32Next(snap, &e))
            {
                if (e.Th32ProcessId == 0)
                {
                    continue; // System Idle Process
                }

                into.Add(new ProcessEntry(e.Th32ProcessId, new string(e.SzExeFile)));
            }
        }
        finally
        {
            CloseHandle(snap);
        }
    }

    private static void ReadWindows(List<WindowEntry> into)
    {
        GCHandle state = GCHandle.Alloc(into);
        try
        {
            EnumWindows(&OnWindow, GCHandle.ToIntPtr(state));
        }
        finally
        {
            state.Free();
        }
    }

    [UnmanagedCallersOnly]
    private static int OnWindow(nint hwnd, nint lParam)
    {
        try
        {
            var into = (List<WindowEntry>)GCHandle.FromIntPtr(lParam).Target!;
            WindowEntry? w = Describe(hwnd);
            if (w is not null)
            {
                into.Add(w);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // An exception must not unwind into user32; skip this window.
        }

        return 1;
    }

    internal static WindowEntry? Describe(nint hwnd)
    {
        if (!IsWindowVisible(hwnd) || GetWindow(hwnd, GwOwner) != 0)
        {
            return null;
        }

        long exStyle = GetWindowLongPtr(hwnd, GwlExStyle);
        if ((exStyle & WsExToolWindow) != 0)
        {
            return null;
        }

        int cloaked = 0;
        if (DwmGetWindowAttribute(hwnd, DwmwaCloaked, &cloaked, sizeof(int)) == 0 && cloaked != 0)
        {
            return null; // suspended Store apps and windows on other virtual desktops
        }

        GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0 || !GetWindowRect(hwnd, out Rect r))
        {
            return null;
        }

        bool minimized = IsIconic(hwnd);
        nint monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        var mode = WindowMode.Windowed;
        var mi = new MonitorInfo { CbSize = (uint)sizeof(MonitorInfo) };
        if (!minimized && monitor != 0 && GetMonitorInfo(monitor, ref mi))
        {
            long style = GetWindowLongPtr(hwnd, GwlStyle);
            bool framed = (style & (WsCaption | WsThickFrame)) != 0;
            bool covers = r.Left <= mi.RcMonitor.Left && r.Top <= mi.RcMonitor.Top && r.Right >= mi.RcMonitor.Right && r.Bottom >= mi.RcMonitor.Bottom;
            if (covers && !framed)
            {
                mode = WindowMode.Borderless;
            }
        }

        return new WindowEntry
        {
            Handle = (ulong)hwnd,
            Pid = pid,
            Title = Title(hwnd),
            Width = r.Right - r.Left,
            Height = r.Bottom - r.Top,
            Monitor = (ulong)monitor,
            Mode = mode,
            Minimized = minimized,
        };
    }

    private static string Title(nint hwnd)
    {
        int n = GetWindowTextLength(hwnd);
        if (n <= 0)
        {
            return "";
        }

        n = Math.Min(n, 1024);
        char* buf = stackalloc char[n + 1];
        int got = GetWindowText(hwnd, buf, n + 1);
        return got <= 0 ? "" : new string(buf, 0, got);
    }
}
