using Lookback.Contracts;
using Lookback.Contracts.V1;
using Lookback.Core.Bus;
using Lookback.Core.Time;
using Lookback.Detection.Lifecycle;
using Lookback.Detection.Matching;
using Lookback.Detection.Probe;

namespace Lookback.Detection;

/// <summary>
/// Watches the machine for games and publishes their lifecycle on the bus:
/// every <see cref="DetectionOptions.PollInterval"/> it takes a snapshot from
/// the probe, runs the <see cref="GameDetector"/>, and publishes each
/// confirmed change as a <c>game.lifecycle</c> envelope stamped with the QPC
/// time it was confirmed. Each run of a game gets its own session id
/// (<see cref="GameInstance.Id"/>). The recorder hosts one runner for the
/// life of the process and starts or stops capture from <see cref="Changed"/>.
/// <para>
/// A probe failure or a throwing subscriber is recorded in
/// <see cref="LastError"/> and the next poll goes ahead: detection never
/// stops because one look at the machine failed.
/// </para>
/// </summary>
public sealed class DetectionRunner : IAsyncDisposable
{
    public const string SourceId = "detection";

    private readonly IEventBus _bus;
    private readonly ISystemProbe _probe;
    private readonly IClock _clock;
    private readonly GameDetector _detector;
    private readonly DetectionOptions _options;
    private readonly Dictionary<string, EnvelopeFactory> _factories = new(StringComparer.Ordinal);
    private readonly List<LifecycleChange> _changes = [];
    private readonly List<GameInstance> _hints = [];
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop = Task.CompletedTask;
    private long _polls;
    private long _published;
    private Exception? _lastError;

    /// <summary>Starts polling at once unless <c>start</c> is false, which leaves polling to the caller (<see cref="Poll"/>); tests drive it that way with a manual clock.</summary>
    public DetectionRunner(IEventBus bus, ISystemProbe probe, GameMatcher matcher, IClock clock, DetectionOptions? options = null, bool start = true)
    {
        ArgumentNullException.ThrowIfNull(bus);
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(matcher);
        ArgumentNullException.ThrowIfNull(clock);
        _bus = bus;
        _probe = probe;
        _clock = clock;
        _options = options ?? new DetectionOptions();
        _detector = new GameDetector(probe, matcher, clock.Frequency, _options);
        if (start)
        {
            _loop = Task.Run(() => LoopAsync(_cts.Token));
        }
    }

    /// <summary>
    /// Raised on the poll thread after each change is published on the bus, in
    /// order. Keep handlers short; start capture on another thread.
    /// </summary>
    public event Action<LifecycleChange>? Changed;

    /// <summary>Raised when a running game's window title changes: pass <see cref="GameInstance.Hints"/> to <c>ProfileRunner.ApplyHints</c>.</summary>
    public event Action<GameInstance>? HintsChanged;

    /// <summary>Games running now, oldest first.</summary>
    public IReadOnlyList<GameInstance> Games
    {
        get
        {
            lock (_gate)
            {
                return _detector.Games;
            }
        }
    }

    /// <summary>Candidate processes not announced, and why (for the bench tool).</summary>
    public IReadOnlyList<(uint Pid, string Exe, string Note)> Pending
    {
        get
        {
            lock (_gate)
            {
                return [.. _detector.Pending];
            }
        }
    }

    public GameMatcher Matcher
    {
        get
        {
            lock (_gate)
            {
                return _detector.Matcher;
            }
        }
    }

    public long Polls => Interlocked.Read(ref _polls);

    public long Published => Interlocked.Read(ref _published);

    /// <summary>The most recent failure of a poll or a subscriber, if any.</summary>
    public Exception? LastError => Volatile.Read(ref _lastError);

    /// <summary>Applies new user settings from the next poll on. Games already running keep their profile.</summary>
    public void UpdateSettings(DetectionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (_gate)
        {
            _detector.Matcher = new GameMatcher(_detector.Matcher.Catalog, settings);
        }
    }

    /// <summary>Looks at the machine once and publishes what changed. The loop calls this; so can a caller that passed <c>start: false</c>.</summary>
    public IReadOnlyList<LifecycleChange> Poll()
    {
        LifecycleChange[] changes;
        GameInstance[] hints;
        lock (_gate)
        {
            _changes.Clear();
            _hints.Clear();
            try
            {
                SystemSnapshot snapshot = _probe.Snapshot();
                _detector.Update(snapshot, _clock.Now, _changes, _hints);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Volatile.Write(ref _lastError, ex);
            }

            changes = [.. _changes];
            hints = [.. _hints];
            foreach (LifecycleChange c in changes)
            {
                Publish(c);
            }
        }

        Interlocked.Increment(ref _polls);
        foreach (LifecycleChange c in changes)
        {
            Raise(() => Changed?.Invoke(c));
        }

        foreach (GameInstance g in hints)
        {
            Raise(() => HintsChanged?.Invoke(g));
        }

        return changes;
    }

    private void Publish(LifecycleChange c)
    {
        if (!_factories.TryGetValue(c.Game.Id, out EnvelopeFactory? f))
        {
            f = new EnvelopeFactory(_clock, c.Game.Id, SourceId);
            _factories[c.Game.Id] = f;
        }

        Envelope e = f.Create(Topics.GameLifecycle, c.At);
        e.GameLifecycle = c.ToProto();
        _bus.Publish(e);
        Interlocked.Increment(ref _published);
        if (c.Kind == GameLifecycle.Types.Kind.Exited)
        {
            _factories.Remove(c.Game.Id);
        }
    }

    private void Raise(Action a)
    {
        try
        {
            a();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Volatile.Write(ref _lastError, ex);
        }
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_options.PollInterval);
        try
        {
            do
            {
                Poll();
            }
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Stops polling. Running games are not announced as stopped: the recorder ends its own sessions.</summary>
    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        await _loop.ConfigureAwait(false);
        _cts.Dispose();
    }
}
