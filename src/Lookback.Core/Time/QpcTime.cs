namespace Lookback.Core.Time;

/// <summary>Overflow-safe conversions between performance-counter ticks and wall durations.</summary>
public static class QpcTime
{
    /// <summary>Converts ticks to a <see cref="TimeSpan"/> without overflowing at large tick counts.</summary>
    public static TimeSpan ToTimeSpan(long ticks, long frequency)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frequency);
        long whole = Math.DivRem(ticks, frequency, out long rem);
        return TimeSpan.FromTicks((whole * TimeSpan.TicksPerSecond) + (rem * TimeSpan.TicksPerSecond / frequency));
    }

    /// <summary>Converts a <see cref="TimeSpan"/> to ticks at <paramref name="frequency"/>.</summary>
    public static long FromTimeSpan(TimeSpan duration, long frequency)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frequency);
        long whole = Math.DivRem(duration.Ticks, TimeSpan.TicksPerSecond, out long rem);
        return (whole * frequency) + (rem * frequency / TimeSpan.TicksPerSecond);
    }

    /// <summary>Rescales ticks from one frequency to another (for example 48 kHz audio samples to QPC).</summary>
    public static long Rescale(long ticks, long fromFrequency, long toFrequency)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fromFrequency);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(toFrequency);
        return (long)((Int128)ticks * toFrequency / fromFrequency);
    }
}
