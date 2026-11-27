using Lookback.Contracts;
using Lookback.Contracts.V1;
using Lookback.Core.Bus;

namespace Lookback.Recorder;

/// <summary>
/// What the recorder knows about recent sessions on its bus: each session's
/// <c>SessionStarted</c> header (for retention checks) and its clip
/// candidates (so a rating can be joined to its candidate). Bounded: the
/// oldest sessions are forgotten first, and so are their ratings' context.
/// </summary>
internal sealed class SessionDirectory : IAsyncDisposable
{
    public const int MaxSessions = 64;
    public const int MaxCandidatesPerSession = 10_000;

    private readonly IEventSubscription _subscription;
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _lock = new();
    private readonly List<string> _order = [];
    private readonly Dictionary<string, Entry> _sessions = new(StringComparer.Ordinal);
    private readonly Task _pump;

    public SessionDirectory(IEventBus bus)
    {
        ArgumentNullException.ThrowIfNull(bus);
        _subscription = bus.Subscribe("*", capacity: 1 << 16);
        _pump = Task.Run(PumpAsync);
    }

    /// <summary>The header of <paramref name="sessionId"/>, or null if the recorder has not seen it (or forgot it).</summary>
    public SessionStarted? Session(string sessionId)
    {
        lock (_lock)
        {
            return _sessions.TryGetValue(sessionId, out Entry? e) ? e.Started : null;
        }
    }

    public ClipCandidate? Candidate(string sessionId, string candidateId)
    {
        lock (_lock)
        {
            return _sessions.TryGetValue(sessionId, out Entry? e) && e.Candidates.TryGetValue(candidateId, out ClipCandidate? c) ? c : null;
        }
    }

    /// <summary>Applies one envelope; the pump calls this, and tests can too.</summary>
    internal void Apply(Envelope e)
    {
        lock (_lock)
        {
            switch (e.PayloadCase)
            {
                case Envelope.PayloadOneofCase.SessionStarted:
                    if (!_sessions.ContainsKey(e.SessionId))
                    {
                        _order.Add(e.SessionId);
                        if (_order.Count > MaxSessions)
                        {
                            _sessions.Remove(_order[0]);
                            _order.RemoveAt(0);
                        }
                    }

                    _sessions[e.SessionId] = new Entry(e.SessionStarted);
                    break;
                case Envelope.PayloadOneofCase.ClipCandidate when _sessions.TryGetValue(e.SessionId, out Entry? s):
                    if (s.Candidates.Count < MaxCandidatesPerSession || s.Candidates.ContainsKey(e.ClipCandidate.CandidateId))
                    {
                        s.Candidates[e.ClipCandidate.CandidateId] = e.ClipCandidate;
                    }

                    break;
            }
        }
    }

    private async Task PumpAsync()
    {
        try
        {
            await foreach (Envelope e in _subscription.ReadAllAsync(_stop.Token).ConfigureAwait(false))
            {
                if (e.Topic == Topics.Session || e.Topic == Topics.ClipCandidate)
                {
                    Apply(e);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        _subscription.Dispose();
        try
        {
            await _pump.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _stop.Dispose();
    }

    private sealed class Entry(SessionStarted started)
    {
        public SessionStarted Started { get; } = started;

        public Dictionary<string, ClipCandidate> Candidates { get; } = new(StringComparer.Ordinal);
    }
}
