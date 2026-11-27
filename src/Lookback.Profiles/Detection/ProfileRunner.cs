using Lookback.Contracts;
using Lookback.Contracts.V1;
using Lookback.Core.Bus;
using Lookback.Core.Time;
using Lookback.Profiles.Candidates;
using Lookback.Profiles.Model;

namespace Lookback.Profiles.Detection;

/// <summary>
/// Runs a profile live on the bus: subscribes to <c>signals.video</c> and
/// <c>signals.audio</c>, merges them into time order, runs the detector, and
/// publishes <c>game.event</c> envelopes plus a <c>signals.rules</c> frame of
/// condition margins per video frame. Given a catalog it runs a whole game
/// tree and publishes <c>game.profile</c> whenever the active sub-game or mode
/// changes. The same component serves live capture
/// and footage replays.
/// </summary>
public sealed class ProfileRunner : IAsyncDisposable
{
    public const uint RulesSchemaId = 3;

    private readonly IEventBus _bus;
    private readonly IEventSubscription _video;
    private readonly IEventSubscription _audio;
    private readonly ProfileEventDetector _detector;
    private readonly TimeOrderedMerger<Envelope> _merger;
    private readonly EnvelopeFactory _events;
    private readonly EnvelopeFactory _rules;
    private readonly EnvelopeFactory _candidates;
    private readonly CandidateBuilder _builder;
    private readonly List<ClipCandidate> _closed = [];
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _pump;
    private readonly Lock _gate = new();
    private readonly bool _publishCandidates;
    private bool _rulesSchemaSent;
    private long _eventCount;

    /// <summary>
    /// Runs one profile. With <c>publishCandidates</c> it also publishes
    /// <c>clip.candidate</c> built from the events alone; pass false when a
    /// clipper (<c>Lookback.Clipper.ClipperRunner</c>) runs on the same bus,
    /// which fuses these events with the excitement curve and publishes the
    /// one candidate set.
    /// </summary>
    public ProfileRunner(IEventBus bus, GameProfile profile, string sessionId, long frequency, int capacity = 65_536, bool publishCandidates = true)
        : this(bus, new ProfileCatalog([profile ?? throw new ArgumentNullException(nameof(profile))]), profile.Id, DetectionMethod.Manual, sessionId, frequency, capacity, publishCandidates)
    {
    }

    /// <summary>Runs a whole game tree from <c>startId</c>; <c>publishCandidates</c> as in the other constructor.</summary>
    public ProfileRunner(IEventBus bus, ProfileCatalog catalog, string startId, DetectionMethod method, string sessionId, long frequency, int capacity = 65_536, bool publishCandidates = true)
    {
        _publishCandidates = publishCandidates;
        ArgumentNullException.ThrowIfNull(bus);
        ArgumentNullException.ThrowIfNull(catalog);
        _bus = bus;
        GameProfile profile = catalog.PathTo(startId)[0];
        Profile = profile;
        _detector = new ProfileEventDetector(catalog, startId, frequency, method);
        _merger = new TimeOrderedMerger<Envelope>(TimeSpan.FromSeconds(2)); // wall time: output depends on QPC only
        _merger.Register(1);
        _merger.Register(2);
        _events = new EnvelopeFactory(new ManualClock(frequency), sessionId, $"profile.{profile.Id}");
        _rules = new EnvelopeFactory(new ManualClock(frequency), sessionId, $"profile.{profile.Id}.rules");
        _candidates = new EnvelopeFactory(new ManualClock(frequency), sessionId, $"profile.{profile.Id}.candidates");
        _builder = new CandidateBuilder(sessionId, frequency);
        SessionId = sessionId;
        _video = bus.Subscribe(Topics.SignalsVideo, capacity);
        _audio = bus.Subscribe(Topics.SignalsAudio, capacity);
        _pump = Task.WhenAll(PumpAsync(_video, 1, _cts.Token), PumpAsync(_audio, 2, _cts.Token));
    }

    /// <summary>The game at the root of the tree being run.</summary>
    public GameProfile Profile { get; }

    /// <summary>The profile currently detected.</summary>
    public string ActiveProfileId
    {
        get
        {
            lock (_gate)
            {
                return _detector.ActiveProfileId;
            }
        }
    }

    /// <summary>Live detection facts; a hint match is published at once.</summary>
    public void ApplyHints(DetectionContext context, long at)
    {
        lock (_gate)
        {
            ProfileDetected? d = _detector.ApplyHints(context, at);
            if (d is not null)
            {
                PublishDetection(d);
            }
        }
    }

    public string SessionId { get; }

    /// <summary>Events waiting in this runner's queues; replays pace on it.</summary>
    public int Queued => _video.Count + _audio.Count;

    public long Dropped => _video.Dropped + _audio.Dropped;

    public long EventsPublished => Interlocked.Read(ref _eventCount);

    private async Task PumpAsync(IEventSubscription sub, int stream, CancellationToken ct)
    {
        var released = new List<Envelope>();
        try
        {
            while (await sub.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                Drain(sub, stream, released);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void Drain(IEventSubscription sub, int stream, List<Envelope> released)
    {
        lock (_gate)
        {
            while (sub.TryRead(out Envelope? e))
            {
                if (e!.SessionId != SessionId)
                {
                    continue;
                }

                if (e.PayloadCase == Envelope.PayloadOneofCase.SignalSchema)
                {
                    _detector.OnSchema(e.SignalSchema);
                }
                else if (e.PayloadCase == Envelope.PayloadOneofCase.SignalFrame)
                {
                    _merger.Add(stream, (long)e.SignalFrame.WindowStartQpc, e);
                }
            }

            released.Clear();
            _merger.Release(released);
            Process(released);
        }
    }

    private void Process(List<Envelope> frames)
    {
        var found = new List<GameEvent>();
        var detections = new List<ProfileDetected>();
        foreach (Envelope e in frames)
        {
            bool video = _detector.OnFrame(e.SignalFrame, found, detections);
            foreach (ProfileDetected d in detections)
            {
                PublishDetection(d);
            }

            detections.Clear();
            if (video)
            {
                PublishMargins((long)e.SignalFrame.WindowStartQpc, (long)e.SignalFrame.WindowEndQpc);
            }

            Publish(found);
        }
    }

    private void PublishMargins(long start, long end)
    {
        if (_detector.MarginNames.Count == 0)
        {
            return;
        }

        if (!_rulesSchemaSent)
        {
            Envelope s = _rules.Create(Topics.SignalsRules, 0);
            s.SignalSchema = new SignalSchema { SchemaId = RulesSchemaId, Family = SignalFamily.Rules, Names = { _detector.MarginNames } };
            _bus.Publish(s);
            _rulesSchemaSent = true;
        }

        var frame = new SignalFrame { SchemaId = RulesSchemaId, WindowStartQpc = (ulong)start, WindowEndQpc = (ulong)end };
        foreach (float m in _detector.Margins)
        {
            frame.Values.Add(float.IsNegativeInfinity(m) ? -1e9f : m); // proto3 JSON cannot carry -Infinity everywhere
        }

        Envelope env = _rules.Create(Topics.SignalsRules, start);
        env.SignalFrame = frame;
        _bus.Publish(env);
    }

    private void PublishDetection(ProfileDetected d)
    {
        Envelope env = _events.Create(Topics.GameProfile, (long)d.AtQpc);
        env.ProfileDetected = d;
        _bus.Publish(env);
    }

    private void Publish(List<GameEvent> found)
    {
        foreach (GameEvent g in found)
        {
            Envelope env = _events.Create(Topics.GameEvent, (long)g.AtQpc);
            env.GameEvent = g;
            _bus.Publish(env);
            Interlocked.Increment(ref _eventCount);
            if (_publishCandidates)
            {
                _builder.Add(g, _closed);
            }
        }

        found.Clear();
        PublishCandidates();
    }

    private void PublishCandidates()
    {
        foreach (ClipCandidate c in _closed)
        {
            Envelope env = _candidates.Create(Topics.ClipCandidate, (long)c.PeakQpc);
            env.ClipCandidate = c;
            _bus.Publish(env);
        }

        _closed.Clear();
    }

    /// <summary>Processes everything queued, flushes the merger and detector, then stops.</summary>
    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        await _pump.ConfigureAwait(false);

        var released = new List<Envelope>();
        Drain(_video, 1, released);
        Drain(_audio, 2, released);
        lock (_gate)
        {
            released.Clear();
            _merger.Flush(released);
            Process(released);
            var found = new List<GameEvent>();
            _detector.Flush(found);
            Publish(found);
            _builder.Flush(_closed);
            PublishCandidates();
        }

        _video.Dispose();
        _audio.Dispose();
        _cts.Dispose();
    }
}
