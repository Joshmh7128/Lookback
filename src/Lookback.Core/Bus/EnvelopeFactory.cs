using Lookback.Contracts.V1;
using Lookback.Core.Time;

namespace Lookback.Core.Bus;

/// <summary>
/// Stamps envelopes for one producer: clock time, session, source id and a
/// monotonic sequence number so consumers can detect gaps. Thread-safe.
/// </summary>
public sealed class EnvelopeFactory
{
    private readonly IClock _clock;
    private long _seq;

    public EnvelopeFactory(IClock clock, string sessionId, string sourceId)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        ArgumentException.ThrowIfNullOrEmpty(sourceId);
        _clock = clock;
        SessionId = sessionId;
        SourceId = sourceId;
    }

    public string SessionId { get; }

    public string SourceId { get; }

    /// <summary>Creates an envelope stamped now. Set the payload on the result.</summary>
    public Envelope Create(string topic, long? qpc = null) => new()
    {
        Qpc = (ulong)(qpc ?? _clock.Now),
        SessionId = SessionId,
        Topic = topic,
        SourceId = SourceId,
        Seq = (ulong)Interlocked.Increment(ref _seq),
    };
}
