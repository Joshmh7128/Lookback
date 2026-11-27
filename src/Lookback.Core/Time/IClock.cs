namespace Lookback.Core.Time;

/// <summary>
/// The one clock every component maps to. Live sessions use the system
/// performance counter; FileSource and tests use a manual clock so footage can
/// be replayed faster than real time with consistent timestamps.
/// </summary>
public interface IClock
{
    /// <summary>Ticks per second.</summary>
    long Frequency { get; }

    /// <summary>Current tick count.</summary>
    long Now { get; }
}

/// <summary>
/// QueryPerformanceCounter on Windows (via <see cref="System.Diagnostics.Stopwatch"/>),
/// the platform monotonic clock elsewhere. Same source as DXGI present
/// statistics and WGC frame times, so captured frames need no clock conversion.
/// </summary>
public sealed class SystemClock : IClock
{
    public static SystemClock Instance { get; } = new();

    private SystemClock()
    {
    }

    public long Frequency => System.Diagnostics.Stopwatch.Frequency;

    public long Now => System.Diagnostics.Stopwatch.GetTimestamp();
}

/// <summary>A clock that moves only when told to. Thread-safe.</summary>
public sealed class ManualClock : IClock
{
    private long _now;

    public ManualClock(long frequency = 10_000_000, long start = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frequency);
        Frequency = frequency;
        _now = start;
    }

    public long Frequency { get; }

    public long Now => Interlocked.Read(ref _now);

    public void Advance(long ticks)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(ticks);
        Interlocked.Add(ref _now, ticks);
    }

    public void Advance(TimeSpan duration) => Advance(QpcTime.FromTimeSpan(duration, Frequency));
}
