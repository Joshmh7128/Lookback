using System.Diagnostics;
using System.Globalization;
using Lookback.Capture.Targets;
using Lookback.Contracts;
using Lookback.Contracts.V1;
using Lookback.Core.Bus;
using Lookback.Core.Time;
using Lookback.Detection.Lifecycle;
using Lookback.Detection.Matching;
using Lookback.Detection.Probe;
using Lookback.Profiles.Model;
using Kind = Lookback.Contracts.V1.GameLifecycle.Types.Kind;

namespace Lookback.Detection.Windows.Tests;

/// <summary>
/// The Windows probe and the runner against a real child process: a tiny
/// Win32 window (<c>lookback-testwindow.exe</c>) that a temporary test
/// profile lists as a game.
/// </summary>
public sealed class RealProcessTests
{
    private const string Exe = "lookback-testwindow.exe";

    private static ProfileCatalog Catalog() => new(
    [
        GameProfileLoader.Load("generic"),
        new GameProfile
        {
            Id = "testwindow",
            DisplayName = "Test window",
            Executables = [Exe],
            // Makes the matcher ask for launch arguments.
            IdHints = [new IdHint { Source = HintSource.LaunchArgs, Regex = "level=(\\d+)", Kind = "test.level" }],
        },
    ]);

    private static string TestWindowPath => Path.Combine(AppContext.BaseDirectory, Exe);

    private static (Process Process, ulong Hwnd) StartWindow(string title)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        Assert.SkipUnless(Environment.UserInteractive, "needs a desktop session for windows");
        Assert.True(File.Exists(TestWindowPath), $"missing {TestWindowPath}");
        var psi = new ProcessStartInfo(TestWindowPath)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(title);
        psi.ArgumentList.Add("60");
        Process p = Process.Start(psi)!;
        string? line = p.StandardOutput.ReadLine();
        Assert.NotNull(line);
        Assert.StartsWith("READY ", line, StringComparison.Ordinal);
        return (p, ulong.Parse(line["READY ".Length..], CultureInfo.InvariantCulture));
    }

    private static void Close(Process p)
    {
        try
        {
            p.StandardInput.Close(); // the window closes itself
            if (!p.WaitForExit(5000))
            {
                p.Kill();
            }
        }
        finally
        {
            p.Dispose();
        }
    }

    [Fact]
    public void The_probe_sees_the_process_its_path_launch_args_and_window()
    {
        string title = $"Lookback probe test {Guid.NewGuid():N} level=7";
        (Process p, ulong hwnd) = StartWindow(title);
        try
        {
            var probe = new WindowsSystemProbe();
            WindowEntry? w = null;
            SystemSnapshot snap = probe.Snapshot();
            for (int i = 0; i < 50 && (w = snap.Windows.FirstOrDefault(x => x.Handle == hwnd)) is null; i++)
            {
                Thread.Sleep(100);
                snap = probe.Snapshot();
            }

            Assert.Contains(snap.Processes, x => x.Pid == (uint)p.Id && x.ExeName == Exe);
            Assert.NotNull(w);
            Assert.Equal((uint)p.Id, w.Pid);
            Assert.Equal(title, w.Title);
            Assert.True(w.Width >= 600 && w.Height >= 400, $"{w.Width}x{w.Height}");
            Assert.NotEqual(0ul, w.Monitor);
            Assert.Equal(WindowMode.Windowed, w.Mode);
            Assert.False(w.Minimized);

            ProcessDetails? d = probe.Details((uint)p.Id);
            Assert.NotNull(d);
            Assert.Equal(Path.GetFullPath(TestWindowPath), d.ExePath, ignoreCase: true);
            Assert.NotEqual(0, d.StartKey);

            string? args = probe.CommandLine((uint)p.Id);
            Assert.NotNull(args);
            Assert.Contains(title, args, StringComparison.Ordinal);
        }
        finally
        {
            Close(p);
        }
    }

    [Fact]
    public void The_probe_answers_for_processes_it_cannot_open_without_throwing()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        var probe = new WindowsSystemProbe();
        _ = probe.Details(4); // System: no path for an unelevated caller
        _ = probe.CommandLine(4);
        Assert.Null(probe.Details(uint.MaxValue - 1));
        Assert.Contains(probe.Snapshot().Processes, x => x.Pid == (uint)Environment.ProcessId);
    }

    [Fact]
    public async Task The_runner_announces_start_and_stop_of_a_real_process_with_its_capture_target()
    {
        string title = $"Lookback runner test {Guid.NewGuid():N} level=3";
        using var bus = new InProcessEventBus();
        using IEventSubscription sub = bus.Subscribe(Topics.GameLifecycle);
        var options = new DetectionOptions
        {
            PollInterval = TimeSpan.FromMilliseconds(100),
            StartHold = TimeSpan.FromMilliseconds(300),
            ExitGrace = TimeSpan.FromMilliseconds(300),
        };
        var seen = new List<LifecycleChange>();
        var detected = new TaskCompletionSource<LifecycleChange>(TaskCreationOptions.RunContinuationsAsynchronously);
        var exited = new TaskCompletionSource<LifecycleChange>(TaskCreationOptions.RunContinuationsAsynchronously);

        (Process p, ulong hwnd) = StartWindow(title);
        uint pid = (uint)p.Id;
        DetectionRunner runner = new(bus, new WindowsSystemProbe(), new GameMatcher(Catalog()), SystemClock.Instance, options);
        await using (runner.ConfigureAwait(false))
        {
            runner.Changed += c =>
            {
                if (c.Game.Pid != pid)
                {
                    return;
                }

                lock (seen)
                {
                    seen.Add(c);
                }

                if (c.Kind == Kind.Detected)
                {
                    detected.TrySetResult(c);
                }
                else if (c.Kind == Kind.Exited)
                {
                    exited.TrySetResult(c);
                }
            };

            try
            {
                LifecycleChange start = await detected.Task.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
                Assert.Equal("testwindow", start.Game.Match.ProfileId);
                Assert.Equal(hwnd, start.Game.Window.Handle);
                Assert.Equal(title, start.Game.Window.Title);
                Assert.Contains("level=3", start.Game.Hints.LaunchArgs, StringComparison.Ordinal);
                Assert.False(start.Game.Policy.AllowPresentHook);
                Assert.False(start.Game.Policy.AllowMemoryReading);

                // One DXGI enumeration (inside CaptureTarget.ForWindow): the API under test. Detection itself never creates a DXGI factory.
                if (start.Game.AdapterLuid != 0)
                {
                    CaptureTarget? target = WindowsDetection.TryCaptureTarget(start.Game);
                    Assert.NotNull(target);
                    Assert.Equal((nint)hwnd, target.Window);
                    Assert.Equal(pid, target.ProcessId);
                    Assert.Equal(target.AdapterLuid, start.Game.AdapterLuid);
                }
            }
            finally
            {
                Close(p);
            }

            LifecycleChange stop = await exited.Task.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
            Assert.Equal(StartId(), stop.Game.Id);
        }

        var envelopes = new List<Envelope>();
        while (sub.TryRead(out Envelope? e))
        {
            if (e!.GameLifecycle.Pid == pid)
            {
                envelopes.Add(e);
            }
        }

        Assert.Equal(Kind.Detected, envelopes[0].GameLifecycle.Kind);
        Assert.Equal(Kind.Exited, envelopes[^1].GameLifecycle.Kind);
        Assert.Equal(Exe, envelopes[0].GameLifecycle.Exe);
        Assert.Equal(hwnd, envelopes[0].GameLifecycle.Hwnd);
        Assert.Single(envelopes.Select(e => e.SessionId).Distinct());
        Assert.True(envelopes[^1].Qpc > envelopes[0].Qpc);

        string StartId()
        {
            lock (seen)
            {
                return seen[0].Game.Id;
            }
        }
    }
}
