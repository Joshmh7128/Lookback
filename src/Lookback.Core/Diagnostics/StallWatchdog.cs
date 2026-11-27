using Lookback.Core.Time;

namespace Lookback.Core.Diagnostics;

/// <summary>What a watched thread reports: whether it is inside a step that must finish, which step, and since when (raw QPC).</summary>
public readonly record struct Heartbeat(bool Busy, string Step, long SinceQpc);

/// <summary>A step that ran longer than its watch allows.</summary>
public sealed record StallReport(string Name, string Step, TimeSpan For);

/// <summary>
/// A heartbeat a hot thread updates without allocating: <see cref="Enter"/>
/// before a step that must finish (a driver call, a lock, an MFT call),
/// <see cref="Exit"/> after it. Step names should be string literals.
/// </summary>
public sealed class Beat
{
    private long _since;
    private string _step = "idle";
    private volatile bool _busy;

    public void Enter(string step, long nowQpc)
    {
        Volatile.Write(ref _since, nowQpc);
        Volatile.Write(ref _step, step);
        _busy = true;
    }

    public void Exit() => _busy = false;

    public Heartbeat Read()
    {
        bool busy = _busy;
        return new Heartbeat(busy, Volatile.Read(ref _step), Volatile.Read(ref _since));
    }
}

/// <summary>
/// Watches heartbeats and calls a watch's handler once per episode when a
/// thread has been busy in one step for longer than that watch's threshold.
/// It never blocks or interrupts the stuck thread; the handler decides what
/// to do (swap in a fallback, or end the process with <see cref="Fatal"/>).
/// Handlers run on the watchdog thread and must not block for long.
/// </summary>
public sealed class StallWatchdog : IDisposable
{
    private readonly IClock _clock;
    private readonly Lock _lock = new();
    private readonly List<Entry> _entries = [];
    private readonly Thread? _thread;
    private readonly TimeSpan _period;
    private volatile bool _stop;

    /// <param name="clock">The session clock.</param>
    /// <param name="period">How often to check. Null: no thread; call <see cref="CheckNow"/> yourself (tests).</param>
    public StallWatchdog(IClock clock, TimeSpan? period = null)
    {
        ArgumentNullException.ThrowIfNull(clock);
        _clock = clock;
        if (period is { } p)
        {
            _period = p;
            _thread = new Thread(Run) { IsBackground = true, Name = "lookback-watchdog", Priority = ThreadPriority.AboveNormal };
            _thread.Start();
        }
    }

    /// <summary>Starts watching <paramref name="probe"/>. Dispose the result to stop.</summary>
    public IDisposable Watch(string name, Func<Heartbeat> probe, TimeSpan threshold, Action<StallReport> onStall)
    {
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(onStall);
        var e = new Entry(this, name, probe, QpcTime.Rescale(threshold.Ticks, TimeSpan.TicksPerSecond, _clock.Frequency), onStall);
        lock (_lock)
        {
            _entries.Add(e);
        }

        return e;
    }

    /// <summary>Watches a <see cref="Beat"/>.</summary>
    public IDisposable Watch(string name, Beat beat, TimeSpan threshold, Action<StallReport> onStall)
    {
        ArgumentNullException.ThrowIfNull(beat);
        return Watch(name, beat.Read, threshold, onStall);
    }

    /// <summary>Checks every watch once; returns how many stalls were reported.</summary>
    public int CheckNow()
    {
        Entry[] entries;
        lock (_lock)
        {
            entries = [.. _entries];
        }

        int fired = 0;
        long now = _clock.Now;
        foreach (Entry e in entries)
        {
            Heartbeat h = e.Probe();
            if (!h.Busy || now - h.SinceQpc < e.ThresholdQpc || e.ReportedSince == h.SinceQpc)
            {
                continue;
            }

            e.ReportedSince = h.SinceQpc;
            fired++;
            var span = TimeSpan.FromTicks(QpcTime.Rescale(now - h.SinceQpc, _clock.Frequency, TimeSpan.TicksPerSecond));
            e.OnStall(new StallReport(e.Name, h.Step, span));
        }

        return fired;
    }

    public void Dispose()
    {
        _stop = true;
        _thread?.Join(_period * 4);
    }

    private void Run()
    {
        while (!_stop)
        {
            Thread.Sleep(_period);
            if (!_stop)
            {
                CheckNow();
            }
        }
    }

    private sealed class Entry(StallWatchdog owner, string name, Func<Heartbeat> probe, long thresholdQpc, Action<StallReport> onStall) : IDisposable
    {
        public string Name { get; } = name;

        public Func<Heartbeat> Probe { get; } = probe;

        public long ThresholdQpc { get; } = thresholdQpc;

        public Action<StallReport> OnStall { get; } = onStall;

        public long ReportedSince { get; set; } = long.MinValue;

        public void Dispose()
        {
            lock (owner._lock)
            {
                owner._entries.Remove(this);
            }
        }
    }
}

/// <summary>
/// The last resort when something cannot be recovered: report and end the
/// process instead of hanging. The shell sees the recorder exit and restarts
/// it; segments already on disk survive. Components take the handler as an
/// <c>Action&lt;string&gt;</c> so tests can pass their own.
/// </summary>
public static class Fatal
{
    /// <summary>Writes the reason to stderr, then <see cref="Environment.FailFast(string)"/>.</summary>
    public static readonly Action<string> Default = reason =>
    {
        Console.Error.WriteLine($"lookback: fatal: {reason}");
        Console.Error.Flush();
        Environment.FailFast($"Lookback: {reason}");
    };
}
