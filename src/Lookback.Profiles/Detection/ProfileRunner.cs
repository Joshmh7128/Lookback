using Lookback.Contracts;
using Lookback.Contracts.V1;
using Lookback.Core.Bus;
using Lookback.Core.Time;
using Lookback.Profiles.Model;

namespace Lookback.Profiles.Detection;

/// <summary>
/// Runs a profile live on the bus: subscribes to <c>signals.video</c> and
/// <c>signals.audio</c>, merges them into time order, runs the detector, and
/// publishes <c>game.event</c> envelopes plus a <c>signals.rules</c> frame of
/// condition margins per video frame. The same component serves live capture
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
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _pump;
    private readonly Lock _gate = new();
    private bool _rulesSchemaSent;
    private long _eventCount;

    public ProfileRunner(IEventBus bus, GameProfile profile, string sessionId, long frequency, int capacity = 65_536)
    {
        ArgumentNullException.ThrowIfNull(bus);
        ArgumentNullException.ThrowIfNull(profile);
        _bus = bus;
        Profile = profile;
        _detector = new ProfileEventDetector(profile, frequency);
        _merger = new TimeOrderedMerger<Envelope>(TimeSpan.FromSeconds(2)); // wall time: output depends on QPC only
        _merger.Register(1);
        _merger.Register(2);
        _events = new EnvelopeFactory(new ManualClock(frequency), sessionId, $"profile.{profile.Id}");
        _rules = new EnvelopeFactory(new ManualClock(frequency), sessionId, $"profile.{profile.Id}.rules");
        SessionId = sessionId;
        _video = bus.Subscribe(Topics.SignalsVideo, capacity);
        _audio = bus.Subscribe(Topics.SignalsAudio, capacity);
        _pump = Task.WhenAll(PumpAsync(_video, 1, _cts.Token), PumpAsync(_audio, 2, _cts.Token));
    }

    public GameProfile Profile { get; }

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
        foreach (Envelope e in frames)
        {
            if (_detector.OnFrame(e.SignalFrame, found))
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

    private void Publish(List<GameEvent> found)
    {
        foreach (GameEvent g in found)
        {
            Envelope env = _events.Create(Topics.GameEvent, (long)g.AtQpc);
            env.GameEvent = g;
            _bus.Publish(env);
            Interlocked.Increment(ref _eventCount);
        }

        found.Clear();
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
        }

        _video.Dispose();
        _audio.Dispose();
        _cts.Dispose();
    }
}
