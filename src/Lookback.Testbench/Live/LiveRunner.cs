using Lookback.Contracts;
using Lookback.Contracts.V1;
using Lookback.Core.Bus;
using Lookback.Core.Time;
using Lookback.Media.Ffmpeg;
using Lookback.Media.Frames;
using Lookback.Profiler;
using Lookback.Profiles.Detection;
using Lookback.Profiles.Model;
using Lookback.Profiles.Regions;
using Lookback.Signals;
using Lookback.Signals.Video;
using Lookback.Stream.Pipes;
using Lookback.Stream.SessionLog;
using Lookback.Stream.WebSockets;

namespace Lookback.Testbench.Live;

/// <summary>Inputs for <c>lookback-testbench live</c>.</summary>
internal sealed record LiveOptions
{
    public required string File { get; init; }

    public Retention Retention { get; init; } = Retention.Owned;

    public string PermissionRef { get; init; } = "";

    public string? Title { get; init; }

    public string? Game { get; init; }

    /// <summary>A built-in profile id or a path to a profile JSON file; its rules run live and fire game events.</summary>
    public string? Profile { get; init; }

    /// <summary>1 is real time.</summary>
    public double Speed { get; init; } = 1.0;

    /// <summary>Send to a running recorder on this pipe instead of hosting a profiler here.</summary>
    public string? PipeName { get; init; }

    /// <summary>Profiler port when self-hosting; 0 picks a free one.</summary>
    public int Port { get; init; }

    /// <summary>Self-hosting only: fixed token (tests); generated when null.</summary>
    public string? Token { get; init; }

    /// <summary>Self-hosting only: hold the clip until a profiler connects, so the start is not missed.</summary>
    public bool WaitForViewer { get; init; } = true;

    /// <summary>Play the clip again, as a new session, until cancelled.</summary>
    public bool Loop { get; init; }

    /// <summary>Self-hosting only: keep the profiler up after the clip ends, until cancelled.</summary>
    public bool KeepServing { get; init; }
}

/// <summary>What one live run sent.</summary>
internal sealed record LiveResult(int Plays, long Released);

/// <summary>
/// Plays footage in real time onto the event stream: the analyser publishes
/// onto a private bus, a <see cref="WallClockRelay"/> releases each envelope
/// when the clip reaches it, and the released envelopes go either to a
/// profiler hosted here or to a running recorder's pipe. Nothing is written
/// to disk.
/// </summary>
internal static class LiveRunner
{
    private const int HighWater = 4096;

    public static async Task<LiveResult> RunAsync(LiveOptions o, Action<string> announce, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(o);
        ArgumentNullException.ThrowIfNull(announce);

        string file = Path.GetFullPath(o.File);
        GameProfile? profile = o.Profile is null ? null : GameProfileLoader.Load(o.Profile);
        MediaInfo media = await MediaProbe.ProbeAsync(file, ct).ConfigureAwait(false);
        if (!media.HasVideo)
        {
            throw new InvalidDataException("The file has no video stream.");
        }

        // Validate before connecting anything, as analyze does.
        string? problem = RetentionRules.Validate(Started(o, file, profile));
        if (problem is not null)
        {
            throw new RetentionViolationException(problem);
        }

        PipeEventClient? pipe = null;
        InProcessEventBus? outBus = null;
        WebSocketBridge? bridge = null;
        try
        {
            Func<Envelope, CancellationToken, ValueTask> sink;
            if (o.PipeName is not null)
            {
                pipe = await PipeEventClient.ConnectAsync(o.PipeName, TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
                sink = pipe.PublishAsync;
                announce($"pipe     {o.PipeName} (open the recorder's LOOKBACK_PROFILER link)");
            }
            else
            {
                outBus = new InProcessEventBus();
                InProcessEventBus target = outBus;
                bridge = new WebSocketBridge(outBus, new WebSocketBridgeOptions { Port = o.Port, Token = o.Token, Pages = [ProfilerPage.Page] });
                await bridge.StartAsync(ct).ConfigureAwait(false);
                sink = (e, _) =>
                {
                    target.Publish(e);
                    return ValueTask.CompletedTask;
                };
                announce($"profiler {bridge.PageUrl(ProfilerPage.Path)}");
                if (o.WaitForViewer)
                {
                    announce("waiting  for the profiler to connect (open the link above)");
                    while (bridge.ConnectedClients == 0)
                    {
                        await Task.Delay(100, ct).ConfigureAwait(false);
                    }
                }
            }

            int plays = 0;
            long released = 0;
            do
            {
                plays++;
                announce($"playing  {Path.GetFileName(file)} at {o.Speed:0.##}x"
                    + (profile is null ? "" : $" with profile '{profile.Id}'")
                    + (o.Loop ? $" (play {plays})" : ""));
                released += await PlayOnceAsync(o, file, media, profile, sink, ct).ConfigureAwait(false);
            }
            while (o.Loop && !ct.IsCancellationRequested);

            if (bridge is not null && o.KeepServing)
            {
                announce("finished the clip; the profiler stays up until Ctrl+C");
                try
                {
                    await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }

            return new LiveResult(plays, released);
        }
        finally
        {
            if (bridge is not null)
            {
                await bridge.DisposeAsync().ConfigureAwait(false);
            }

            outBus?.Dispose();
            if (pipe is not null)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static async Task<long> PlayOnceAsync(
        LiveOptions o, string file, MediaInfo media, GameProfile? profile, Func<Envelope, CancellationToken, ValueTask> sink, CancellationToken ct)
    {
        string sessionId = TestbenchRunner.MakeSessionId(file) + "-live";
        var factory = new EnvelopeFactory(new ManualClock(TestbenchRunner.Frequency), sessionId, "testbench");
        Envelope header = factory.Create(Topics.Session, 0);
        header.SessionStarted = Started(o, file, profile);

        using var bus = new InProcessEventBus();
        using IEventSubscription sub = bus.Subscribe("*", capacity: HighWater * 4);
        var relay = new WallClockRelay(sub, sink, SystemClock.Instance, TestbenchRunner.Frequency, o.Speed);
        Task relaying = relay.RunAsync(ct);
        AnalysisFormat format = AnalysisFormat.Default;
        IVideoSignal[] extra = profile is { Regions.Count: > 0 } ? [new RegionSignal(profile.Regions, format.Width, format.Height)] : [];

        // The profile runs on the private bus, so its game events and rule
        // margins are released by the relay in step with the footage.
        ProfileRunner? runner = profile is null ? null : new ProfileRunner(bus, profile, sessionId, TestbenchRunner.Frequency);
        try
        {
            bus.Publish(header);
            var analyzer = new FootageAnalyzer(bus, format, TestbenchRunner.Frequency);
            var pacer = new ReplayPacer(() => Math.Max(relay.Pending, runner?.Queued ?? 0), HighWater);
            await analyzer.AnalyzeAsync(
                file,
                sessionId,
                new FootageAnalysisOptions { HasAudio = media.HasAudio, Pacer = pacer, ExtraVideoSignals = extra },
                ct).ConfigureAwait(false);

            if (runner is not null)
            {
                await runner.DisposeAsync().ConfigureAwait(false); // flushes its last events
                runner = null;
            }

            Envelope end = factory.Create(Topics.Session, QpcTime.FromTimeSpan(media.Duration, TestbenchRunner.Frequency));
            end.SessionEnded = new SessionEnded { Reason = "end of file" };
            bus.Publish(end);
        }
        finally
        {
            if (runner is not null)
            {
                await runner.DisposeAsync().ConfigureAwait(false);
            }

            bus.Dispose(); // completes the subscription, so the relay drains and stops
            await relaying.ConfigureAwait(false);
        }

        if (sub.Dropped > 0)
        {
            throw new InvalidOperationException($"The live relay dropped {sub.Dropped} events.");
        }

        return relay.Released;
    }

    private static SessionStarted Started(LiveOptions o, string file, GameProfile? profile) => new()
    {
        SourceKind = SourceKind.File,
        Retention = o.Retention,
        PermissionRef = o.PermissionRef,
        Title = o.Title ?? Path.GetFileName(file),
        QpcFrequency = TestbenchRunner.Frequency,
        GameProfile = o.Game ?? profile?.Id ?? "",
    };
}
