using Lookback.Contracts;
using Lookback.Contracts.V1;
using Lookback.Core.Bus;

namespace Lookback.Scoring.Ratings;

/// <summary>What <see cref="RatingSink"/> did with one <c>clip.rating</c>.</summary>
public sealed record RatingOutcome(ClipRating Rating, RatingWriteResult Result);

/// <summary>Options for <see cref="RatingSink"/>.</summary>
public sealed record RatingSinkOptions
{
    /// <summary>
    /// Finds the header of a session the sink has not seen on the bus (for
    /// example one the shell loaded from a session log). Null, or returning
    /// null, refuses the rating: retention is never guessed.
    /// </summary>
    public Func<string, SessionStarted?>? SessionLookup { get; init; }

    /// <summary>Finds a candidate the sink has not seen on the bus, by (session id, candidate id).</summary>
    public Func<string, string, ClipCandidate?>? CandidateLookup { get; init; }

    /// <summary>Written to <see cref="RatingRecord.Source"/>.</summary>
    public string Source { get; init; } = "shell";

    /// <summary>Candidates remembered for joining ratings to features; the oldest are forgotten first.</summary>
    public int MaxCandidates { get; init; } = 8192;

    /// <summary>Called on the sink's task after every rating, written or refused (for example to log or notify the shell).</summary>
    public Action<RatingOutcome>? OnOutcome { get; init; }
}

/// <summary>
/// Persists <c>clip.rating</c> events from the bus into a <see cref="RatingStore"/>.
/// It remembers session headers (<c>session</c>) and candidates
/// (<c>clip.candidate</c>) as they pass, so each rating is stored with its
/// candidate's features and checked against its session's retention. A rating
/// of a session whose header is unknown is refused, never stored on a guess.
/// Ratings are handled one at a time on a background task.
/// </summary>
public sealed class RatingSink : IAsyncDisposable
{
    private readonly RatingStore _store;
    private readonly RatingSinkOptions _options;
    private readonly IEventSubscription _sessions;
    private readonly IEventSubscription _clips;
    private readonly Dictionary<string, SessionStarted> _sessionHeaders = new(StringComparer.Ordinal);
    private readonly Dictionary<(string, string), ClipCandidate> _candidates = [];
    private readonly Queue<(string, string)> _candidateOrder = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _pump;
    private long _written;
    private long _refused;

    public RatingSink(IEventBus bus, RatingStore store, RatingSinkOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(bus);
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        _options = options ?? new RatingSinkOptions();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_options.MaxCandidates);
        _sessions = bus.Subscribe(Topics.Session, 1024);
        // One subscription for candidates and ratings keeps their order: a
        // candidate published before its rating is always remembered first.
        _clips = bus.Subscribe("clip.*", 16_384);
        _pump = PumpAsync(_cts.Token);
    }

    /// <summary>Ratings written so far.</summary>
    public long Written => Interlocked.Read(ref _written);

    /// <summary>Ratings refused so far.</summary>
    public long Refused => Interlocked.Read(ref _refused);

    /// <summary>Events lost because the sink fell behind (should stay 0).</summary>
    public long Dropped => _sessions.Dropped + _clips.Dropped;

    private async Task PumpAsync(CancellationToken ct)
    {
        try
        {
            while (await _clips.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                Drain();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    // Runs on the pump, or after it stopped (dispose): never concurrently.
    private void Drain()
    {
        DrainSessions();
        while (_clips.TryRead(out Envelope? e))
        {
            switch (e!.PayloadCase)
            {
                case Envelope.PayloadOneofCase.ClipCandidate:
                    Remember(e.SessionId, e.ClipCandidate);
                    break;
                case Envelope.PayloadOneofCase.ClipRating:
                    DrainSessions(); // a session that started since the last rating
                    Handle(e.SessionId, e.ClipRating);
                    break;
                default:
                    break;
            }
        }
    }

    private void DrainSessions()
    {
        while (_sessions.TryRead(out Envelope? e))
        {
            if (e!.PayloadCase == Envelope.PayloadOneofCase.SessionStarted && e.SessionId.Length > 0)
            {
                _sessionHeaders[e.SessionId] = e.SessionStarted;
            }
        }
    }

    private void Remember(string sessionId, ClipCandidate candidate)
    {
        var key = (sessionId, candidate.CandidateId);
        if (_candidates.TryAdd(key, candidate))
        {
            _candidateOrder.Enqueue(key);
            while (_candidateOrder.Count > _options.MaxCandidates)
            {
                _candidates.Remove(_candidateOrder.Dequeue());
            }
        }
        else
        {
            _candidates[key] = candidate; // a later publish (say, with features) replaces the first
        }
    }

    private void Handle(string envelopeSession, ClipRating rating)
    {
        if (rating.SessionId.Length == 0)
        {
            rating = new ClipRating(rating) { SessionId = envelopeSession };
        }

        RatingWriteResult result;
        try
        {
            SessionStarted? session = _sessionHeaders.GetValueOrDefault(rating.SessionId)
                ?? _options.SessionLookup?.Invoke(rating.SessionId);
            ClipCandidate? candidate = _candidates.GetValueOrDefault((rating.SessionId, rating.CandidateId))
                ?? _options.CandidateLookup?.Invoke(rating.SessionId, rating.CandidateId);
            result = _store.Append(session, rating, candidate, _options.Source);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            result = RatingWriteResult.Refused($"Could not write the rating: {ex.Message}");
        }

        if (result.Written)
        {
            Interlocked.Increment(ref _written);
        }
        else
        {
            Interlocked.Increment(ref _refused);
        }

        _options.OnOutcome?.Invoke(new RatingOutcome(rating, result));
    }

    /// <summary>Handles every rating already queued, then stops.</summary>
    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        await _pump.ConfigureAwait(false);
        Drain();
        _sessions.Dispose();
        _clips.Dispose();
        _cts.Dispose();
    }
}
