using System.Globalization;
using Lookback.Capture;
using Lookback.Capture.Hook;
using Lookback.Contracts.V1;
using Lookback.Core.Bus;

/// <summary>
/// <c>--hook-testapp d3d11|d3d12</c>: launches one of our own test apps with
/// the present hook (the only processes it may run in), captures it through
/// <see cref="HookCaptureSource"/> in a normal live session, so the analysis
/// tap and <c>--encode</c> work unchanged, and prints per-second rates and the
/// hook's counters. Never points at a game: the injector refuses anything that
/// is not a test app from the native build output.
/// </summary>
internal static class HookBench
{
    public static string Usage => "lookback-capture --hook-testapp d3d11|d3d12 [--seconds 15] [--app-fps 120] [--present1] [--resize-at N] [--recreate-at N] [--encode ...]";

    /// <summary>Runs the bench when <c>--hook-testapp</c> is given; null otherwise.</summary>
    public static async Task<int?> TryRunAsync(string[] args)
    {
        string? which = Arg(args, "--hook-testapp");
        if (which is null)
        {
            return null;
        }

        TestApp app = which.ToUpperInvariant() switch
        {
            "D3D11" => TestApp.D3D11,
            "D3D12" => TestApp.D3D12,
            _ => throw new ArgumentException($"--hook-testapp takes d3d11 or d3d12, not {which}."),
        };
        var appArgs = new List<string> { "--fps", Arg(args, "--app-fps") ?? "120" };
        foreach (string flag in (string[])["--resize-at", "--recreate-at"])
        {
            if (Arg(args, flag) is string v)
            {
                appArgs.AddRange([flag, v]);
            }
        }

        if (args.Contains("--present1"))
        {
            appArgs.Add("--present1");
        }

        double seconds = double.Parse(Arg(args, "--seconds") ?? "15", CultureInfo.InvariantCulture);
        HookedProcess process;
        try
        {
            process = HookInjector.Launch(HookLaunchRequest.ForTestApp(app, [.. appArgs]));
        }
        catch (HookRefusedException e)
        {
            Console.Error.WriteLine(e.Message);
            return 2;
        }

        using (process)
        {
            HookConfig config = process.WaitForFirstFrame(TimeSpan.FromSeconds(10));
            Console.WriteLine($"hooked {Path.GetFileName(process.ExecutablePath)} (pid {process.ProcessId}): {config.Api} {config.Width}x{config.Height}, adapter {config.AdapterLuid:X}");
            var bus = new InProcessEventBus();
            await using LiveCaptureSession session = LiveCaptureSession.Start(bus, new LiveSessionOptions
            {
                Target = process.ToCaptureTarget(config),
                Backend = CaptureBackend.Hook,
                HookTarget = process,
                SourceKind = SourceKind.LiveGame,
                Retention = Retention.Owned,
                Title = $"Lookback test app ({config.Api})",
            });
            Console.WriteLine($"capturing with {session.Backend} on {session.AdapterName}; persist: {session.CanPersist}");
            using EncodeBench? encode = EncodeBench.TryStart(args, session);
            var until = DateTime.UtcNow.AddSeconds(seconds);
            SessionStats last = session.Stats;
            HookCounters lastHook = process.Channel.Counters;
            while (DateTime.UtcNow < until && session.StoppedReason is null)
            {
                await Task.Delay(1000);
                SessionStats st = session.Stats;
                HookCounters h = process.Channel.Counters;
                double dt = (st.Elapsed - last.Elapsed).TotalSeconds;
                Console.WriteLine($"{st.Elapsed.TotalSeconds,5:F1}s  presents {(h.Presents - lastHook.Presents) / dt,6:F1}/s  capture {(st.Captured - last.Captured) / dt,6:F1} fps  hook dropped {h.Dropped - lastHook.Dropped,3}  analysis {(st.Analysed - last.Analysed) / dt,5:F1} fps{encode?.Tick(dt)}");
                last = st;
                lastHook = h;
            }

            encode?.Finish();
            Console.WriteLine($"hook {process.Channel.Counters}; stopped: {session.StoppedReason ?? "no"}");
        }

        return 0;
    }

    private static string? Arg(string[] a, string name)
    {
        int i = Array.IndexOf(a, name);
        return i >= 0 && i + 1 < a.Length ? a[i + 1] : null;
    }
}
