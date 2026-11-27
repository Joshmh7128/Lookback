using Lookback.Contracts;
using Lookback.Contracts.V1;
using Lookback.Core.Bus;
using Lookback.Core.Time;
using Lookback.Detection.Lifecycle;
using Lookback.Detection.Matching;
using Lookback.Profiles.Model;
using Kind = Lookback.Contracts.V1.GameLifecycle.Types.Kind;

namespace Lookback.Detection.Tests;

/// <summary>The detector and runner against a fake machine, polled once a (manual) second.</summary>
public sealed class LifecycleTests : IAsyncLifetime
{
    private const long Freq = 10_000_000;
    private readonly FakeProbe _probe = new();
    private readonly ManualClock _clock = new(Freq, start: 1_000_000_000);
    private readonly InProcessEventBus _bus = new();
    private readonly IEventSubscription _sub;
    private DetectionRunner _runner = null!;

    public LifecycleTests()
    {
        _sub = _bus.Subscribe(Topics.GameLifecycle);
    }

    public ValueTask InitializeAsync()
    {
        _runner = new DetectionRunner(_bus, _probe, new GameMatcher(ProfileCatalog.BuiltIn), _clock, new DetectionOptions(), start: false);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _runner.DisposeAsync();
        _sub.Dispose();
        _bus.Dispose();
    }

    /// <summary>Advances one second, polls, and returns the kinds published.</summary>
    private List<Kind> Tick(double seconds = 1)
    {
        _clock.Advance(TimeSpan.FromSeconds(seconds));
        return [.. _runner.Poll().Select(c => c.Kind)];
    }

    private List<Kind> Ticks(int n, double seconds = 1)
    {
        var all = new List<Kind>();
        for (int i = 0; i < n; i++)
        {
            all.AddRange(Tick(seconds));
        }

        return all;
    }

    private List<Envelope> Drain()
    {
        var list = new List<Envelope>();
        while (_sub.TryRead(out Envelope? e))
        {
            list.Add(e!);
        }

        return list;
    }

    [Fact]
    public void A_game_with_a_steady_window_is_announced_once_after_the_start_hold_then_focused()
    {
        _probe.Start(100, "Overwatch.exe");
        _probe.Window(100, 0xA1, "Overwatch");
        _probe.ForegroundPid = 100;

        Assert.Empty(Ticks(3)); // seen at t=1, held until t=4
        Assert.Equal([Kind.Detected, Kind.Focused], Tick());
        Assert.Empty(Ticks(5));

        List<Envelope> env = Drain();
        Assert.Equal(2, env.Count);
        GameLifecycle d = env[0].GameLifecycle;
        Assert.Equal(Kind.Detected, d.Kind);
        Assert.Equal("overwatch", d.GameProfile);
        Assert.Equal(100u, d.Pid);
        Assert.Equal("Overwatch.exe", d.Exe);
        Assert.Equal(0xA1ul, d.Hwnd);
        Assert.Equal(1ul, d.Monitor);
        Assert.Equal(0x1001ul, d.AdapterLuid);
        Assert.Equal("Overwatch", d.WindowTitle);
        Assert.Equal(WindowMode.Borderless, d.WindowMode);
        Assert.Equal(DetectionMethod.Executable, d.Method);
        Assert.False(d.AllowPresentHook);
        Assert.False(d.AllowMemoryReading);
        Assert.EndsWith("Overwatch.exe", d.ExePath, StringComparison.Ordinal);
        Assert.Equal((ulong)_clock.Now - (ulong)QpcTime.FromTimeSpan(TimeSpan.FromSeconds(5), Freq), env[0].Qpc); // raw QPC of the confirming poll
        Assert.Equal(env[0].SessionId, env[1].SessionId);
        Assert.Equal(DetectionRunner.SourceId, env[0].SourceId);
        Assert.Equal([1ul, 2ul], env.Select(e => e.Seq));
    }

    [Fact]
    public void A_process_without_a_window_or_with_a_short_splash_is_never_announced()
    {
        _probe.Start(200, "Overwatch.exe");
        Assert.Empty(Ticks(10)); // no window: a launcher stub, a crash reporter

        _probe.Window(200, 0xB1, "splash", 600, 340);
        Assert.Empty(Ticks(2)); // a splash that lasts two seconds
        _probe.Stop(200);
        Assert.Empty(Ticks(10));
        Assert.Empty(Drain());
        Assert.Empty(_runner.Games);
    }

    [Fact]
    public void Tiny_windows_do_not_count_as_a_game_window()
    {
        _probe.Start(210, "Overwatch.exe");
        _probe.Window(210, 0xB2, "tooltip", 200, 40);
        Assert.Empty(Ticks(6));
        Assert.Contains(_runner.Pending, p => p.Pid == 210 && p.Note.Contains("window", StringComparison.Ordinal));
    }

    [Fact]
    public void An_alt_tab_shorter_than_the_focus_hold_does_not_flap_but_a_real_one_does()
    {
        StartRunning(300, "Overwatch.exe", 0xC1);

        // Away for under a poll interval: the poll never sees it.
        _probe.ForegroundPid = 0;
        _probe.ForegroundPid = 300;
        Assert.Empty(Tick());

        // Away for one poll only, back by the next: the hold (0.5 s) needs the change seen twice.
        _probe.ForegroundPid = 999;
        Assert.Empty(Tick(0.4));
        _probe.ForegroundPid = 300;
        Assert.Empty(Tick(0.4));

        _probe.ForegroundPid = 999;
        Assert.Empty(Tick());
        Assert.Equal([Kind.Unfocused], Tick());
        Assert.Empty(Ticks(3));
        _probe.ForegroundPid = 300;
        Assert.Empty(Tick());
        Assert.Equal([Kind.Focused], Tick());
    }

    [Fact]
    public void A_game_that_starts_in_the_background_is_announced_without_focus()
    {
        _probe.Start(310, "cs2.exe");
        _probe.Window(310, 0xC2, "Counter-Strike 2");
        _probe.ForegroundPid = 1;
        Assert.Equal([Kind.Detected], Ticks(4));
        Assert.False(_runner.Games.Single().Focused);
    }

    [Fact]
    public void Exit_is_announced_after_the_grace_with_the_last_state()
    {
        StartRunning(400, "Overwatch.exe", 0xD1);
        string id = _runner.Games.Single().Id;
        Drain();

        _probe.Stop(400);
        Assert.Empty(Ticks(5)); // gone from the first of these polls; grace 5 s
        Assert.Equal([Kind.Exited], Tick());
        Assert.Empty(_runner.Games);

        Envelope e = Drain().Single();
        Assert.Equal(id, e.SessionId);
        Assert.Equal(Kind.Exited, e.GameLifecycle.Kind);
        Assert.Equal(400u, e.GameLifecycle.Pid);
        Assert.Equal(0xD1ul, e.GameLifecycle.Hwnd);
    }

    [Fact]
    public void A_relaunch_within_the_grace_continues_the_same_run_as_a_window_change()
    {
        StartRunning(500, "FortniteClient-Win64-Shipping.exe", 0xE1);
        string id = _runner.Games.Single().Id;
        Drain();

        _probe.Stop(500);
        Assert.Empty(Ticks(2));
        _probe.Start(501, "FortniteClient-Win64-Shipping.exe");
        _probe.Window(501, 0xE2, "Fortnite");
        _probe.ForegroundPid = 501;
        Assert.Empty(Tick()); // the new window is held (1 s)
        Assert.Equal([Kind.WindowChanged], Tick());
        Assert.Empty(Ticks(10)); // and the old pid's exit is never announced

        GameInstance g = _runner.Games.Single();
        Assert.Equal(id, g.Id);
        Assert.Equal(501u, g.Pid);
        Assert.Equal(0xE2ul, g.Window.Handle);
        Envelope e = Drain().Single();
        Assert.Equal(id, e.SessionId);
        Assert.Equal(501u, e.GameLifecycle.Pid);
    }

    [Fact]
    public void A_new_window_monitor_or_mode_that_lasts_changes_the_target()
    {
        StartRunning(600, "Overwatch.exe", 0xF1);

        // Splash to game: a new, larger window replaces the first.
        _probe.CloseWindow(0xF1);
        _probe.Window(600, 0xF2, "Overwatch");
        Assert.Empty(Tick());
        Assert.Equal([Kind.WindowChanged], Tick());
        Assert.Equal(0xF2ul, _runner.Games.Single().Window.Handle);

        // Dragged to another monitor.
        _probe.Window(600, 0xF2, "Overwatch", monitor: 2, mode: WindowMode.Windowed);
        Assert.Empty(Tick());
        Assert.Equal([Kind.WindowChanged], Tick());
        GameInstance g = _runner.Games.Single();
        Assert.Equal(2ul, g.Window.Monitor);
        Assert.Equal(0x1002, g.AdapterLuid);
        Assert.Equal(WindowMode.Windowed, g.Window.Mode);

        // A mode flicker shorter than the hold changes nothing.
        _probe.Window(600, 0xF2, "Overwatch", monitor: 2, mode: WindowMode.Fullscreen);
        Assert.Empty(Tick(0.5));
        _probe.Window(600, 0xF2, "Overwatch", monitor: 2, mode: WindowMode.Windowed);
        Assert.Empty(Ticks(3));
    }

    [Fact]
    public void Minimizing_keeps_the_last_target()
    {
        StartRunning(650, "Overwatch.exe", 0xF5);
        _probe.Window(650, 0xF5, "Overwatch", 160, 28, minimized: true);
        Assert.DoesNotContain(Kind.WindowChanged, Ticks(3));
        Assert.Equal(1920, _runner.Games.Single().Window.Width);
    }

    [Fact]
    public void Title_changes_refresh_hints_without_publishing()
    {
        _probe.CommandLines[700] = "RobloxPlayerBeta.exe --app -t 1 roblox-player:1+launchmode:play+placeId%3D286090429";
        StartRunning(700, "RobloxPlayerBeta.exe", 0x71, "Roblox");
        GameInstance g = _runner.Games.Single();
        Assert.Contains("placeId%3D286090429", g.Hints.LaunchArgs, StringComparison.Ordinal);
        Assert.Equal(1, _probe.CommandLineCalls);
        Drain();

        var seen = new List<string>();
        _runner.HintsChanged += x => seen.Add(x.Hints.WindowTitle);
        _probe.Window(700, 0x71, "Roblox - Arsenal");
        Assert.Empty(Tick());
        Assert.Equal(["Roblox - Arsenal"], seen);
        Assert.Empty(Drain());
        Assert.Equal("Roblox - Arsenal", _runner.Games.Single().Window.Title);
    }

    [Fact]
    public void Launch_args_are_not_asked_for_games_that_do_not_use_them()
    {
        StartRunning(710, "Overwatch.exe", 0x72);
        Assert.Equal(0, _probe.CommandLineCalls);
    }

    [Fact]
    public void Only_candidate_processes_are_looked_up()
    {
        for (uint pid = 1; pid <= 50; pid++)
        {
            _probe.Start(pid, $"app{pid}.exe");
            _probe.Window(pid, 0x9000 + pid, "app");
        }

        _probe.Start(800, "Overwatch.exe");
        Ticks(3);
        Assert.Equal(1, _probe.DetailCalls); // once, for Overwatch.exe, and never again
    }

    [Fact]
    public void Two_games_at_once_each_get_their_own_run()
    {
        _probe.Start(900, "Overwatch.exe");
        _probe.Window(900, 0x91, "Overwatch");
        _probe.Start(901, "RocketLeague.exe");
        _probe.Window(901, 0x92, "Rocket League");
        Assert.Equal([Kind.Detected, Kind.Detected], Ticks(4));
        Assert.Equal(2, _runner.Games.Select(g => g.Id).Distinct().Count());
        Assert.Equal(2, Drain().Select(e => e.SessionId).Distinct().Count());
    }

    [Fact]
    public void Opting_in_takes_effect_on_the_next_poll_with_the_generic_profile()
    {
        _probe.Start(1000, "MyIndieGame.exe");
        _probe.Window(1000, 0x101, "My Indie Game");
        Assert.Empty(Ticks(5));

        _runner.UpdateSettings(new DetectionSettings().WithAllowed("MyIndieGame.exe"));
        Assert.Equal([Kind.Detected], Ticks(4));
        GameLifecycle d = Drain().Single().GameLifecycle;
        Assert.Equal("generic", d.GameProfile);
        Assert.Equal(DetectionMethod.Manual, d.Method);
        Assert.False(d.AllowPresentHook);
    }

    [Fact]
    public void A_failing_probe_or_subscriber_does_not_stop_detection()
    {
        _probe.Throw = true;
        Assert.Empty(Tick());
        Assert.IsType<InvalidOperationException>(_runner.LastError);
        _probe.Throw = false;

        _runner.Changed += _ => throw new InvalidOperationException("bad subscriber");
        _probe.Start(1100, "Overwatch.exe");
        _probe.Window(1100, 0x111, "Overwatch");
        Assert.Equal([Kind.Detected], Ticks(4));
        Assert.Equal("bad subscriber", _runner.LastError!.Message);
        Assert.Single(Drain());
    }

    [Fact]
    public async Task The_runner_polls_on_its_own_when_started()
    {
        var probe = new FakeProbe();
        probe.Start(1, "Overwatch.exe");
        probe.Window(1, 0x1, "Overwatch");
        var options = new DetectionOptions { PollInterval = TimeSpan.FromMilliseconds(20), StartHold = TimeSpan.Zero };
        var done = new TaskCompletionSource<LifecycleChange>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var runner = new DetectionRunner(_bus, probe, new GameMatcher(ProfileCatalog.BuiltIn), SystemClock.Instance, options);
        runner.Changed += c => done.TrySetResult(c);
        LifecycleChange first = await done.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(Kind.Detected, first.Kind);
    }

    private void StartRunning(uint pid, string exe, ulong hwnd, string title = "Game")
    {
        _probe.Start(pid, exe);
        _probe.Window(pid, hwnd, title);
        _probe.ForegroundPid = pid;
        Assert.Contains(Kind.Detected, Ticks(4));
        Assert.Empty(Tick());
    }
}
