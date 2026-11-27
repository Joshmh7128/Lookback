using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Lookback.Capture.Targets;
using Microsoft.Win32.SafeHandles;

namespace Lookback.Capture.Hook;

/// <summary>Thrown when the injector refuses a target. Nothing was launched or injected.</summary>
public sealed class HookRefusedException : Exception
{
    public HookRefusedException()
    {
    }

    public HookRefusedException(string message)
        : base(message)
    {
    }

    public HookRefusedException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>A request to launch one of our test apps with the present hook.</summary>
public sealed record HookLaunchRequest
{
    /// <summary>Full path of the executable. Only the test apps in the native build output pass the allowlist.</summary>
    public required string ExecutablePath { get; init; }

    public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <summary>
    /// The target's capture policy (<c>capturePolicy.allowPresentHook</c> in its
    /// profile). False, the default, refuses: the hook is opt-in per target.
    /// </summary>
    public bool AllowPresentHook { get; init; }

    /// <summary>Bound for each step: window up, remote load, hook installed.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>The request for one of our test apps from the native build output, with the policy that allows it.</summary>
    public static HookLaunchRequest ForTestApp(TestApp app, params string[] arguments)
    {
        string dir = TestAppAllowlist.FindNativeDirectory() ?? throw new FileNotFoundException("The native test apps are not built; run native/build.ps1.");
        return new HookLaunchRequest
        {
            ExecutablePath = Path.Combine(dir, TestAppAllowlist.FileName(app)),
            Arguments = arguments,
            AllowPresentHook = true, // our own testbench app, never a game
        };
    }
}

/// <summary>
/// Launches one of our test apps and injects the present hook into it. It
/// only injects into processes it started itself (suspended, checked, then
/// resumed), from the test-app build output, verified by final path and by
/// the <c>LookbackTestAppMarker</c> export. There is no way to hook a process
/// that is already running: <see cref="Attach"/> refuses every process this
/// injector did not launch. Never use it on games, launchers or anything with
/// anti-cheat; the allowlist makes that impossible by construction.
/// </summary>
public static class HookInjector
{
    private static readonly ConcurrentDictionary<uint, HookedProcess> Launched = new();

    /// <summary>Launches the test app, injects the hook and waits until it has installed. Throws <see cref="HookRefusedException"/> when the target is not allowed.</summary>
    public static HookedProcess Launch(HookLaunchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The present hook is Windows only.");
        }

        if (!request.AllowPresentHook)
        {
            throw new HookRefusedException($"Refused \"{request.ExecutablePath}\": its capture policy has allowPresentHook = false.");
        }

        string? native = TestAppAllowlist.FindNativeDirectory();
        if (native is null)
        {
            throw new HookRefusedException("Refused: the native hook and test apps are not built (run native/build.ps1).");
        }

        string? problem = TestAppAllowlist.Check(request.ExecutablePath, native, out string exe);
        if (problem is not null)
        {
            throw new HookRefusedException("Refused: " + problem);
        }

        string dll = Path.Combine(Path.GetDirectoryName(exe)!, TestAppAllowlist.HookDllName);
        foreach (string a in request.Arguments)
        {
            if (a.Length == 0 || a.Contains('"', StringComparison.Ordinal) || a.Any(char.IsWhiteSpace))
            {
                throw new ArgumentException($"Test-app arguments are plain tokens; got \"{a}\".", nameof(request));
            }
        }

        return LaunchVerified(exe, dll, request);
    }

    /// <summary>
    /// The only entry point that takes a process id. It returns the hooked
    /// process when this injector launched it, and refuses every other
    /// process: the hook never attaches to a running process it did not start.
    /// </summary>
    public static HookedProcess Attach(int processId)
    {
        if (processId > 0 && Launched.TryGetValue((uint)processId, out HookedProcess? p) && !p.HasExited)
        {
            return p;
        }

        throw new HookRefusedException($"Refused process {processId}: it was not started by the Lookback injector, and the present hook never attaches to running processes.");
    }

    internal static void Forget(HookedProcess p) => Launched.TryRemove(p.ProcessId, out _);

    private static unsafe HookedProcess LaunchVerified(string exe, string dll, HookLaunchRequest request)
    {
        var cmd = new StringBuilder();
        cmd.Append('"').Append(exe).Append('"');
        foreach (string a in request.Arguments)
        {
            cmd.Append(' ').Append(a);
        }

        char[] cmdLine = [.. cmd.ToString(), '\0'];
        string dir = Path.GetDirectoryName(exe)!;
        var si = new HookNative.StartupInfo { Cb = (uint)sizeof(HookNative.StartupInfo) };
        HookNative.ProcessInformation pi;
        fixed (char* app = exe)
        fixed (char* line = cmdLine)
        fixed (char* cwd = dir)
        {
            if (!HookNative.CreateProcess(app, line, 0, 0, false, HookNative.CreateSuspended | HookNative.CreateUnicodeEnvironment, 0, cwd, ref si, out pi))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), $"Could not start \"{exe}\".");
            }
        }

        var process = new SafeProcessHandle(pi.Process, ownsHandle: true);
        HookChannel? channel = null;
        try
        {
            // The image is mapped now, so the file cannot change under us: check what actually runs.
            string image = ImagePath(process);
            if (!string.Equals(TestAppAllowlist.FinalPath(image), exe, StringComparison.OrdinalIgnoreCase))
            {
                throw new HookRefusedException($"Refused: the started process runs \"{image}\", not \"{exe}\".");
            }

            if (!HookNative.IsWow64Process2(process, out ushort machine, out _) || machine != 0 || !Environment.Is64BitProcess)
            {
                throw new HookRefusedException("Refused: the test app is not a native 64-bit process.");
            }

            channel = new HookChannel(pi.ProcessId);
            if (HookNative.ResumeThread(pi.Thread) == uint.MaxValue)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not resume the test app.");
            }

            HookNative.CloseHandle(pi.Thread);
            pi.Thread = 0;
            nint window = WaitForWindow(process, pi.ProcessId, request.Timeout);
            LoadRemoteWithRetry(process, pi.ProcessId, dll, request.Timeout);
            WaitInstalled(process, channel, request.Timeout);
            var hooked = new HookedProcess(process, pi.ProcessId, exe, channel, window);
            Launched[pi.ProcessId] = hooked;
            return hooked;
        }
        catch
        {
            HookNative.TerminateProcess(process, 1);
            if (pi.Thread != 0)
            {
                HookNative.CloseHandle(pi.Thread);
            }

            channel?.Dispose();
            process.Dispose();
            throw;
        }
    }

    private static unsafe string ImagePath(SafeProcessHandle process)
    {
        char* buf = stackalloc char[1024];
        uint n = 1024;
        if (!HookNative.QueryFullProcessImageName(process, 0, buf, ref n))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not read the test app's image path.");
        }

        return new string(buf, 0, (int)n);
    }

    private static nint WaitForWindow(SafeProcessHandle process, uint pid, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (HookNative.WaitForSingleObject(process.DangerousGetHandle(), 0) == HookNative.WaitObject0)
            {
                HookNative.GetExitCodeProcess(process, out uint code);
                throw new InvalidOperationException($"The test app exited before showing its window (exit code {code}).");
            }

            if (CaptureTarget.FindWindow(null, pid) is { } t)
            {
                return t.Window;
            }

            Thread.Sleep(20);
        }

        throw new TimeoutException($"The test app showed no window within {timeout.TotalSeconds:F0} s.");
    }

    // A freshly built DLL can fail its first loads while the antivirus scans
    // it; retry a few times with a bounded backoff.
    private static void LoadRemoteWithRetry(SafeProcessHandle process, uint pid, string dll, TimeSpan timeout)
    {
        for (int attempt = 0; ; attempt++)
        {
            if (LoadRemote(process, dll, timeout) || IsLoaded(pid, dll))
            {
                return;
            }

            if (attempt == 5)
            {
                throw new InvalidOperationException($"The test app could not load \"{dll}\" (tried {attempt + 1} times).");
            }

            Thread.Sleep(200 << attempt);
        }
    }

    private static bool IsLoaded(uint pid, string dll)
    {
        try
        {
            using Process p = Process.GetProcessById((int)pid);
            foreach (ProcessModule m in p.Modules)
            {
                if (string.Equals(m.FileName, dll, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception or ArgumentException)
        {
        }

        return false;
    }

    /// <summary>LoadLibraryW on a remote thread; false when it returned null.</summary>
    private static unsafe bool LoadRemote(SafeProcessHandle process, string dll, TimeSpan timeout)
    {
        // kernel32 is mapped at the same address in every 64-bit process of this boot.
        nint loadLibrary = HookNative.GetProcAddress(HookNative.GetModuleHandle("kernel32.dll"), "LoadLibraryW");
        if (loadLibrary == 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "LoadLibraryW not found.");
        }

        byte[] path = Encoding.Unicode.GetBytes(dll + "\0");
        nint remote = HookNative.VirtualAllocEx(process, 0, (nuint)path.Length, HookNative.MemCommit | HookNative.MemReserve, HookNative.PageReadWrite);
        if (remote == 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not allocate the DLL path in the test app.");
        }

        try
        {
            fixed (byte* p = path)
            {
                if (!HookNative.WriteProcessMemory(process, remote, p, (nuint)path.Length, out nuint written) || written != (nuint)path.Length)
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not write the DLL path into the test app.");
                }
            }

            nint thread = HookNative.CreateRemoteThread(process, 0, 0, loadLibrary, remote, 0, out _);
            if (thread == 0)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not start the loader thread in the test app.");
            }

            try
            {
                if (HookNative.WaitForSingleObject(thread, (uint)timeout.TotalMilliseconds) != HookNative.WaitObject0)
                {
                    throw new TimeoutException("Loading the hook DLL in the test app timed out.");
                }

                // The exit code is the low 32 bits of the HMODULE: zero means
                // LoadLibraryW failed (or, rarely, a 4 GB-aligned base: the caller checks the module list).
                return HookNative.GetExitCodeThread(thread, out uint code) && code != 0;
            }
            finally
            {
                HookNative.CloseHandle(thread);
            }
        }
        finally
        {
            HookNative.VirtualFreeEx(process, remote, 0, HookNative.MemRelease);
        }
    }

    private static void WaitInstalled(SafeProcessHandle process, HookChannel channel, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            switch (channel.State)
            {
                case HookState.Installed:
                case HookState.Capturing:
                    return;
                case HookState.Refused:
                    throw new HookRefusedException("The hook refused to install: " + channel.Status);
                case HookState.Error:
                    throw new InvalidOperationException("The hook failed to install: " + channel.Status);
                default:
                    break;
            }

            if (HookNative.WaitForSingleObject(process.DangerousGetHandle(), 0) == HookNative.WaitObject0)
            {
                throw new InvalidOperationException("The test app exited while the hook was installing.");
            }

            Thread.Sleep(10);
        }

        throw new TimeoutException(string.Create(CultureInfo.InvariantCulture, $"The hook did not report in within {timeout.TotalSeconds:F0} s (state {channel.State})."));
    }
}

/// <summary>A test app this process launched with the present hook. Dispose closes it.</summary>
public sealed class HookedProcess : IDisposable
{
    private int _disposed;

    internal HookedProcess(SafeProcessHandle process, uint processId, string executablePath, HookChannel channel, nint window)
    {
        Handle = process;
        ProcessId = processId;
        ExecutablePath = executablePath;
        Channel = channel;
        Window = window;
    }

    public uint ProcessId { get; }

    public string ExecutablePath { get; }

    public HookChannel Channel { get; }

    public nint Window { get; }

    internal SafeProcessHandle Handle { get; }

    public bool HasExited => Handle.IsClosed || HookNative.WaitForSingleObject(Handle.DangerousGetHandle(), 0) == HookNative.WaitObject0;

    /// <summary>The exit code, or null while running.</summary>
    public int? ExitCode => HasExited && !Handle.IsClosed && HookNative.GetExitCodeProcess(Handle, out uint c) ? (int)c : null;

    /// <summary>
    /// Waits until the hook has published a frame and returns the shared-texture
    /// configuration (its adapter is the one the capture device must use).
    /// </summary>
    public HookConfig WaitForFirstFrame(TimeSpan timeout)
    {
        Span<ulong> handles = stackalloc ulong[HookProtocol.MaxSlots];
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            HookState state = Channel.State;
            if (state is HookState.Refused or HookState.Error)
            {
                throw new InvalidOperationException($"The hook is in state {state}: {Channel.Status}");
            }

            if (HasExited)
            {
                throw new InvalidOperationException($"The test app exited (code {ExitCode}).");
            }

            if (Channel.Published > 0 && Channel.TryReadConfig(out HookConfig config, handles, out _, out _) && config.SlotCount > 0)
            {
                return config;
            }

            Thread.Sleep(10);
        }

        throw new TimeoutException($"The hook published no frame within {timeout.TotalSeconds:F0} s (presents {Channel.Counters.Presents}, status \"{Channel.Status}\").");
    }

    /// <summary>The test app's window as a capture target on the hook's adapter.</summary>
    public CaptureTarget ToCaptureTarget(HookConfig config) => CaptureTarget.ForWindow(Window) with { AdapterLuid = config.AdapterLuid };

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        HookInjector.Forget(this);
        if (!HasExited)
        {
            HookNative.PostMessage(Window, HookNative.WmClose, 0, 0);
            if (HookNative.WaitForSingleObject(Handle.DangerousGetHandle(), 3000) != HookNative.WaitObject0)
            {
                HookNative.TerminateProcess(Handle, 1);
                HookNative.WaitForSingleObject(Handle.DangerousGetHandle(), 3000);
            }
        }

        Channel.Dispose();
        Handle.Dispose();
    }
}
