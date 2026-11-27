using Lookback.Capture.Gpu;
using Vortice.DXGI;

namespace Lookback.Capture.Targets;

/// <summary>What to capture: a window or a whole monitor, and the adapter that scans it out.</summary>
public sealed record CaptureTarget
{
    /// <summary>Window handle; zero for a monitor capture.</summary>
    public nint Window { get; init; }

    /// <summary>The monitor the window is on, or the monitor to capture.</summary>
    public required nint Monitor { get; init; }

    /// <summary>LUID of the adapter that owns <see cref="Monitor"/>; the whole pipeline runs on it.</summary>
    public required long AdapterLuid { get; init; }

    /// <summary>DXGI output index on that adapter (for duplication).</summary>
    public required int OutputIndex { get; init; }

    public required string Description { get; init; }

    public uint ProcessId { get; init; }

    public bool IsWindow => Window != 0;

    /// <summary>A window by handle.</summary>
    public static CaptureTarget ForWindow(nint hwnd)
    {
        if (!Win32.IsWindow(hwnd))
        {
            throw new ArgumentException($"0x{hwnd:X} is not a window.", nameof(hwnd));
        }

        nint monitor = Win32.MonitorFromWindow(hwnd, Win32.MonitorDefaultToNearest);
        (long luid, int output, string name) = FindOutput(monitor);
        Win32.GetWindowThreadProcessId(hwnd, out uint pid);
        return new CaptureTarget
        {
            Window = hwnd,
            Monitor = monitor,
            AdapterLuid = luid,
            OutputIndex = output,
            ProcessId = pid,
            Description = $"window \"{Win32.Title(hwnd)}\" (pid {pid}) on {name}",
        };
    }

    /// <summary>
    /// The largest visible titled top-level window whose title contains
    /// <paramref name="titleContains"/> (case-insensitive) and, if given,
    /// belongs to <paramref name="processId"/>.
    /// </summary>
    public static CaptureTarget? FindWindow(string? titleContains, uint? processId = null)
    {
        nint best = 0;
        long bestArea = 0;
        Win32.EnumWindows((hwnd, _) =>
        {
            if (!Win32.IsWindowVisible(hwnd) || Win32.GetWindow(hwnd, 4 /* GW_OWNER */) != 0)
            {
                return true;
            }

            string title = Win32.Title(hwnd);
            if (title.Length == 0 || (titleContains is not null && !title.Contains(titleContains, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            Win32.GetWindowThreadProcessId(hwnd, out uint pid);
            if (processId is not null && pid != processId)
            {
                return true;
            }

            Win32.GetWindowRect(hwnd, out Win32.Rect r);
            long area = (long)(r.Right - r.Left) * (r.Bottom - r.Top);
            if (area > bestArea)
            {
                best = hwnd;
                bestArea = area;
            }

            return true;
        }, 0);
        return best == 0 ? null : ForWindow(best);
    }

    /// <summary>Monitor <paramref name="index"/> in DXGI enumeration order (adapter by adapter).</summary>
    public static CaptureTarget ForMonitor(int index)
    {
        int n = 0;
        foreach ((long luid, int output, OutputDescription d) in Outputs())
        {
            if (n++ == index)
            {
                return new CaptureTarget { Monitor = d.Monitor, AdapterLuid = luid, OutputIndex = output, Description = $"monitor {d.DeviceName}" };
            }
        }

        throw new ArgumentOutOfRangeException(nameof(index), index, $"Only {n} monitors are attached.");
    }

    /// <summary>Every attached output: adapter LUID, output index and description.</summary>
    public static IEnumerable<(long Luid, int Output, OutputDescription Description)> Outputs()
    {
        using IDXGIFactory1 factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        var result = new List<(long, int, OutputDescription)>();
        for (uint a = 0; factory.EnumAdapters1(a, out IDXGIAdapter1 adapter).Success; a++)
        {
            using (adapter)
            {
                long luid = GpuDevice.ToLong(adapter.Description1.Luid);
                for (uint o = 0; adapter.EnumOutputs(o, out IDXGIOutput output).Success; o++)
                {
                    using (output)
                    {
                        OutputDescription d = output.Description;
                        if (d.AttachedToDesktop)
                        {
                            result.Add((luid, (int)o, d));
                        }
                    }
                }
            }
        }

        return result;
    }

    private static (long Luid, int Output, string Name) FindOutput(nint monitor)
    {
        foreach ((long luid, int output, OutputDescription d) in Outputs())
        {
            if (d.Monitor == monitor)
            {
                return (luid, output, d.DeviceName);
            }
        }

        throw new InvalidOperationException("The window's monitor is not driven by any DXGI adapter.");
    }
}
