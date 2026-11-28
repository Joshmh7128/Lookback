using System.Threading.Channels;
using Lookback.Audio;
using Lookback.Capture;
using Lookback.Capture.Targets;
using Lookback.Clipper;
using Lookback.Contracts;
using Lookback.Contracts.V1;
using Lookback.Core.Bus;
using Lookback.Core.Diagnostics;
using Lookback.Core.Time;
using Lookback.Detection;
using Lookback.Detection.Lifecycle;
using Lookback.Detection.Windows;
using Lookback.Encode;
using Lookback.Governor;
using Lookback.Governor.Windows;
using Lookback.Media.Frames;
using Lookback.Profiles.Detection;
using Lookback.Profiles.Model;
using Lookback.Profiles.Regions;
using Lookback.Replay.Packets;
using Lookback.Replay.Store;
using Lookback.Signals.Video;
using Microsoft.Extensions.Logging;

namespace Lookback.Recorder;

/// <summary>Why a <c>save</c> was refused, or the saved clip's path.</summary>
internal readonly record struct SaveResult(string? Path, string? Error, string? Message);

/// <summary>
/// Live capture in the recorder (capture-handoff step 6). Game detection
/// announces a game; this records it: WGC capture of its window on the
/// adapter that drives it, the analysis tap, the CFR encoder on that adapter
/// (fallback chain, stall watchdog, fail-fast), game audio (and the mic if
/// enabled) to AAC, all into a RAM replay ring that a <see cref="SessionRecording"/>
/// cuts into 2 s segments in the store, plus the game's profile and the
/// clipper for events and candidates. One game at a time.
/// <para>
/// Order matters: the governor's machine probe (which creates a DXGI factory)
/// runs before the encoder exists, because creating a DXGI factory while
/// NVENC encodes stalls it. Footage is capped by the governor's disk budget
/// (at most <see cref="RecorderOptions.FootageBudgetGB"/>); saved clips by
/// <see cref="RecorderOptions.ClipsBudgetGB"/>, warned at
/// <see cref="RecorderOptions.ClipsWarnFraction"/> and refused at the cap,
/// never deleted silently. Everything persisted goes through
/// <c>RetentionRules</c> (the store and the clip writer check it).
/// </para>
/// </summary>
internal sealed partial class CaptureService : IAsyncDisposable
{
    private static readonly TimeSpan StatusInterval = TimeSpan.FromSeconds(10);

    private readonly IEventBus _bus;
    private readonly RecorderOptions _options;
    private readonly SessionDirectory _sessions;
    private readonly ILogger _logger;
    private ClipScoring _scoring = null!; // set by Start; shared with footage analysis, owned by the host
    private readonly SegmentStore _store;
    private readonly ProfileSource _profiles;
    private readonly EncoderBlocklist _blocklist = new(EncoderBlocklist.DefaultPath);
    private readonly StallWatchdog _watchdog = new(SystemClock.Instance, TimeSpan.FromMilliseconds(250));
    private readonly Channel<LifecycleChange> _changes = Channel.CreateUnbounded<LifecycleChange>(new UnboundedChannelOptions { SingleReader = true });
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Channel<(string Session, string Candidate, bool Keep)> _cacheJobs = Channel.CreateUnbounded<(string, string, bool)>(new UnboundedChannelOptions { SingleReader = true });
    private SessionHistory _history = null!; // set by Start
    private Task? _cacheWorker;
    private readonly CancellationTokenSource _stop = new();
    private readonly long _clipsBudget;
    private readonly long _footageBudget;
    private DetectionRunner? _detection;
    private Task? _worker;
    private Task? _status;
    private Live? _live;

    private CaptureService(IEventBus bus, RecorderOptions options, SessionDirectory sessions, ILogger logger, SegmentStore store, ProfileSource profiles)
    {
        _profiles = profiles;
        _bus = bus;
        _options = options;
        _sessions = sessions;
        _logger = logger;
        _store = store;
        _clipsBudget = (long)(options.ClipsBudgetGB * (1L << 30));
        _footageBudget = (long)(options.FootageBudgetGB * (1L << 30));
    }

    /// <summary>Opens the store (sweeping expired segments) and starts game detection.</summary>
    /// <remarks>
    /// Detection matches executables against <paramref name="profiles"/> as
    /// they are at start; each recording takes the profiles as they are when
    /// it starts, so an edited weight or threshold applies from the next game.
    /// </remarks>
    public static CaptureService Start(IEventBus bus, RecorderOptions options, SessionDirectory sessions, ILogger logger, ClipScoring? scoring = null, ProfileSource? profiles = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        string root = options.ResolvedStoreDirectory;
        var store = SegmentStore.Open(new StoreOptions
        {
            Root = root,
            BudgetBytes = (long)(options.FootageBudgetGB * (1L << 30)),
            PreviewBudgetBytes = (long)(options.CacheBudgetGB * (1L << 30)),
        });
        SweepResult swept = CacheSweeper.Sweep(store);
        profiles ??= ProfileSource.BuiltIn(logger);
        var service = new CaptureService(bus, options, sessions, logger, store, profiles);
        service._scoring = scoring ?? ClipScoring.HeuristicOnly();
        service._history = new SessionHistory(bus, store);
        service.RestoreHistory();
        service._history.CandidateRecorded += (s, c) =>
        {
            // Highlights and short clips are cut into the cache at once; a long candidate with
            // highlights is cut only if it is opened.
            if (c.ParentCandidateId.Length > 0 || c.EndQpc - c.StartQpc <= (ulong)(CacheMaxSeconds * SystemClock.Instance.Frequency))
            {
                service._cacheJobs.Writer.TryWrite((s, c.CandidateId, false));
            }
        };
        service._cacheWorker = Task.Run(service.CacheLoopAsync);
        LogStarted(logger, root, options.FootageBudgetGB, options.ClipsBudgetGB, swept.BytesFreed >> 20);
        service._worker = Task.Run(service.WorkAsync);
        service._status = Task.Run(service.StatusLoopAsync);
        service._detection = WindowsDetection.CreateRunner(bus, profiles.Current(), settingsPath: options.DetectionSettings);
        service._detection.Changed += c => service._changes.Writer.TryWrite(c); // the poll thread only queues
        service._detection.HintsChanged += g => service._live?.ApplyHints(g);
        return service;
    }

    /// <summary>The live session's id, if a game is being recorded.</summary>
    public string? LiveSessionId => _live?.Game.Id;

    /// <summary>Cuts the candidate's window into a clip in <c>clips/</c>, from RAM when the ring still holds it, else from the segments.</summary>
    public async Task<SaveResult> SaveAsync(string sessionId, string candidateId)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Refusal(sessionId, candidateId, out ClipCandidate? candidate) is { } refused)
            {
                return refused;
            }

            StorageStatus status = Status();
            if (status.ClipsFull)
            {
                return new SaveResult(null, "clips-full", $"Saved clips have reached their {_options.ClipsBudgetGB:0.#} GB budget; delete some to save more.");
            }

            long from = (long)candidate!.StartQpc, to = (long)candidate.EndQpc;
            if (Read(sessionId, from, to, out SaveResult? gone) is not { } clip)
            {
                return gone!.Value;
            }

            string path = _store.SaveClip(sessionId, clip, from, to);
            Envelope e = new EnvelopeFactory(SystemClock.Instance, sessionId, "recorder.capture").Create(Topics.ClipSaved);
            e.ClipSaved = new ClipSaved { CandidateId = candidateId, Path = path, StartQpc = (ulong)from, EndQpc = (ulong)to };
            _bus.Publish(e);
            PublishStatus();
            LogSaved(_logger, candidateId, path);
            return new SaveResult(path, null, null);
        }
        catch (IOException ex)
        {
            return new SaveResult(null, "bad-request", $"The clip could not be written: {ex.Message}");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// A temporary preview of a candidate's window, to watch it without saving
    /// it (<see cref="SegmentStore.WritePreview"/>): the same checks and sources
    /// as <see cref="SaveAsync"/>, but it keeps nothing. It pins no footage, uses
    /// no clips budget, and old previews are deleted.
    /// </summary>
    public async Task<SaveResult> PreviewAsync(string sessionId, string candidateId)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Refusal(sessionId, candidateId, out ClipCandidate? candidate) is { } refused)
            {
                return refused;
            }

            long from = (long)candidate!.StartQpc, to = (long)candidate.EndQpc;
            if (Read(sessionId, from, to, out SaveResult? gone) is not { } clip)
            {
                return gone!.Value;
            }

            return new SaveResult(_store.WritePreview(sessionId, candidateId, clip), null, null);
        }
        catch (IOException ex)
        {
            return new SaveResult(null, "bad-request", $"The preview could not be written: {ex.Message}");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Why a candidate cannot be cut at all: unknown, or from a session that may not persist. Null when it can.</summary>
    private SaveResult? Refusal(string sessionId, string candidateId, out ClipCandidate? candidate)
    {
        candidate = _sessions.Candidate(sessionId, candidateId);
        if (candidate is null)
        {
            return new SaveResult(null, _sessions.Session(sessionId) is null ? "unknown-session" : "unknown-candidate", $"No candidate '{candidateId}' in session '{sessionId}'.");
        }

        SessionStarted? started = _sessions.Session(sessionId);
        return started is null || !RetentionRules.CanPersist(started)
            ? new SaveResult(null, "ephemeral", "This session is ephemeral: nothing from it is saved.")
            : null;
    }

    /// <summary>The recording of [from, to]: the RAM ring while the session is live, else its segments on disk.</summary>
    private ClipSource? Read(string sessionId, long from, long to, out SaveResult? gone)
    {
        gone = null;
        if (_live?.Game.Id == sessionId && _live.Ring.TrySnapshot(from, to, out ClipSource fromRam))
        {
            return fromRam;
        }

        if (_store.TryReadClip(sessionId, from, to, out ClipSource fromDisk))
        {
            return fromDisk;
        }

        gone = new SaveResult(null, "unsupported", _sessions.Session(sessionId)?.SourceKind == SourceKind.LiveGame
            ? "The recording no longer covers that moment."
            : "Only live recordings can be cut into clips; footage files are already on disk.");
        return null;
    }

    /// <summary>Candidates up to this long are cached as they are proposed; longer ones have highlights.</summary>
    public const int CacheMaxSeconds = 25;

    /// <summary>Most sessions reloaded at start: the newest, within what the session directory keeps.</summary>
    public const int MaxRestoredSessions = 32;

    /// <summary>Recorded live sessions newest first, as the store has them (footage may be partly swept).</summary>
    private IEnumerable<SessionManifest> Recorded() =>
        _store.Sessions().Where(m => m.SourceKind == SourceKind.LiveGame);

    /// <summary>
    /// Reloads recorded sessions' candidates into the session directory, so
    /// they can be rated, watched and saved after a restart.
    /// </summary>
    private void RestoreHistory()
    {
        int restored = 0;
        foreach (SessionManifest m in Recorded().Take(MaxRestoredSessions).Reverse())
        {
            IReadOnlyList<Envelope> lines = _history.Restore(m.Id);
            if (lines.Count == 0)
            {
                continue;
            }

            if (!lines.Any(e => e.PayloadCase == Envelope.PayloadOneofCase.SessionStarted))
            {
                Envelope header = new EnvelopeFactory(SystemClock.Instance, m.Id, "recorder.capture").Create(Topics.Session);
                header.SessionStarted = m.ToSessionStarted();
                _sessions.Apply(header);
            }

            foreach (Envelope e in lines)
            {
                _sessions.Apply(e);
            }

            restored++;
        }

        if (restored > 0)
        {
            LogRestored(_logger, restored);
        }
    }

    /// <summary>
    /// Recorded sessions' own envelopes for the shell's library, oldest
    /// session first, the live one left out (it streams). A session whose
    /// end was never recorded (the recorder stopped) gets one.
    /// </summary>
    public IReadOnlyList<string> HistoryLines()
    {
        string? live = LiveSessionId;
        var lines = new List<string>();
        foreach (SessionManifest m in Recorded().Take(MaxRestoredSessions).Reverse())
        {
            if (m.Id == live)
            {
                continue;
            }

            IReadOnlyList<string> stored = _store.ReadEvents(m.Id);
            if (stored.Count == 0)
            {
                continue;
            }

            if (!stored[0].Contains("\"sessionStarted\"", StringComparison.Ordinal))
            {
                Envelope header = new EnvelopeFactory(SystemClock.Instance, m.Id, "recorder.capture").Create(Topics.Session);
                header.SessionStarted = m.ToSessionStarted();
                lines.Add(SessionHistory.FormatLine(header));
            }

            lines.AddRange(stored);
            if (!stored.Any(l => l.Contains("\"sessionEnded\"", StringComparison.Ordinal)))
            {
                Envelope end = new EnvelopeFactory(SystemClock.Instance, m.Id, "recorder.capture").Create(Topics.Session);
                end.SessionEnded = new SessionEnded { Reason = "recorded earlier" };
                lines.Add(SessionHistory.FormatLine(end));
            }
        }

        return lines;
    }

    /// <summary>
    /// The user rated a candidate of a live recording: keep its clip. The
    /// cached cut moves to saved clips (or one is cut now); queued, so the
    /// rating's reply does not wait on it.
    /// </summary>
    public void KeepRated(string sessionId, string candidateId) =>
        _cacheJobs.Writer.TryWrite((sessionId, candidateId, true));

    private async Task CacheLoopAsync()
    {
        try
        {
            await foreach ((string session, string candidate, bool keep) in _cacheJobs.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))
            {
                // The session directory learns a candidate from the bus too: give it a moment.
                for (int attempt = 0; attempt < 5; attempt++)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(250 * (attempt + 1)), _stop.Token).ConfigureAwait(false);
                    SaveResult r = keep ? await KeepAsync(session, candidate).ConfigureAwait(false) : await PreviewAsync(session, candidate).ConfigureAwait(false);
                    if (r.Error != "unknown-candidate")
                    {
                        if (r.Path is null && r.Error is not null)
                        {
                            LogCacheSkipped(_logger, candidate, r.Message ?? r.Error);
                        }

                        break;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Keeps a candidate's clip as a saved clip: moves its cached cut, or cuts one. Nothing when it is already kept.</summary>
    private async Task<SaveResult> KeepAsync(string sessionId, string candidateId)
    {
        if (_history.IsKept(sessionId, candidateId))
        {
            return new SaveResult(null, null, null);
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        string? path;
        long from, to;
        try
        {
            if (Refusal(sessionId, candidateId, out ClipCandidate? candidate) is { } refused)
            {
                return refused;
            }

            if (Status().ClipsFull)
            {
                return new SaveResult(null, "clips-full", "Saved clips are full: the rated clip stays in the cache.");
            }

            from = (long)candidate!.StartQpc;
            to = (long)candidate.EndQpc;
            path = _store.KeepPreview(sessionId, candidateId, from, to);
            if (path is null)
            {
                if (Read(sessionId, from, to, out SaveResult? gone) is not { } clip)
                {
                    return gone!.Value;
                }

                path = _store.SaveClip(sessionId, clip, from, to);
            }
        }
        catch (IOException ex)
        {
            return new SaveResult(null, "bad-request", $"The clip could not be kept: {ex.Message}");
        }
        finally
        {
            _gate.Release();
        }

        Envelope e = new EnvelopeFactory(SystemClock.Instance, sessionId, "recorder.capture").Create(Topics.ClipSaved);
        e.ClipSaved = new ClipSaved { CandidateId = candidateId, Path = path, StartQpc = (ulong)from, EndQpc = (ulong)to };
        _bus.Publish(e);
        PublishStatus();
        LogSaved(_logger, candidateId, path);
        return new SaveResult(path, null, null);
    }

    /// <summary>Everything the store holds, for the shell's storage page.</summary>
    public System.Text.Json.Nodes.JsonObject Listing()
    {
        string? live = LiveSessionId;
        var sessions = new System.Text.Json.Nodes.JsonArray();
        foreach (SessionManifest m in Recorded())
        {
            IReadOnlyList<string> lines = _store.ReadEvents(m.Id);
            int candidates = lines.Count(l => l.Contains("\"clipCandidate\"", StringComparison.Ordinal));
            DateTimeOffset? sweep = m.Keep || m.Id == live ? null : (m.EndedUtc ?? m.StartedUtc) + _store.Options.Grace;
            sessions.Add(new System.Text.Json.Nodes.JsonObject
            {
                ["id"] = m.Id,
                ["title"] = m.Title,
                ["profile"] = m.GameProfile,
                ["startedUtc"] = m.StartedUtc,
                ["endedUtc"] = m.EndedUtc,
                ["live"] = m.Id == live,
                ["keep"] = m.Keep,
                ["bytes"] = _store.SessionBytes(m.Id),
                ["candidates"] = candidates,
                ["sweepAfterUtc"] = sweep,
            });
        }

        var clips = new System.Text.Json.Nodes.JsonArray();
        foreach (FileInfo f in Files(_store.ClipsRoot))
        {
            ClipSaved? saved = _history.SavedClip(f.FullName, out string? sessionId);
            clips.Add(new System.Text.Json.Nodes.JsonObject
            {
                ["path"] = f.FullName,
                ["name"] = f.Name,
                ["bytes"] = f.Length,
                ["createdUtc"] = new DateTimeOffset(f.CreationTimeUtc, TimeSpan.Zero),
                ["sessionId"] = sessionId,
                ["candidateId"] = saved?.CandidateId,
            });
        }

        var cache = new System.Text.Json.Nodes.JsonArray();
        long cacheBytes = 0;
        foreach (FileInfo f in Files(_store.PreviewsRoot))
        {
            cacheBytes += f.Length;
            cache.Add(new System.Text.Json.Nodes.JsonObject
            {
                ["path"] = f.FullName,
                ["name"] = f.Name,
                ["bytes"] = f.Length,
                ["lastUsedUtc"] = new DateTimeOffset(f.LastWriteTimeUtc, TimeSpan.Zero),
                ["expiresUtc"] = new DateTimeOffset(f.LastWriteTimeUtc, TimeSpan.Zero) + _store.Options.PreviewMaxAge,
            });
        }

        StorageStatus s = Status();
        return new System.Text.Json.Nodes.JsonObject
        {
            ["root"] = _store.Options.Root,
            ["sessions"] = sessions,
            ["clips"] = clips,
            ["cache"] = cache,
            ["footageBytes"] = (long)s.FootageBytes,
            ["footageBudgetBytes"] = (long)s.FootageBudgetBytes,
            ["clipsBytes"] = (long)s.ClipsBytes,
            ["clipsBudgetBytes"] = (long)s.ClipsBudgetBytes,
            ["cacheBytes"] = cacheBytes,
            ["cacheBudgetBytes"] = _store.Options.PreviewBudgetBytes,
            ["volumeFreeBytes"] = (long)s.VolumeFreeBytes,
        };
    }

    private static IEnumerable<FileInfo> Files(string dir) =>
        Directory.Exists(dir) ? new DirectoryInfo(dir).EnumerateFiles("*.mp4").OrderByDescending(f => f.LastWriteTimeUtc) : [];

    /// <summary>
    /// Deletes what the storage page asked for: a session's footage (with
    /// its cached clips; never the live one), a saved clip (its pin goes too),
    /// or a cached clip. Returns why it was refused, or null when done.
    /// </summary>
    public async Task<string?> DeleteAsync(string kind, string? sessionId, string? path)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            switch (kind)
            {
                case "session" when sessionId is not null:
                    if (sessionId == LiveSessionId)
                    {
                        return "That session is still recording.";
                    }

                    return _store.DeleteSession(sessionId) ? null : "The session could not be deleted (unknown, or in use).";
                case "clip" when path is not null:
                    ClipSaved? saved = _history.SavedClip(path, out string? owner);
                    QpcRange? pin = saved is null ? null : new QpcRange((long)saved.StartQpc, (long)saved.EndQpc);
                    return _store.DeleteFile(path, owner, pin) ? null : "That is not a saved clip the recorder made.";
                case "cache" when path is not null:
                    return _store.DeleteFile(path) ? null : "That is not a cached clip.";
                case "cache":
                    foreach (FileInfo f in Files(_store.PreviewsRoot).ToList())
                    {
                        _store.DeleteFile(f.FullName);
                    }

                    return null;
                default:
                    return "Delete a \"session\" (with sessionId), a \"clip\" or one \"cache\" file (with path), or the whole \"cache\".";
            }
        }
        finally
        {
            _gate.Release();
            PublishStatus();
        }
    }

    /// <summary>Keeps a session out of the sweep (or lets it go again).</summary>
    public bool SetKeep(string sessionId, bool keep) => _store.SetKeep(sessionId, keep);

    /// <summary>The two budgets now: saved clips (never swept) and session footage.</summary>
    public StorageStatus Status()
    {
        long clips = 0;
        try
        {
            if (Directory.Exists(_store.ClipsRoot))
            {
                clips = new DirectoryInfo(_store.ClipsRoot).EnumerateFiles("*.mp4").Sum(f => f.Length);
            }
        }
        catch (IOException)
        {
        }

        long free = 0;
        try
        {
            free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(_store.Options.Root))!).AvailableFreeSpace;
        }
        catch (IOException)
        {
        }

        return new StorageStatus
        {
            ClipsBytes = (ulong)clips,
            ClipsBudgetBytes = (ulong)_clipsBudget,
            FootageBytes = (ulong)_store.TotalBytes,
            FootageBudgetBytes = (ulong)Math.Min(_footageBudget, _store.Options.BudgetBytes),
            ClipsNearLimit = clips >= _clipsBudget * _options.ClipsWarnFraction,
            ClipsFull = clips >= _clipsBudget,
            VolumeFreeBytes = (ulong)free,
        };
    }

    private void PublishStatus()
    {
        Envelope e = new EnvelopeFactory(SystemClock.Instance, "recorder", "recorder.storage").Create(Topics.Storage);
        e.StorageStatus = Status();
        _bus.Publish(e);
    }

    private async Task StatusLoopAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                PublishStatus();
                await Task.Delay(StatusInterval, _stop.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Starts and stops sessions one change at a time, off the detection poll thread.</summary>
    private async Task WorkAsync()
    {
        try
        {
            await foreach (LifecycleChange c in _changes.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))
            {
                await _gate.WaitAsync(_stop.Token).ConfigureAwait(false);
                try
                {
                    switch (c.Kind)
                    {
                        case GameLifecycle.Types.Kind.Detected when _live is null:
                            _live = TryStartLive(c);
                            break;
                        case GameLifecycle.Types.Kind.Detected:
                            LogBusy(_logger, c.Game.Match.DisplayName, _live.Game.Match.DisplayName);
                            break;
                        case GameLifecycle.Types.Kind.WindowChanged when _live?.Game.Id == c.Game.Id:
                            // The window moved, resized or changed monitor: capture it afresh (a new session).
                            await _live.DisposeAsync().ConfigureAwait(false);
                            _live = TryStartLive(c);
                            break;
                        case GameLifecycle.Types.Kind.Exited when _live?.Game.Id == c.Game.Id:
                            await _live.DisposeAsync().ConfigureAwait(false);
                            _live = null;
                            PublishStatus();
                            break;
                    }
                }
                finally
                {
                    _gate.Release();
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private Live? TryStartLive(LifecycleChange change)
    {
        GameInstance game = change.Game;
        CaptureTarget? target = WindowsDetection.TryCaptureTarget(game);
        if (target is null)
        {
            LogNoWindow(_logger, game.Match.DisplayName);
            return null;
        }

        try
        {
            return Live.Start(this, change, target);
        }
        catch (Exception ex) when (ex is InvalidOperationException or SharpGen.Runtime.SharpGenException or IOException or UnauthorizedAccessException or AggregateException)
        {
            LogStartFailed(_logger, game.Match.DisplayName, ex.Message);
            return null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        if (_detection is not null)
        {
            await _detection.DisposeAsync().ConfigureAwait(false);
        }

        _changes.Writer.TryComplete();
        _cacheJobs.Writer.TryComplete();
        foreach (Task? t in (Task?[])[_worker, _status, _cacheWorker])
        {
            if (t is not null)
            {
                await t.ConfigureAwait(false);
            }
        }

        if (_live is not null)
        {
            await _live.DisposeAsync().ConfigureAwait(false);
            _live = null;
        }

        await _history.DisposeAsync().ConfigureAwait(false);
        _watchdog.Dispose();
        _gate.Dispose();
        _stop.Dispose();
    }

    /// <summary>One recorded game: everything that runs while it plays, torn down in reverse.</summary>
    private sealed class Live : IAsyncDisposable
    {
        private readonly CaptureService _owner;
        private readonly LiveCaptureSession _session;
        private readonly EncodeTicker _ticker;
        private readonly IDisposable _captureWatch;
        private readonly MfAacEncoder? _gameAac;
        private readonly MfAacEncoder? _micAac;
        private readonly WasapiSource? _gameAudio;
        private readonly WasapiSource? _mic;
        private readonly SessionRecording _recording;
        private readonly CancellationTokenSource _recordingStop = new();
        private readonly Task _recordingTask;
        private readonly ProfileRunner _profile;
        private readonly ClipperRunner _clipper;

        private Live(CaptureService owner, GameInstance game, LiveCaptureSession session, ReplayRing ring, EncodeTicker ticker, IDisposable captureWatch,
            MfAacEncoder? gameAac, MfAacEncoder? micAac, WasapiSource? gameAudio, WasapiSource? mic, SessionRecording recording, ProfileRunner profile, ClipperRunner clipper)
        {
            _owner = owner;
            Game = game;
            _session = session;
            Ring = ring;
            _ticker = ticker;
            _captureWatch = captureWatch;
            _gameAac = gameAac;
            _micAac = micAac;
            _gameAudio = gameAudio;
            _mic = mic;
            _recording = recording;
            _profile = profile;
            _clipper = clipper;
            _recordingTask = recording.RunAsync(TimeSpan.FromMilliseconds(500), _recordingStop.Token);
        }

        public GameInstance Game { get; }

        public ReplayRing Ring { get; }

        public void ApplyHints(GameInstance g)
        {
            if (g.Id == Game.Id)
            {
                _profile.ApplyHints(g.Hints, SystemClock.Instance.Now);
            }
        }

        public static Live Start(CaptureService owner, LifecycleChange change, CaptureTarget target)
        {
            GameInstance game = change.Game;
            RecorderOptions o = owner._options;
            long freq = SystemClock.Instance.Frequency;

            // 1. Plan first: the probe creates a DXGI factory, which must not happen while an encoder runs.
            MachineProbeResult probe = WindowsMachineProbe.Run(o.ResolvedStoreDirectory);
            ProfileMapping map = MachineProfileMapper.Map(probe, target.ToProbeTarget());
            RecordingPlan plan = GovernorPolicy.Decide(map.Profile, new GovernorOverrides { DiskCapBytes = owner._footageBudget });
            owner._store.Reconfigure(plan.DiskBudgetBytes, plan.FreeSpaceFloorBytes);

            // 2. Capture and analysis, with the game's HUD regions as extra signals.
            ProfileCatalog catalog = owner._profiles.Current(); // the profiles folder as it is now, so edits apply from this game on
            string profileId = catalog.Contains(game.Match.ProfileId) ? game.Match.ProfileId : "generic";
            AnalysisFormat format = AnalysisFormat.Default;
            IReadOnlyList<HudRegion> regions = catalog.SubtreeRegions(catalog.PathTo(profileId)[0].Id);
            IVideoSignal[] extra = regions.Count > 0 ? [new RegionSignal(regions, format.Width, format.Height)] : [];
            LiveCaptureSession session = LiveCaptureSession.Start(owner._bus, new LiveSessionOptions
            {
                Target = target,
                SessionId = game.Id,
                // A game the user opted in under the generic profile is named by its program, not "Any game".
                Title = profileId == "generic" ? Path.GetFileNameWithoutExtension(game.ExeName) : game.Match.DisplayName,
                GameProfile = profileId,
                ExtraVideoSignals = extra,
            });

            // The bridge's snapshot resets a session on SessionStarted: announce the game again after it.
            Envelope lifecycle = new EnvelopeFactory(SystemClock.Instance, game.Id, "recorder.detection").Create(Topics.GameLifecycle);
            lifecycle.GameLifecycle = change.ToProto();
            owner._bus.Publish(lifecycle);

            ReplayRing? ring = null;
            EncodeTicker? ticker = null;
            try
            {
                // 3. Encode into the RAM ring; the watchdog swaps a stalled encoder or ends the process.
                ring = new ReplayRing(plan.RingBytes, QpcTime.FromTimeSpan(plan.RingLength, freq));
                var settings = new VideoEncoderSettings(plan.Tier.Codec, plan.Tier.Width, plan.Tier.Height, plan.Tier.Fps, plan.Tier.BitrateBps) { PreferHardware = plan.HardwareEncode };
                ticker = EncodeTicker.Start(session.Gpu, settings, ring, trackId: 1, SystemClock.Instance, new EncodeTickerOptions
                {
                    Watchdog = owner._watchdog,
                    Blocklist = owner._blocklist,
                    Log = m => LogEncode(owner._logger, m),
                });
                session.FrameTap = ticker.FrameTap;
                IDisposable captureWatch = owner._watchdog.Watch($"capture thread ({game.Match.DisplayName})", session.CaptureBeat, TimeSpan.FromSeconds(4),
                    r => Fatal.Default($"capture thread stuck in {r.Step} for {r.For.TotalSeconds:F1} s"));

                // 4. Audio: game loopback (process loopback where the OS has it), the mic only if enabled. A failure here leaves video running.
                AudioPlan audio = AudioCapturePolicy.Decide(session.Started, AudioCapturePolicy.OsBuild, game.Pid, o.Microphone);
                MfAacEncoder? gameAac = null, micAac = null;
                WasapiSource? gameAudio = null, mic = null;
                try
                {
                    gameAac = audio.Encode && audio.Game is not null ? MfAacEncoder.Create(new AacEncoderSettings { Role = "game" }, ring, trackId: 2, freq) : null;
                    micAac = audio.Encode && audio.Microphone ? MfAacEncoder.Create(new AacEncoderSettings { Role = "mic" }, ring, trackId: 3, freq) : null;
                    LiveCaptureSession s0 = session;
                    ILogger log = owner._logger;
                    string name = game.Match.DisplayName;
                    gameAudio = audio.Game is null ? null : WasapiSource.Start(
                        AudioSourceOptions.ForGame(audio, game.Pid) with { Log = line => LogAudioNote(log, name, line) }, // why it fell back to the endpoint
                        gameAac is null ? null : gameAac.Encode,
                        b => s0.Analyzer.SubmitAudio(b));
                    mic = audio.Microphone ? WasapiSource.Start(new AudioSourceOptions { Kind = AudioSourceKind.Microphone }, micAac is null ? null : micAac.Encode) : null;
                }
                catch (Exception ex) when (ex is InvalidOperationException or SharpGen.Runtime.SharpGenException or System.Runtime.InteropServices.COMException)
                {
                    LogAudioFailed(owner._logger, game.Match.DisplayName, ex.Message);
                }

                // 5. Segments to disk, then the profile and the clipper (events and candidates).
                SessionRecording recording = owner._store.BeginSession(game.Id, session.Started, SystemClock.Instance.Now, ring);
                var profile = new ProfileRunner(owner._bus, catalog, profileId, game.Match.Method, game.Id, freq, publishCandidates: false);
                profile.ApplyHints(game.Hints, SystemClock.Instance.Now);
                // Every candidate gets its features (and a model score when there is a model) before it is published.
                var clipper = new ClipperRunner(owner._bus, catalog, profileId, game.Id, freq, owner._scoring.Options(freq));

                // The analyzer named its signals when capture started: before the session was
                // announced and before the profile and clipper subscribed. Name them again now, or
                // the profile fires no events, the clipper proposes nothing, and pages that join
                // late (the profiler) see no signals.
                session.Analyzer.PublishSchemas();
                LogLive(owner._logger, game.Match.DisplayName, ticker.EncoderName, plan.Tier.Name, plan.RingLength.TotalSeconds, audio.Reason);
                return new Live(owner, game, session, ring, ticker, captureWatch, gameAac, micAac, gameAudio, mic, recording, profile, clipper);
            }
            catch
            {
                ticker?.Dispose();
                ring?.Dispose();
                session.DisposeAsync().AsTask().GetAwaiter().GetResult();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            _session.FrameTap = null;
            _captureWatch.Dispose();
            _ticker.Dispose(); // drains the encoder; never hangs
            _gameAudio?.Dispose();
            _mic?.Dispose();
            _gameAac?.Dispose();
            _micAac?.Dispose();
            await _session.DisposeAsync().ConfigureAwait(false); // stops capture, publishes SessionEnded
            await _profile.DisposeAsync().ConfigureAwait(false);
            await _clipper.DisposeAsync().ConfigureAwait(false);
            await _recordingStop.CancelAsync().ConfigureAwait(false);
            try
            {
                await _recordingTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            await _recording.DisposeAsync().ConfigureAwait(false); // last segment, session marked ended
            Ring.Dispose();
            _recordingStop.Dispose();
            LogStopped(_owner._logger, Game.Match.DisplayName, _recording.SegmentsWritten);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Capture ready: store {Root}, footage budget {Footage} GB, clips budget {Clips} GB ({Swept} MB of expired segments swept)")]
    private static partial void LogStarted(ILogger logger, string root, double footage, double clips, long swept);

    [LoggerMessage(Level = LogLevel.Information, Message = "Recording {Game}: {Encoder}, tier {Tier}, ring {Ring:F0} s; audio: {Audio}")]
    private static partial void LogLive(ILogger logger, string game, string encoder, string tier, double ring, string audio);

    [LoggerMessage(Level = LogLevel.Information, Message = "Stopped recording {Game} ({Segments} segments)")]
    private static partial void LogStopped(ILogger logger, string game, int segments);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Game} started while {Recording} is recording; not recording it")]
    private static partial void LogBusy(ILogger logger, string game, string recording);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Game} has no capturable window yet")]
    private static partial void LogNoWindow(ILogger logger, string game);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not start recording {Game}: {Reason}")]
    private static partial void LogStartFailed(ILogger logger, string game, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Audio for {Game}: {Line}")]
    private static partial void LogAudioNote(ILogger logger, string game, string line);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Audio for {Game} did not start, recording video only: {Reason}")]
    private static partial void LogAudioFailed(ILogger logger, string game, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Message}")]
    private static partial void LogEncode(ILogger logger, string message);

    [LoggerMessage(Level = LogLevel.Information, Message = "Reloaded {Count} recorded sessions: their candidates can be watched, rated and saved")]
    private static partial void LogRestored(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "Clip cache skipped {Candidate}: {Reason}")]
    private static partial void LogCacheSkipped(ILogger logger, string candidate, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Saved clip of {Candidate} to {Path}")]
    private static partial void LogSaved(ILogger logger, string candidate, string path);
}
