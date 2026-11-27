using Lookback.Contracts.V1;
using Lookback.Core.Bus;

namespace Lookback.Stream.SessionLog;

/// <summary>
/// Drains one session's events from the bus into a <see cref="SessionLogWriter"/>
/// on a background task, flushing on an interval. Only events for the
/// writer's session are recorded.
/// </summary>
public sealed class SessionLogRecorder : IAsyncDisposable
{
    private readonly SessionLogWriter _writer;
    private readonly IEventSubscription _subscription;
    private readonly string _sessionId;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _pump;

    public SessionLogRecorder(IEventBus bus, SessionLogWriter writer, string sessionId, TimeSpan? flushInterval = null)
    {
        ArgumentNullException.ThrowIfNull(bus);
        ArgumentNullException.ThrowIfNull(writer);
        _writer = writer;
        _sessionId = sessionId;
        _subscription = bus.Subscribe("*", capacity: 65_536);
        _pump = PumpAsync(flushInterval ?? TimeSpan.FromSeconds(1), _cts.Token);
    }

    /// <summary>Events lost because disk writes fell behind.</summary>
    public long Dropped => _subscription.Dropped;

    // Cancellation only interrupts waiting, never a write: a write cut off
    // part-way would leave a torn record in the middle of the log.
    private async Task PumpAsync(TimeSpan flushInterval, CancellationToken ct)
    {
        DateTime nextFlush = DateTime.UtcNow + flushInterval;
        try
        {
            while (await _subscription.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                await DrainQueuedAsync().ConfigureAwait(false);
                if (DateTime.UtcNow >= nextFlush)
                {
                    await _writer.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                    nextFlush = DateTime.UtcNow + flushInterval;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task DrainQueuedAsync()
    {
        while (_subscription.TryRead(out Envelope? envelope))
        {
            // Other sessions are not ours; the writer already holds our header.
            if (envelope!.SessionId == _sessionId && envelope.PayloadCase != Envelope.PayloadOneofCase.SessionStarted)
            {
                await _writer.AppendAsync(envelope, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Records whatever is already queued, then flushes and closes the log.</summary>
    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        await _pump.ConfigureAwait(false);
        await DrainQueuedAsync().ConfigureAwait(false);

        _subscription.Dispose();
        await _writer.FlushAsync().ConfigureAwait(false);
        await _writer.DisposeAsync().ConfigureAwait(false);
        _cts.Dispose();
    }
}
