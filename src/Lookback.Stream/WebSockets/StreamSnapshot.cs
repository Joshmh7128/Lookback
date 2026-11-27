using Lookback.Contracts.V1;
using Lookback.Core.Bus;

namespace Lookback.Stream.WebSockets;

/// <summary>
/// Remembers the "once per session" envelopes a client needs to make sense
/// of the stream when it connects late: each live session's
/// <c>SessionStarted</c> (for the QPC frequency and game), its latest
/// <c>GameLifecycle</c>, and every producer's latest <c>SignalSchema</c>
/// (without which <c>SignalFrame</c> values have no names). A session is
/// forgotten when its <c>SessionEnded</c> arrives, and the oldest sessions
/// are evicted past <see cref="MaxSessions"/>, so memory stays bounded in a
/// long-running recorder.
/// </summary>
internal sealed class StreamSnapshot : IAsyncDisposable
{
    public const int MaxSessions = 32;

    private readonly IEventSubscription _subscription;
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _lock = new();
    private readonly List<string> _order = [];
    private readonly Dictionary<string, SessionState> _sessions = new(StringComparer.Ordinal);
    private readonly Task _pump;
    private long _seen;

    public StreamSnapshot(IEventBus bus)
    {
        ArgumentNullException.ThrowIfNull(bus);

        // Large queue: the pump does almost no work, but a burst of frames must
        // not push a schema out of the queue before it is read.
        _subscription = bus.Subscribe("*", capacity: 16_384);
        _pump = Task.Run(() => PumpAsync(_stop.Token));
    }

    /// <summary>Envelopes processed so far. For tests.</summary>
    internal long Seen => Interlocked.Read(ref _seen);

    /// <summary>The envelopes a new client should receive first, sessions in the order they started.</summary>
    public IReadOnlyList<Envelope> Capture()
    {
        var result = new List<Envelope>();
        lock (_lock)
        {
            foreach (string id in _order)
            {
                SessionState s = _sessions[id];
                if (s.Started is not null)
                {
                    result.Add(s.Started);
                }

                if (s.Lifecycle is not null)
                {
                    result.Add(s.Lifecycle);
                }

                result.AddRange(s.Schemas.Values);
            }
        }

        return result;
    }

    private async Task PumpAsync(CancellationToken ct)
    {
        try
        {
            await foreach (Envelope e in _subscription.ReadAllAsync(ct).ConfigureAwait(false))
            {
                Observe(e);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    internal void Observe(Envelope e)
    {
        lock (_lock)
        {
            Interlocked.Increment(ref _seen);
            switch (e.PayloadCase)
            {
                case Envelope.PayloadOneofCase.SessionStarted:
                    _sessions.Remove(e.SessionId);
                    _order.Remove(e.SessionId);
                    Get(e.SessionId).Started = e;
                    break;
                case Envelope.PayloadOneofCase.SessionEnded:
                    _sessions.Remove(e.SessionId);
                    _order.Remove(e.SessionId);
                    break;
                case Envelope.PayloadOneofCase.GameLifecycle:
                    Get(e.SessionId).Lifecycle = e;
                    break;
                case Envelope.PayloadOneofCase.SignalSchema:
                    Get(e.SessionId).Schemas[(e.SourceId, e.SignalSchema.SchemaId)] = e;
                    break;
                default:
                    break;
            }
        }
    }

    private SessionState Get(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out SessionState? s))
        {
            if (_order.Count >= MaxSessions)
            {
                _sessions.Remove(_order[0]);
                _order.RemoveAt(0);
            }

            s = new SessionState();
            _sessions.Add(sessionId, s);
            _order.Add(sessionId);
        }

        return s;
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        await _pump.ConfigureAwait(false);
        _subscription.Dispose();
        _stop.Dispose();
    }

    private sealed class SessionState
    {
        public Envelope? Started { get; set; }

        public Envelope? Lifecycle { get; set; }

        public Dictionary<(string Source, uint SchemaId), Envelope> Schemas { get; } = [];
    }
}
