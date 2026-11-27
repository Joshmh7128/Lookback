using Lookback.Contracts;
using Lookback.Contracts.V1;
using Lookback.Core.Bus;
using Lookback.Core.Time;
using Lookback.Profiles.Detection;
using Lookback.Profiles.Model;

namespace Lookback.Clipper;

/// <summary>
/// Runs the clipper on the bus for one session, the same shape as
/// <see cref="ProfileRunner"/>: it subscribes to <c>signals.video</c> and
/// <c>signals.audio</c>, merges them into time order, and publishes
/// <c>clip.candidate</c> followed by that candidate's <c>qa.report</c>, both
/// stamped with the candidate's peak (raw QPC), plus the excitement curve on
/// <c>signals.clipper</c> for viewers.
/// <para>
/// Given a profile tree it also follows the <see cref="ProfileRunner"/> on the
/// same bus: it reads <c>game.event</c> and <c>game.profile</c>, and holds its
/// own frames back until the profile runner's <c>signals.rules</c> frames have
/// passed them. The profile runner publishes a rules frame after every event
/// and profile change it found before that frame, so once a rules frame has
/// arrived, everything earlier is in. Run that profile runner with
/// <c>publishCandidates: false</c>: this runner publishes the one candidate set.
/// At the end of a session dispose the profile runner first, then this one.
/// </para>
/// </summary>
public sealed class ClipperRunner : IAsyncDisposable
{
    /// <summary>Schema id of the curve on <c>signals.clipper</c> (video 1, audio 2, rules 3).</summary>
    public const uint CurveSchemaId = 4;

    private const int VideoStream = 1;
    private const int AudioStream = 2;
    private const int RulesStream = 3;

    private readonly IEventBus _bus;
    private readonly ClipperEngine _engine;
    private readonly IEventSubscription _video;
    private readonly IEventSubscription _audio;
    private readonly IEventSubscription? _rules;
    private readonly IEventSubscription? _events;
    private readonly IEventSubscription? _profiles;
    private readonly TimeOrderedMerger<Envelope> _merger;
    private readonly EnvelopeFactory _out;
    private readonly EnvelopeFactory _curveOut;
    private readonly List<Envelope> _released = [];
    private readonly List<ClipperOutput> _outputs = [];
    private readonly long _curveInterval;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _pump;
    private readonly Lock _gate = new();
    private bool _curveSchemaSent;
    private long _nextCurve;
    private long _published;

    /// <summary>The curve alone, with the default weights (no game profile).</summary>
    public ClipperRunner(IEventBus bus, string sessionId, long frequency, ClipperOptions? options = null, int capacity = 65_536)
        : this(bus, new ClipperEngine(sessionId, frequency, options), sessionId, frequency, options, capacity)
    {
    }

    /// <summary>
    /// Follows a profile runner over the same tree: weights from the active
    /// profile, and the tree's events fused with the curve.
    /// </summary>
    public ClipperRunner(IEventBus bus, ProfileCatalog catalog, string startId, string sessionId, long frequency, ClipperOptions? options = null, int capacity = 65_536)
        : this(bus, new ClipperEngine(catalog, startId, sessionId, frequency, options), sessionId, frequency, options, capacity)
    {
    }

    private ClipperRunner(IEventBus bus, ClipperEngine engine, string sessionId, long frequency, ClipperOptions? options, int capacity)
    {
        ArgumentNullException.ThrowIfNull(bus);
        _bus = bus;
        _engine = engine;
        SessionId = sessionId;
        _curveInterval = QpcTime.Rescale((options ?? new ClipperOptions()).CurvePublishIntervalMs, 1000, frequency);

        // Longer than the profile runner's own 2 s, so when a stream is missing (no audio)
        // the profile runner gives up on it first and its rules frames keep gating this one.
        _merger = new TimeOrderedMerger<Envelope>(TimeSpan.FromSeconds(5));
        _merger.Register(VideoStream);
        _merger.Register(AudioStream);
        _out = new EnvelopeFactory(new ManualClock(frequency), sessionId, "clipper");
        _curveOut = new EnvelopeFactory(new ManualClock(frequency), sessionId, "clipper.curve");
        _video = bus.Subscribe(Topics.SignalsVideo, capacity);
        _audio = bus.Subscribe(Topics.SignalsAudio, capacity);
        var pumps = new List<Task> { PumpAsync(_video, _cts.Token), PumpAsync(_audio, _cts.Token) };
        if (engine.FollowsProfile)
        {
            _merger.Register(RulesStream);
            _rules = bus.Subscribe(Topics.SignalsRules, capacity);
            _events = bus.Subscribe(Topics.GameEvent, capacity);
            _profiles = bus.Subscribe(Topics.GameProfile, capacity);
            pumps.Add(PumpAsync(_rules, _cts.Token));
            pumps.Add(PumpAsync(_events, _cts.Token));
            pumps.Add(PumpAsync(_profiles, _cts.Token));
        }

        _pump = Task.WhenAll(pumps);
    }

    public string SessionId { get; }

    /// <summary>The signals the curve combines.</summary>
    public IReadOnlyList<string> Components => _engine.Components;

    /// <summary>Events waiting in this runner's queues; replays pace on it.</summary>
    public int Queued => _video.Count + _audio.Count + (_rules?.Count ?? 0) + (_events?.Count ?? 0) + (_profiles?.Count ?? 0);

    public long Dropped => _video.Dropped + _audio.Dropped + (_rules?.Dropped ?? 0) + (_events?.Dropped ?? 0) + (_profiles?.Dropped ?? 0);

    /// <summary>
    /// Raised for every finished candidate, right after its <c>clip.candidate</c>
    /// and <c>qa.report</c> are published, with the candidate's in-memory
    /// <see cref="CandidateFeatures"/> (for the learned scorer, M4). Raised on
    /// the runner's pump thread under its lock: hand the work off, do not block.
    /// </summary>
    public event EventHandler<CandidateFinishedEventArgs>? CandidateFinished;

    /// <summary>Names of every candidate's features, in order.</summary>
    public IReadOnlyList<string> FeatureNames => _engine.FeatureNames;

    /// <summary>Candidates published so far (each followed by its QA report).</summary>
    public long CandidatesPublished => Interlocked.Read(ref _published);

    private async Task PumpAsync(IEventSubscription sub, CancellationToken ct)
    {
        try
        {
            while (await sub.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                Drain();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// Reads every queue under the lock. Order matters: rules frames first, then
    /// events and profile changes, so that everything published before a rules
    /// frame is in before that frame lets the clipper move on.
    /// </summary>
    private void Drain()
    {
        lock (_gate)
        {
            if (_rules is not null)
            {
                while (_rules.TryRead(out Envelope? e))
                {
                    if (e!.SessionId == SessionId && e.PayloadCase == Envelope.PayloadOneofCase.SignalFrame)
                    {
                        _merger.Add(RulesStream, (long)e.SignalFrame.WindowStartQpc, e);
                    }
                }

                while (_profiles!.TryRead(out Envelope? e))
                {
                    if (e!.SessionId == SessionId && e.PayloadCase == Envelope.PayloadOneofCase.ProfileDetected)
                    {
                        _engine.OnProfile(e.ProfileDetected);
                    }
                }

                while (_events!.TryRead(out Envelope? e))
                {
                    if (e!.SessionId == SessionId && e.PayloadCase == Envelope.PayloadOneofCase.GameEvent)
                    {
                        _engine.OnEvent(e.GameEvent);
                    }
                }
            }

            Read(_video, VideoStream);
            Read(_audio, AudioStream);
            _released.Clear();
            _merger.Release(_released);
            Process();
        }
    }

    private void Read(IEventSubscription sub, int stream)
    {
        while (sub.TryRead(out Envelope? e))
        {
            if (e!.SessionId != SessionId)
            {
                continue;
            }

            if (e.PayloadCase == Envelope.PayloadOneofCase.SignalSchema)
            {
                _engine.OnSchema(e.SignalSchema);
            }
            else if (e.PayloadCase == Envelope.PayloadOneofCase.SignalFrame)
            {
                _merger.Add(stream, (long)e.SignalFrame.WindowStartQpc, e);
            }
        }
    }

    private void Process()
    {
        foreach (Envelope e in _released)
        {
            if (e.Topic == Topics.SignalsRules)
            {
                continue; // a rules frame only gates the merge
            }

            if (_engine.OnFrame(e.SignalFrame, _outputs))
            {
                MaybePublishCurve((long)e.SignalFrame.WindowStartQpc);
            }

            Publish();
        }

        _released.Clear();
    }

    private void MaybePublishCurve(long t)
    {
        // A quarter-interval of slack, so 30 fps frames (three of them a hair under 100 ms) give 10 Hz.
        if (_curveInterval <= 0 || (_curveSchemaSent && t < _nextCurve - (_curveInterval / 4)))
        {
            return;
        }

        if (!_curveSchemaSent)
        {
            Envelope s = _curveOut.Create(Topics.SignalsClipper, t);
            s.SignalSchema = new SignalSchema { SchemaId = CurveSchemaId, Family = SignalFamily.Clipper, Names = { _engine.CurveNames } };
            _bus.Publish(s);
            _curveSchemaSent = true;
        }

        // One message per interval, not per frame: the curve is for viewers. Each frame
        // covers the interval it stands for, [t, t + interval).
        var frame = new SignalFrame { SchemaId = CurveSchemaId, WindowStartQpc = (ulong)t, WindowEndQpc = (ulong)(t + _curveInterval) };
        foreach (float v in _engine.CurveValues)
        {
            frame.Values.Add(v);
        }

        Envelope env = _curveOut.Create(Topics.SignalsClipper, t);
        env.SignalFrame = frame;
        _bus.Publish(env);
        _nextCurve = t + _curveInterval;
    }

    private void Publish()
    {
        foreach (ClipperOutput output in _outputs)
        {
            (ClipCandidate candidate, QaReport qa, _) = output;
            Envelope c = _out.Create(Topics.ClipCandidate, (long)candidate.PeakQpc);
            c.ClipCandidate = candidate;
            _bus.Publish(c);
            Envelope q = _out.Create(Topics.QaReport, (long)candidate.PeakQpc);
            q.QaReport = qa;
            _bus.Publish(q);
            Interlocked.Increment(ref _published);
            CandidateFinished?.Invoke(this, new CandidateFinishedEventArgs(output));
        }

        _outputs.Clear();
    }

    /// <summary>Processes everything queued, flushes the merger and the engine, then stops.</summary>
    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        await _pump.ConfigureAwait(false);
        Drain();
        lock (_gate)
        {
            _released.Clear();
            _merger.Flush(_released);
            Process();
            _engine.Flush(_outputs);
            Publish();
        }

        _video.Dispose();
        _audio.Dispose();
        _rules?.Dispose();
        _events?.Dispose();
        _profiles?.Dispose();
        _cts.Dispose();
    }
}
