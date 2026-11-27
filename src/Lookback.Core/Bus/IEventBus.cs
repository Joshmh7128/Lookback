using Lookback.Contracts.V1;

namespace Lookback.Core.Bus;

/// <summary>
/// The local event stream. Producers publish envelopes; each subscriber gets
/// its own bounded queue so one slow consumer (the UI, a logger) can never
/// back-pressure capture or analysis. Transport-agnostic: in-process today,
/// pipes and WebSocket bridge onto it, and another backend can replace it.
/// </summary>
public interface IEventBus
{
    /// <summary>Never blocks. Returns false if the bus is completed.</summary>
    bool Publish(Envelope envelope);

    /// <param name="topicPattern">Exact topic, <c>*</c>, or a prefix like <c>signals.*</c>.</param>
    /// <param name="capacity">Per-subscriber queue length; the oldest events drop when full.</param>
    IEventSubscription Subscribe(string topicPattern, int capacity = 4096);
}

/// <summary>A subscriber's view of the bus. Dispose to unsubscribe.</summary>
public interface IEventSubscription : IDisposable
{
    string TopicPattern { get; }

    /// <summary>Events dropped because this subscriber fell behind.</summary>
    long Dropped { get; }

    IAsyncEnumerable<Envelope> ReadAllAsync(CancellationToken cancellationToken = default);

    bool TryRead(out Envelope? envelope);

    /// <summary>Waits until an event is queued. False once the subscription is completed and drained.</summary>
    ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken = default);
}
