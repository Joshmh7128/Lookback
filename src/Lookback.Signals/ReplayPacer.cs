namespace Lookback.Signals;

/// <summary>
/// Paces a replay to its slowest lossless consumer. Live capture runs at the
/// game's speed and the bus may drop for a slow consumer; a footage replay
/// runs as fast as it can decode, so it waits here instead, keeping a
/// consumer's queue (for example the session log) below a high-water mark so
/// nothing is dropped.
/// </summary>
public sealed class ReplayPacer
{
    private readonly Func<int> _queueDepth;
    private readonly int _highWater;

    public ReplayPacer(Func<int> queueDepth, int highWater = 4096)
    {
        ArgumentNullException.ThrowIfNull(queueDepth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(highWater);
        _queueDepth = queueDepth;
        _highWater = highWater;
    }

    public async ValueTask WaitAsync(CancellationToken cancellationToken)
    {
        while (_queueDepth() > _highWater)
        {
            await Task.Delay(1, cancellationToken).ConfigureAwait(false);
        }
    }
}
