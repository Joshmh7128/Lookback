using Lookback.Contracts.V1;
using Lookback.Core.Bus;
using Lookback.Core.Time;

namespace Lookback.Testbench.Live;

/// <summary>
/// Releases a footage replay's envelopes at the moment they happened in the
/// footage, so a viewer sees events fire as the clip would play.
/// <para>
/// The analyser runs ahead of real time and its video and audio threads
/// publish slightly out of order, so envelopes are held in a queue ordered by
/// QPC (ties keep arrival order, which keeps <c>SessionStarted</c> first) and
/// each is sent once the wall clock reaches it. <see cref="Pending"/> feeds a
/// <c>ReplayPacer</c> that stops the analyser running too far ahead, so the
/// source subscription never overflows and nothing is dropped.
/// </para>
/// </summary>
internal sealed class WallClockRelay
{
    private static readonly TimeSpan MaxNap = TimeSpan.FromMilliseconds(5);

    private readonly IEventSubscription _source;
    private readonly Func<Envelope, CancellationToken, ValueTask> _sink;
    private readonly IClock _wall;
    private readonly long _mediaFrequency;
    private readonly double _speed;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly PriorityQueue<Envelope, (ulong Qpc, long Order)> _held = new();
    private long _order;
    private int _heldCount;

    /// <param name="source">Subscription on the analyser's bus; the relay ends when it completes.</param>
    /// <param name="sink">Where released envelopes go: another bus or a pipe.</param>
    /// <param name="wall">Real time.</param>
    /// <param name="mediaFrequency">Ticks per second of the envelopes' QPC.</param>
    /// <param name="speed">1 is real time; 2 plays twice as fast.</param>
    /// <param name="delay">Injected for tests; defaults to <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.</param>
    public WallClockRelay(
        IEventSubscription source,
        Func<Envelope, CancellationToken, ValueTask> sink,
        IClock wall,
        long mediaFrequency,
        double speed = 1.0,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(wall);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(mediaFrequency);
        if (!(speed > 0) || double.IsInfinity(speed))
        {
            throw new ArgumentOutOfRangeException(nameof(speed), "Speed must be a positive number.");
        }

        _source = source;
        _sink = sink;
        _wall = wall;
        _mediaFrequency = mediaFrequency;
        _speed = speed;
        _delay = delay ?? Task.Delay;
    }

    /// <summary>Envelopes received but not yet released.</summary>
    public int Pending => _source.Count + Volatile.Read(ref _heldCount);

    /// <summary>Envelopes released so far.</summary>
    public long Released { get; private set; }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        long start = 0;
        bool anchored = false;
        bool sourceOpen = true;
        while (sourceOpen || _held.Count > 0)
        {
            while (_source.TryRead(out Envelope? e))
            {
                _held.Enqueue(e!, (e!.Qpc, _order++));
            }

            Volatile.Write(ref _heldCount, _held.Count);
            if (_held.Count == 0)
            {
                sourceOpen = await _source.WaitToReadAsync(cancellationToken).ConfigureAwait(false);
                continue;
            }

            _held.TryPeek(out Envelope? next, out _);
            if (!anchored)
            {
                // Footage time starts when the first envelope shows up, not when
                // the relay starts, so decoder start-up does not cause a burst.
                start = _wall.Now - ToWall(next!.Qpc);
                anchored = true;
            }

            long due = start + ToWall(next!.Qpc);
            long wait = due - _wall.Now;
            if (wait > 0)
            {
                // Short naps, so an earlier envelope that arrives meanwhile still goes first.
                TimeSpan nap = TimeSpan.FromSeconds((double)wait / _wall.Frequency);
                await _delay(nap < MaxNap ? nap : MaxNap, cancellationToken).ConfigureAwait(false);
                continue;
            }

            _held.Dequeue();
            Volatile.Write(ref _heldCount, _held.Count);
            await _sink(next, cancellationToken).ConfigureAwait(false);
            Released++;
        }
    }

    private long ToWall(ulong qpc) => (long)(qpc / (double)_mediaFrequency / _speed * _wall.Frequency);
}
