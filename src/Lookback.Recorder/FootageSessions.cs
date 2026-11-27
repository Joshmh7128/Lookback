using Lookback.Clipper;
using Lookback.Contracts;
using Lookback.Contracts.V1;
using Lookback.Core.Bus;
using Lookback.Core.Time;
using Lookback.Media.Ffmpeg;
using Lookback.Media.Frames;
using Lookback.Profiles.Detection;
using Lookback.Profiles.Model;
using Lookback.Profiles.Regions;
using Lookback.Signals;
using Lookback.Signals.Video;
using Microsoft.Extensions.Logging;

namespace Lookback.Recorder;

/// <summary>A footage file to analyse for the shell's "Open footage…".</summary>
internal sealed record FootageRequest(string Path, string? Profile, Retention Retention, string PermissionRef);

/// <summary>
/// Runs the <c>analyze</c> command: a footage file through FootageAnalyzer,
/// the game profile (when one is chosen) and the clipper, as fast as the
/// machine allows, on a private bus. Only what the shell needs crosses to the
/// recorder's bus: the session header and end, game events, profile
/// detections, clip candidates and QA reports. Signal frames stay private,
/// so the shell's socket is not flooded. Nothing is written to disk here;
/// retention is validated before anything starts. At most two run at once,
/// so a batch of files leaves room for live capture.
/// </summary>
internal sealed partial class FootageSessions(IEventBus bus, ILogger logger) : IAsyncDisposable
{
    public const long Frequency = 10_000_000;
    private static readonly string[] Forwarded = [Topics.Session, Topics.GameEvent, Topics.GameProfile, Topics.ClipCandidate, Topics.QaReport];

    private readonly SemaphoreSlim _slots = new(2, 2);
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _lock = new();
    private readonly List<Task> _running = [];
    private int _counter;

    /// <summary>
    /// Checks <paramref name="request"/> and starts it in the background.
    /// Returns the session id, or a refusal (error code and message) without starting anything.
    /// </summary>
    public (string? SessionId, string? Error, string? Message) Start(FootageRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!System.IO.Path.IsPathFullyQualified(request.Path) || !File.Exists(request.Path))
        {
            return (null, "bad-request", $"No file at '{request.Path}' (send an absolute path).");
        }

        ProfileCatalog catalog = ProfileCatalog.BuiltIn;
        if (request.Profile is { Length: > 0 } id && !catalog.Contains(id))
        {
            return (null, "bad-request", $"No profile '{id}'.");
        }

        var started = new SessionStarted
        {
            SourceKind = SourceKind.File,
            Retention = request.Retention,
            PermissionRef = request.PermissionRef,
            Title = request.Path, // the shell links previews to the file by this exact path
            QpcFrequency = Frequency,
            GameProfile = request.Profile ?? "",
        };
        string? problem = RetentionRules.Validate(started);
        if (problem is not null)
        {
            return (null, request.Retention == Retention.Ephemeral ? "ephemeral" : "bad-request", problem);
        }

        string sessionId = $"footage-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Interlocked.Increment(ref _counter)}";
        Task run = Task.Run(() => RunAsync(request, catalog, started, sessionId, _stop.Token));
        lock (_lock)
        {
            _running.RemoveAll(t => t.IsCompleted);
            _running.Add(run);
        }

        return (sessionId, null, null);
    }

    private async Task RunAsync(FootageRequest request, ProfileCatalog catalog, SessionStarted started, string sessionId, CancellationToken ct)
    {
        await _slots.WaitAsync(ct).ConfigureAwait(false);
        var factory = new EnvelopeFactory(new ManualClock(Frequency), sessionId, "recorder.footage");
        using var privateBus = new InProcessEventBus();
        using IEventSubscription forward = privateBus.Subscribe("*", capacity: 1 << 18);
        Task pump = Task.Run(() => ForwardAsync(forward, ct), CancellationToken.None);
        ProfileRunner? profile = null;
        ClipperRunner? clipper = null;
        string reason = "end of file";
        long endQpc = 0;
        try
        {
            Envelope header = factory.Create(Topics.Session, 0);
            header.SessionStarted = started;
            privateBus.Publish(header);

            MediaInfo media = await MediaProbe.ProbeAsync(request.Path, ct).ConfigureAwait(false);
            if (!media.HasVideo)
            {
                throw new InvalidDataException("The file has no video stream.");
            }

            endQpc = QpcTime.FromTimeSpan(media.Duration, Frequency);
            string? startId = request.Profile is { Length: > 0 } p ? p : null;
            AnalysisFormat format = AnalysisFormat.Default;
            IReadOnlyList<HudRegion> regions = startId is null ? [] : catalog.SubtreeRegions(catalog.PathTo(startId)[0].Id);
            IVideoSignal[] extra = regions.Count > 0 ? [new RegionSignal(regions, format.Width, format.Height)] : [];

            // The clipper publishes the one candidate set: the profile's events fused with the excitement curve.
            profile = startId is null ? null : new ProfileRunner(privateBus, catalog, startId, DetectionMethod.Manual, sessionId, Frequency, publishCandidates: false);
            clipper = startId is null ? new ClipperRunner(privateBus, sessionId, Frequency) : new ClipperRunner(privateBus, catalog, startId, sessionId, Frequency);
            ProfileRunner? p0 = profile;
            ClipperRunner c0 = clipper;
            var pacer = new ReplayPacer(() => Math.Max(p0?.Queued ?? 0, c0.Queued));
            var analyzer = new FootageAnalyzer(privateBus, format, Frequency);
            FootageAnalysisResult result = await analyzer.AnalyzeAsync(
                request.Path,
                sessionId,
                new FootageAnalysisOptions { HasAudio = media.HasAudio, Pacer = pacer, ExtraVideoSignals = extra },
                ct).ConfigureAwait(false);

            // The profile flushes its last events first; the clipper waits on them.
            if (profile is not null)
            {
                await profile.DisposeAsync().ConfigureAwait(false);
                profile = null;
            }

            await clipper.DisposeAsync().ConfigureAwait(false);
            LogFinished(logger, request.Path, sessionId, result.VideoFrames, c0.CandidatesPublished, result.Elapsed.TotalSeconds);
            clipper = null;
        }
        catch (OperationCanceledException)
        {
            reason = "recorder stopping";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            reason = $"failed: {ex.Message}";
            LogFailed(logger, request.Path, sessionId, ex.Message);
        }
        finally
        {
            if (profile is not null)
            {
                await profile.DisposeAsync().ConfigureAwait(false);
            }

            if (clipper is not null)
            {
                await clipper.DisposeAsync().ConfigureAwait(false);
            }

            Envelope end = factory.Create(Topics.Session, endQpc);
            end.SessionEnded = new SessionEnded { Reason = reason };
            privateBus.Publish(end);
            forward.Dispose(); // drains what is queued, then ends the forwarder
            await pump.ConfigureAwait(false);
            _slots.Release();
        }
    }

    private async Task ForwardAsync(IEventSubscription forward, CancellationToken ct)
    {
        try
        {
            await foreach (Envelope e in forward.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
            {
                if (Array.IndexOf(Forwarded, e.Topic) >= 0)
                {
                    bus.Publish(e);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        Task[] running;
        lock (_lock)
        {
            running = [.. _running];
        }

        try
        {
            await Task.WhenAll(running).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
        }
        catch (OperationCanceledException)
        {
        }

        _stop.Dispose();
        _slots.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Analysed {Path} as {Session}: {Frames} frames, {Candidates} candidates in {Seconds:F1} s")]
    private static partial void LogFinished(ILogger logger, string path, string session, long frames, long candidates, double seconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Analysis of {Path} ({Session}) failed: {Reason}")]
    private static partial void LogFailed(ILogger logger, string path, string session, string reason);
}
