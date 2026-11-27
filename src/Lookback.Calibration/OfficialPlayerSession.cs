using Lookback.Calibration.Check;
using Lookback.Calibration.Probe;
using Lookback.Calibration.Profiles;
using Lookback.Clipper;
using Lookback.Contracts;
using Lookback.Contracts.V1;
using Lookback.Core.Bus;
using Lookback.Core.Time;
using Lookback.Media.Frames;
using Lookback.Profiles.Detection;
using Lookback.Profiles.Model;
using Lookback.Profiles.Regions;
using Lookback.Signals;
using Lookback.Signals.Video;
using Lookback.Stream.SessionLog;

namespace Lookback.Calibration;

/// <summary>
/// One official-player session: frames of a YouTube or Twitch embed, sent
/// by the viewer window, analysed live. The session is
/// <see cref="SourceKind.OfficialPlayer"/> and <see cref="Retention.Ephemeral"/>:
/// there is no encoder, no replay ring and no session log, the frames are
/// borrowed buffers overwritten by the next one, and every derived value
/// lives in memory until the session ends. The only thing that can leave it
/// is <see cref="CalibrationEdits"/> the user confirms, through
/// <see cref="CalibrationWriter"/>.
/// <para>
/// The clipper runs on the player's frames too (video only: the viewer sends
/// no audio), and its candidates feed <see cref="Check"/>, where the user can
/// judge them to see how accurate the clipper is. The check is memory only and
/// is cleared when the player stops; the host may save the user's ratings,
/// numbers only, through <c>RatingStore.AppendCheck</c>.
/// </para>
/// </summary>
public sealed class OfficialPlayerSession : IAsyncDisposable
{
    private readonly IEventBus _bus;
    private readonly IClock _clock;
    private readonly AnalysisFormat _format = AnalysisFormat.Default;
    private readonly VideoFrame _frame;
    private readonly CalibrationProbe _probe;
    private readonly Lock _gate = new();
    private readonly string _baseId;
    private readonly ProfileCatalog _catalog;
    private Pipeline? _pipeline;
    private long _originQpc;
    private int _generation;

    private sealed record Pipeline(
        string SessionId, SessionStarted Started, EnvelopeFactory Factory, VideoSignalBank Bank, SignalPublisher Publisher,
        ProfileRunner? Runner, ClipperRunner Clipper, ThresholdTuner? Tuner, GameProfile? Resolved);

    public OfficialPlayerSession(IEventBus bus, ProfileCatalog catalog, string platform, IClock? clock = null)
    {
        ArgumentNullException.ThrowIfNull(bus);
        ArgumentNullException.ThrowIfNull(catalog);
        _bus = bus;
        _catalog = catalog;
        _clock = clock ?? SystemClock.Instance;
        Platform = platform is "youtube" or "twitch" ? platform : throw new ArgumentException("Platform must be youtube or twitch.", nameof(platform));
        _frame = new VideoFrame(_format.Width, _format.Height);
        _probe = new CalibrationProbe(_format, _clock.Frequency);
        _baseId = $"official-{platform}-{_clock.Now:x}";
        Check = new ClipperCheck(_clock.Frequency);
    }

    /// <summary>The clipper check for this player: candidates, judgments and missed-highlight marks, in memory only.</summary>
    public ClipperCheck Check { get; }

    /// <summary>The playback clock's origin (QPC); playback time is <c>(qpc - OriginQpc) / frequency</c>.</summary>
    public long OriginQpc => _originQpc;

    public string Platform { get; }

    public CalibrationProbe Probe => _probe;

    /// <summary>Edits tried live but not saved yet.</summary>
    public CalibrationEdits Applied { get; private set; } = new();

    public string? ProfileId { get; private set; }

    public long VideoFrames { get; private set; }

    public SessionStarted? Started => _pipeline?.Started;

    public string? SessionId => _pipeline?.SessionId;

    /// <summary>The active profile with everything inherited and the live edits applied.</summary>
    public GameProfile? Resolved => _pipeline?.Resolved;

    /// <summary>The catalog the session runs on, with the live edits applied.</summary>
    public ProfileCatalog Catalog => Applied.ApplyTo(_catalog);

    public ThresholdTuner? Tuner => _pipeline?.Tuner;

    /// <summary>Starts (or restarts, after a change of profile or edits) the analysis.</summary>
    public async Task StartAsync(string? profileId)
    {
        Pipeline? old;
        lock (_gate)
        {
            old = _pipeline;
            _pipeline = null;
        }

        await StopPipelineAsync(old, "restarted").ConfigureAwait(false);
        if (profileId is not null && !_catalog.Contains(profileId))
        {
            throw new KeyNotFoundException($"No profile '{profileId}'.");
        }

        if (profileId != ProfileId)
        {
            _probe.ClearMarks();
        }

        ProfileId = profileId;
        ProfileCatalog catalog = Catalog;
        string sessionId = $"{_baseId}-{++_generation}";
        var started = new SessionStarted
        {
            SourceKind = SourceKind.OfficialPlayer,
            Retention = Retention.Ephemeral,
            Title = Platform == "youtube" ? "YouTube embedded player" : "Twitch embedded player",
            QpcFrequency = (ulong)_clock.Frequency,
            GameProfile = profileId ?? "",
        };
        string? problem = RetentionRules.Validate(started);
        if (problem is not null || RetentionRules.CanPersist(started))
        {
            throw new RetentionViolationException(problem ?? "An official-player session must not be persistable.");
        }

        GameProfile? resolved = profileId is null ? null : catalog.Resolve(profileId);
        string? root = profileId is null ? null : catalog.PathTo(profileId)[0].Id;
        IVideoSignal[] extra = root is null
            ? [_probe]
            : [new RegionSignal(catalog.SubtreeRegions(root), _format.Width, _format.Height), _probe];
        VideoSignalBank bank = VideoSignalBank.CreateDefault(_format, extra);
        var factory = new EnvelopeFactory(_clock, sessionId, "official-player");
        var publisher = new SignalPublisher(_bus, new EnvelopeFactory(_clock, sessionId, "signals.video"),
            Topics.SignalsVideo, SignalPublisher.VideoSchemaId, SignalFamily.Video, bank.Names);
        // The clipper publishes the one candidate set: the profile's events fused with the excitement curve.
        ProfileRunner? runner = profileId is null ? null : new ProfileRunner(_bus, catalog, profileId, DetectionMethod.Manual, sessionId, _clock.Frequency, publishCandidates: false);
        ClipperRunner clipper = profileId is null
            ? new ClipperRunner(_bus, sessionId, _clock.Frequency)
            : new ClipperRunner(_bus, catalog, profileId, sessionId, _clock.Frequency);
        clipper.CandidateFinished += (_, e) => Check.Add(e.Output.Candidate, e.Output.Qa, e.Output.Features);
        ThresholdTuner? tuner = resolved is null ? null : new ThresholdTuner(Owners(catalog, profileId!, resolved), bank.Names, _clock.Frequency);

        long now = _clock.Now;
        Envelope header = factory.Create(Topics.Session, now);
        header.SessionStarted = started;
        _bus.Publish(header);
        publisher.PublishSchema(now);
        lock (_gate)
        {
            _pipeline = new Pipeline(sessionId, started, factory, bank, publisher, runner, clipper, tuner, resolved);
        }
    }

    /// <summary>The playback clock's origin: microseconds in frames count from here.</summary>
    public void SetOrigin(long qpc) => _originQpc = qpc;

    /// <summary>
    /// One frame of the player, BGRA8, any size (scaled to the analysis
    /// format). <paramref name="pixels"/> is only read during this call.
    /// </summary>
    public void OnVideo(long microseconds, int width, int height, ReadOnlySpan<byte> pixels)
    {
        if (width <= 0 || height <= 0 || pixels.Length < width * height * 4)
        {
            throw new InvalidDataException("A video message's size does not match its pixels.");
        }

        lock (_gate)
        {
            if (_pipeline is not { } p)
            {
                return;
            }

            Scale(pixels, width, height, _frame.Pixels, _format.Width, _format.Height);
            long qpc = _originQpc + QpcTime.Rescale(microseconds, 1_000_000, _clock.Frequency);
            _frame.Qpc = qpc;
            _frame.Index = VideoFrames++;
            ReadOnlySpan<float> values = p.Bank.Process(_frame);
            p.Publisher.Publish(qpc, qpc + QpcTime.Rescale(1, _format.FramesPerSecond, _clock.Frequency), values);
            p.Tuner?.OnFrame(qpc, values);
        }
    }

    /// <summary>The user saw <paramref name="eventName"/> now.</summary>
    public void Mark(string eventName)
    {
        long at = _probe.LastQpc;
        _probe.Mark(eventName, at);
        _pipeline?.Tuner?.Mark(eventName, at);
    }

    /// <summary>The user saw a highlight now; the check counts whether the clipper finds it.</summary>
    public void MarkMissed() => Check.MarkMissed(_probe.LastQpc);

    /// <summary>Tries edits live: the session restarts on the catalog with them applied.</summary>
    public Task ApplyAsync(CalibrationEdits edits)
    {
        ArgumentNullException.ThrowIfNull(edits);
        Applied = edits.Over(Applied);
        return StartAsync(ProfileId);
    }

    public async ValueTask DisposeAsync()
    {
        Pipeline? old;
        lock (_gate)
        {
            old = _pipeline;
            _pipeline = null;
        }

        await StopPipelineAsync(old, "player stopped").ConfigureAwait(false);
        _probe.ClearMarks();
        Check.Reset(); // nothing of the video outlives the player
    }

    private async Task StopPipelineAsync(Pipeline? p, string reason)
    {
        if (p is null)
        {
            return;
        }

        // The profile flushes its last events first; the clipper waits on them.
        if (p.Runner is not null)
        {
            await p.Runner.DisposeAsync().ConfigureAwait(false);
        }

        await p.Clipper.DisposeAsync().ConfigureAwait(false);

        Envelope end = p.Factory.Create(Topics.Session, _clock.Now);
        end.SessionEnded = new SessionEnded { Reason = reason };
        _bus.Publish(end);
    }

    // Each rule of the resolved profile, with the profile in its path that defines it.
    private static IEnumerable<(string, EventRule)> Owners(ProfileCatalog catalog, string id, GameProfile resolved)
    {
        IReadOnlyList<GameProfile> path = catalog.PathTo(id);
        foreach (EventRule e in resolved.Events)
        {
            GameProfile owner = path.Reverse().First(p => p.Events.Any(x => x.Name == e.Name));
            yield return (owner.Id, e);
        }
    }

    // Nearest-neighbour, which is all the 384x216 analysis frame needs.
    private static void Scale(ReadOnlySpan<byte> src, int sw, int sh, Span<byte> dst, int dw, int dh)
    {
        if (sw == dw && sh == dh)
        {
            src[..(dw * dh * 4)].CopyTo(dst);
            return;
        }

        for (int y = 0; y < dh; y++)
        {
            int sy = (int)((long)y * sh / dh);
            for (int x = 0; x < dw; x++)
            {
                int sx = (int)((long)x * sw / dw);
                src.Slice(((sy * sw) + sx) * 4, 4).CopyTo(dst.Slice(((y * dw) + x) * 4, 4));
            }
        }
    }
}
