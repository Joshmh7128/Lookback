using Google.Protobuf;
using Lookback.Contracts;
using Lookback.Contracts.V1;
using Lookback.Core.Bus;
using Lookback.Replay.Store;

namespace Lookback.Recorder;

/// <summary>
/// Keeps each recorded live session's own envelopes in its store folder
/// (<see cref="SegmentStore.AppendEvent"/>): its start and end, candidates, QA
/// reports, ratings and saved clips, never signal frames. After a restart
/// the recorder reloads them, so the session's candidates can still be
/// listed, rated, watched and saved, and the shell rebuilds its library from
/// the same lines it would have seen live. Lines are the bridge's own JSON,
/// so they parse exactly like the live stream. Only sessions that may
/// persist (live games, owned) are kept.
/// </summary>
internal sealed class SessionHistory : IAsyncDisposable
{
    public const int MaxPendingLines = 1000;

    private static readonly JsonFormatter Format = new(JsonFormatter.Settings.Default.WithFormatDefaultValues(false));
    private static readonly JsonParser Parse = new(JsonParser.Settings.Default.WithIgnoreUnknownFields(true));

    private readonly SegmentStore _store;
    private readonly IEventSubscription _subscription;
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _lock = new();
    private readonly HashSet<string> _recorded = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _pending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ClipSaved> _savedByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<(string, string)> _kept = [];
    private readonly Task _pump;

    public SessionHistory(IEventBus bus, SegmentStore store)
    {
        ArgumentNullException.ThrowIfNull(bus);
        _store = store;
        _subscription = bus.Subscribe("*", capacity: 1 << 14);
        _pump = Task.Run(PumpAsync);
    }

    /// <summary>A live session's new candidate was recorded: (session id, candidate). Raised on the history's pump.</summary>
    public event Action<string, ClipCandidate>? CandidateRecorded;

    /// <summary>Parses a stored line back into its envelope; null for a torn or foreign line.</summary>
    public static Envelope? ParseLine(string line)
    {
        try
        {
            return Parse.Parse<Envelope>(line);
        }
        catch (InvalidProtocolBufferException)
        {
            return null;
        }
        catch (InvalidJsonException)
        {
            return null;
        }
    }

    public static string FormatLine(Envelope e) => Format.Format(e);

    /// <summary>Reloads a stored session (at start): its lines are already on disk; new ones are appended.</summary>
    public IReadOnlyList<Envelope> Restore(string sessionId)
    {
        var envelopes = new List<Envelope>();
        foreach (string line in _store.ReadEvents(sessionId))
        {
            if (ParseLine(line) is { } e)
            {
                envelopes.Add(e);
                Note(e);
            }
        }

        lock (_lock)
        {
            _recorded.Add(sessionId);
        }

        return envelopes;
    }

    /// <summary>True once the candidate's clip was saved or kept.</summary>
    public bool IsKept(string sessionId, string candidateId)
    {
        lock (_lock)
        {
            return _kept.Contains((sessionId, candidateId));
        }
    }

    /// <summary>The saved-clip record for a clip file, when the history has one.</summary>
    public ClipSaved? SavedClip(string path, out string? sessionId)
    {
        lock (_lock)
        {
            if (_savedByPath.TryGetValue(Path.GetFullPath(path), out ClipSaved? s))
            {
                sessionId = _sessionOfSaved.GetValueOrDefault(Path.GetFullPath(path));
                return s;
            }
        }

        sessionId = null;
        return null;
    }

    private readonly Dictionary<string, string> _sessionOfSaved = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Applies one envelope; the pump calls this, and tests can too.</summary>
    internal void Observe(Envelope e)
    {
        if (e.Topic is not (Topics.Session or Topics.ClipCandidate or Topics.QaReport or Topics.ClipRating or Topics.ClipSaved))
        {
            return;
        }

        string id = e.SessionId;
        string line;
        lock (_lock)
        {
            if (e.PayloadCase == Envelope.PayloadOneofCase.SessionStarted)
            {
                SessionStarted s = e.SessionStarted;
                if (s.SourceKind != SourceKind.LiveGame || !RetentionRules.CanPersist(s))
                {
                    return;
                }

                _recorded.Add(id);
                _pending[id] = [];
            }
            else if (!_recorded.Contains(id))
            {
                return;
            }

            line = FormatLine(e);
            if (_pending.TryGetValue(id, out List<string>? waiting))
            {
                // The session's folder appears a moment after it starts: hold its lines until then.
                if (waiting.Count < MaxPendingLines)
                {
                    waiting.Add(line);
                }

                if (!_store.AppendEvent(id, waiting[0]))
                {
                    return;
                }

                foreach (string held in waiting.Skip(1))
                {
                    _store.AppendEvent(id, held);
                }

                _pending.Remove(id);
            }
            else
            {
                _store.AppendEvent(id, line);
            }
        }

        Note(e);
        if (e.PayloadCase == Envelope.PayloadOneofCase.ClipCandidate)
        {
            CandidateRecorded?.Invoke(id, e.ClipCandidate);
        }
    }

    private void Note(Envelope e)
    {
        if (e.PayloadCase != Envelope.PayloadOneofCase.ClipSaved)
        {
            return;
        }

        lock (_lock)
        {
            _kept.Add((e.SessionId, e.ClipSaved.CandidateId));
            if (e.ClipSaved.Path.Length > 0)
            {
                string full = Path.GetFullPath(e.ClipSaved.Path);
                _savedByPath[full] = e.ClipSaved;
                _sessionOfSaved[full] = e.SessionId;
            }
        }
    }

    private async Task PumpAsync()
    {
        try
        {
            await foreach (Envelope e in _subscription.ReadAllAsync(_stop.Token).ConfigureAwait(false))
            {
                try
                {
                    Observe(e);
                }
                catch (IOException)
                {
                    // the disk is full or the folder went away: the live session carries on
                }
                catch (UnauthorizedAccessException)
                {
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
}
