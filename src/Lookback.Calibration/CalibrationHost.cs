using System.Buffers;
using System.Buffers.Binary;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lookback.Calibration.Probe;
using Lookback.Calibration.Profiles;
using Lookback.Core.Bus;
using Lookback.Contracts.V1;
using Lookback.Core.Time;
using Lookback.Media.Frames;
using Lookback.Profiles.Model;
using Lookback.Scoring.Ratings;
using Lookback.Stream.SessionLog;
using Lookback.Stream.WebSockets;

namespace Lookback.Calibration;

/// <summary>Options for <see cref="CalibrationHost"/>.</summary>
public sealed record CalibrationHostOptions
{
    /// <summary>
    /// The checkout's <c>profiles</c> folder. The session runs on the profiles
    /// in it and Save writes confirmed numbers back into them. Null runs on the
    /// built-in profiles with saving off.
    /// </summary>
    public string? ProfilesDirectory { get; init; }

    /// <summary>
    /// Where the clipper check saves your ratings for training the scorer:
    /// stars and each candidate's numbers, never the video
    /// (<see cref="RatingStore.AppendCheck"/>). Null keeps ratings in memory only.
    /// </summary>
    public RatingStore? Ratings { get; init; }

    /// <summary>
    /// Starts hearing the player when one starts: given the session's header
    /// and where to send each hop (mono, analysis rate, capture QPC), returns
    /// what stops it, or null for none. The recorder captures the desktop
    /// shell's own process tree with process loopback, never the whole system.
    /// Null keeps the player video only.
    /// </summary>
    public Func<SessionStarted, Action<AudioBlock>, IDisposable?>? PlayerAudio { get; init; }
}

/// <summary>
/// The official-player calibration endpoints the WebSocket bridge serves:
/// <list type="bullet">
/// <item><c>/ingest</c>: the viewer window streams the player's frames in (see the wire format below).</item>
/// <item><c>/calibrate</c> and <c>/calibrate/ws</c>: the panel, which shows proposals, takes marks and confirms saves.</item>
/// </list>
/// Wire format of <c>/ingest</c>: a text <c>{"type":"start","platform":"youtube"|"twitch","profile":"cs2"}</c>,
/// then binary little-endian messages <c>byte kind, int64 microseconds</c> followed, for
/// kind 1 (video), by <c>uint16 width, uint16 height, BGRA8 pixels</c>; then a text <c>{"type":"stop"}</c>.
/// Kind 2 (audio) is accepted and ignored: the player's sound comes from <see cref="CalibrationHostOptions.PlayerAudio"/>.
/// A text <c>{"type":"position","t":microseconds,"video":seconds,"rate":1,"playing":true,"seekable":true}</c> reports the
/// player's own position (in memory only); the host sends <c>{"type":"seek","seconds":s}</c> back to replay a candidate.
/// Frames are read into one pooled buffer and analysed in place.
/// </summary>
public sealed class CalibrationHost : IAsyncDisposable
{
    public const string IngestPath = "/ingest";
    public const string PanelPath = "/calibrate";
    public const string PanelSocketPath = "/calibrate/ws";
    private const int MaxMessage = (2560 * 1440 * 4) + 64;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IEventBus _bus;
    private readonly IClock _clock;
    private readonly CalibrationWriter? _writer;
    private readonly RatingStore? _ratings;
    private readonly Func<SessionStarted, Action<AudioBlock>, IDisposable?>? _playerAudio;
    private IDisposable? _audio;
    private string? _audioNote;
    private readonly SemaphoreSlim _ingestSend = new(1, 1);
    private WebSocket? _ingestSocket;
    private readonly ProfileCatalog _builtIn;
    private readonly Lock _gate = new();
    private OfficialPlayerSession? _session;
    private string? _lastSaved;

    public CalibrationHost(IEventBus bus, CalibrationHostOptions? options = null, IClock? clock = null)
    {
        ArgumentNullException.ThrowIfNull(bus);
        _bus = bus;
        _clock = clock ?? SystemClock.Instance;
        options ??= new CalibrationHostOptions();
        _writer = options.ProfilesDirectory is null ? null : new CalibrationWriter(options.ProfilesDirectory);
        _ratings = options.Ratings;
        _playerAudio = options.PlayerAudio;
        _builtIn = ProfileCatalog.BuiltIn;
    }

    /// <summary>The bridge pages: the panel.</summary>
    public static BridgePage Page => new(PanelPath, PanelHtml.Value);

    /// <summary>The bridge sockets: ingest and the panel's socket.</summary>
    public IReadOnlyList<BridgeSocket> Sockets => [new(IngestPath, IngestAsync), new(PanelSocketPath, PanelAsync)];

    public OfficialPlayerSession? Session
    {
        get
        {
            lock (_gate)
            {
                return _session;
            }
        }
    }

    private static readonly Lazy<string> PanelHtml = new(() =>
    {
        using System.IO.Stream s = typeof(CalibrationHost).Assembly.GetManifestResourceStream("calibrate.html")
            ?? throw new InvalidOperationException("The calibration panel is missing from the assembly.");
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    });

    private ProfileCatalog LoadCatalog() => _writer?.Catalog() ?? _builtIn;

    /// <summary>Starts a session as the ingest's start message would; for tests and tools.</summary>
    public async Task<OfficialPlayerSession> StartAsync(string platform, string? profile)
    {
        var session = new OfficialPlayerSession(_bus, LoadCatalog(), platform, _clock);
        await session.StartAsync(profile).ConfigureAwait(false);
        session.SetOrigin(_clock.Now);
        OfficialPlayerSession? old;
        IDisposable? oldAudio;
        lock (_gate)
        {
            old = _session;
            _session = session;
            oldAudio = _audio;
            _audio = null;
        }

        // Stop hearing the old player before it goes; never under a lock (stopping joins the audio thread).
        oldAudio?.Dispose();
        if (old is not null)
        {
            await old.DisposeAsync().ConfigureAwait(false);
        }

        StartAudio(session);
        return session;
    }

    private void StartAudio(OfficialPlayerSession session)
    {
        IDisposable? handle = null;
        if (_playerAudio is null || session.Started is not { } started)
        {
            _audioNote = "video only: this host has no audio source for the player";
            return;
        }

        try
        {
            handle = _playerAudio(started, session.OnAudio);
            _audioNote = handle is null ? "video only: the player's sound cannot be isolated here" : null;
        }
        catch (Exception e) when (e is InvalidOperationException or ExternalException or IOException or UnauthorizedAccessException)
        {
            _audioNote = $"video only: {e.Message}";
        }

        lock (_gate)
        {
            if (ReferenceEquals(_session, session))
            {
                _audio = handle;
                handle = null;
            }
        }

        handle?.Dispose(); // the player changed meanwhile
    }

    public async Task StopAsync()
    {
        OfficialPlayerSession? old;
        IDisposable? audio;
        lock (_gate)
        {
            old = _session;
            _session = null;
            audio = _audio;
            _audio = null;
        }

        audio?.Dispose();
        if (old is not null)
        {
            await old.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task IngestAsync(WebSocket socket, CancellationToken ct)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(MaxMessage);
        Volatile.Write(ref _ingestSocket, socket); // the viewer: replays are sent back on it
        try
        {
            while (socket.State == WebSocketState.Open)
            {
                int length = 0;
                ValueWebSocketReceiveResult r;
                do
                {
                    if (length == MaxMessage)
                    {
                        await socket.CloseAsync(WebSocketCloseStatus.MessageTooBig, "frame too large", ct).ConfigureAwait(false);
                        return;
                    }

                    r = await socket.ReceiveAsync(buffer.AsMemory(length, MaxMessage - length), ct).ConfigureAwait(false);
                    length += r.Count;
                }
                while (!r.EndOfMessage && r.MessageType != WebSocketMessageType.Close);

                if (r.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                if (r.MessageType == WebSocketMessageType.Text)
                {
                    JsonNode? msg = JsonNode.Parse(buffer.AsSpan(0, length));
                    switch ((string?)msg?["type"])
                    {
                        case "start":
                            await StartAsync((string?)msg!["platform"] ?? "", (string?)msg["profile"] is { Length: > 0 } p ? p : null).ConfigureAwait(false);
                            break;
                        case "stop":
                            await StopAsync().ConfigureAwait(false);
                            break;
                        case "position":
                            // The player's own position, for replays; in memory only.
                            if (Session is { } playing && Number(msg!["t"]) is { } t && Number(msg["video"]) is { } video)
                            {
                                playing.OnPosition((long)Math.Round(t), video, Number(msg["rate"]) ?? 1, msg["playing"]?.GetValueKind() == JsonValueKind.True, msg["seekable"]?.GetValueKind() == JsonValueKind.True);
                            }

                            break;
                        default:
                            break;
                    }

                    continue;
                }

                ReadOnlySpan<byte> m = buffer.AsSpan(0, length);
                if (m.Length < 9 || Session is not { } session)
                {
                    continue;
                }

                long micros = BinaryPrimitives.ReadInt64LittleEndian(m[1..]);
                if (m[0] == 1 && m.Length >= 13)
                {
                    int w = BinaryPrimitives.ReadUInt16LittleEndian(m[9..]);
                    int h = BinaryPrimitives.ReadUInt16LittleEndian(m[11..]);
                    session.OnVideo(micros, w, h, m[13..]);
                }
            }
        }
        finally
        {
            Interlocked.CompareExchange(ref _ingestSocket, null, socket);
            Array.Clear(buffer, 0, Math.Min(buffer.Length, MaxMessage)); // no frame outlives the connection, even in the pool
            ArrayPool<byte>.Shared.Return(buffer);
            await StopAsync().ConfigureAwait(false);
        }
    }

    private async Task PanelAsync(WebSocket socket, CancellationToken ct)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task sending = Task.Run(async () =>
        {
            while (!stop.Token.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                await SendAsync(socket, State(), stop.Token).ConfigureAwait(false);
                await Task.Delay(500, stop.Token).ConfigureAwait(false);
            }
        }, stop.Token);

        byte[] buffer = new byte[64 * 1024];
        try
        {
            while (socket.State == WebSocketState.Open)
            {
                WebSocketReceiveResult r = await socket.ReceiveAsync(buffer, ct).ConfigureAwait(false);
                if (r.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                if (!r.EndOfMessage || r.MessageType != WebSocketMessageType.Text)
                {
                    continue;
                }

                string reply;
                try
                {
                    reply = await CommandAsync(JsonNode.Parse(buffer.AsSpan(0, r.Count))).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is JsonException or KeyNotFoundException or ArgumentException or InvalidDataException or RetentionViolationException or IOException or InvalidOperationException)
                {
                    reply = JsonSerializer.Serialize(new { type = "error", message = ex.Message }, Json);
                }

                await SendAsync(socket, reply, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            await stop.CancelAsync().ConfigureAwait(false);
            try
            {
                await sending.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (WebSocketException)
            {
            }
        }
    }

    private static Task SendAsync(WebSocket socket, string text, CancellationToken ct) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, ct);

    /// <summary>Runs one panel command; returns the reply. Public for tests.</summary>
    public async Task<string> CommandAsync(JsonNode? msg)
    {
        OfficialPlayerSession session = Session ?? throw new InvalidOperationException("No player is playing.");
        switch ((string?)msg?["type"])
        {
            case "profile":
                await session.StartAsync((string?)msg!["id"] is { Length: > 0 } id ? id : null).ConfigureAwait(false);
                return Ok("profile");
            case "mark":
                session.Mark((string?)msg!["event"] ?? throw new ArgumentException("A mark names its event."));
                return Ok("mark");
            case "apply":
                await session.ApplyAsync(Edits(msg!)).ConfigureAwait(false);
                return Ok("apply");
            case "judge":
            {
                string candidateId = (string?)msg!["candidateId"] ?? throw new ArgumentException("judge needs a candidateId.");
                int stars = msg["stars"]?.GetValue<int>() ?? throw new ArgumentException("judge needs stars (0 to 5).");
                if (!session.Check.Judge(candidateId, stars))
                {
                    throw new KeyNotFoundException("No such candidate in the check.");
                }

                // Your rating is yours: it is saved with the candidate's numbers, never the video.
                (bool stored, string? reason) = SaveRating(session, candidateId, stars);
                return JsonSerializer.Serialize(new { type = "ok", what = "judge", stored, reason }, Json);
            }
            case "missed":
                session.MarkMissed();
                return Ok("missed");
            case "check-reset":
                session.Check.Reset();
                return Ok("check-reset");
            case "replay":
            {
                // The player goes back to the moment itself; nothing of the video is kept to replay it.
                string candidateId = (string?)msg!["candidateId"] ?? throw new ArgumentException("replay needs a candidateId.");
                Check.CheckCandidate c = session.Check.Get(candidateId) ?? throw new KeyNotFoundException("No such candidate in the check.");
                if (!session.Seekable)
                {
                    throw new InvalidOperationException("This player cannot go back (a live channel), so the moment cannot be replayed.");
                }

                double seconds = session.VideoSecondsAt(c.StartQpc) ?? throw new InvalidOperationException("The player has not reported where it is yet; try again in a second.");
                await SendToViewerAsync(JsonSerializer.Serialize(new { type = "seek", seconds = Math.Round(seconds, 2) }, Json)).ConfigureAwait(false);
                return Ok("replay");
            }
            case "save":
                if (_writer is null)
                {
                    throw new InvalidOperationException("Saving is off: start with a profiles folder (Recorder:ProfilesDirectory or --profiles).");
                }

                // The user pressed Save on the panel and confirmed exactly these numbers.
                CalibrationEdits edits = Edits(msg!);
                IReadOnlyList<string> files = _writer.Save(session.Started!, edits, userConfirmed: msg!["confirmed"]?.GetValue<bool>() == true);
                _lastSaved = files.Count == 0 ? "nothing to save" : "saved " + string.Join(", ", files.Select(f => Path.GetRelativePath(_writer.Directory, f)));
                await session.ApplyAsync(edits).ConfigureAwait(false);
                return JsonSerializer.Serialize(new { type = "saved", files = files.Select(f => Path.GetRelativePath(_writer.Directory, f)) }, Json);
            default:
                throw new ArgumentException("Unknown command.");
        }
    }

    private static string Ok(string what) => JsonSerializer.Serialize(new { type = "ok", what }, Json);

    private static double? Number(JsonNode? n) =>
        n is JsonValue v && v.TryGetValue(out double d) && double.IsFinite(d) ? d : null;

    /// <summary>A text message to the viewer on its ingest socket; gives up after 2 s rather than wait on it.</summary>
    private async Task SendToViewerAsync(string json)
    {
        WebSocket socket = Volatile.Read(ref _ingestSocket) ?? throw new InvalidOperationException("The viewer is not connected.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            await _ingestSend.WaitAsync(timeout.Token).ConfigureAwait(false);
            try
            {
                await socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, endOfMessage: true, timeout.Token).ConfigureAwait(false);
            }
            finally
            {
                _ingestSend.Release();
            }
        }
        catch (Exception e) when (e is OperationCanceledException or WebSocketException or ObjectDisposedException)
        {
            throw new InvalidOperationException("The viewer did not take the replay; is it still open?", e);
        }
    }

    private (bool Stored, string? Reason) SaveRating(OfficialPlayerSession session, string candidateId, int stars)
    {
        if (_ratings is null)
        {
            return (false, "Saving ratings is off: they stay in memory.");
        }

        if (session.Check.RatingFor(candidateId, stars, session.Platform, session.ProfileId, videoOnly: !session.HasAudio) is not { } input)
        {
            return (false, "The candidate is no longer in the check.");
        }

        RatingWriteResult result;
        try
        {
            result = _ratings.AppendCheck(session.Started, input);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            result = new RatingWriteResult(false, $"Could not write the rating: {e.Message}", null, null);
        }

        session.Check.MarkSaved(candidateId, result.Written);
        return (result.Written, result.Reason);
    }

    // {"regions":{"kill_feed":{"x":..,"y":..,"w":..,"h":..}},"thresholds":[{"owner":"cs2","event":"elimination","index":0,"value":0.02}]}
    private static CalibrationEdits Edits(JsonNode msg)
    {
        var regions = new Dictionary<string, (double, double, double, double)>(StringComparer.Ordinal);
        foreach ((string name, JsonNode? r) in msg["regions"]?.AsObject() ?? [])
        {
            regions[name] = (r!["x"]!.GetValue<double>(), r["y"]!.GetValue<double>(), r["w"]!.GetValue<double>(), r["h"]!.GetValue<double>());
        }

        var thresholds = new Dictionary<ConditionKey, float>();
        foreach (JsonNode? t in msg["thresholds"]?.AsArray() ?? [])
        {
            thresholds[new ConditionKey((string)t!["owner"]!, (string)t["event"]!, t["index"]!.GetValue<int>())] = t["value"]!.GetValue<float>();
        }

        return new CalibrationEdits { Regions = regions, Thresholds = thresholds };
    }

    /// <summary>The panel's view, about twice a second. Public for tests.</summary>
    public string State()
    {
        OfficialPlayerSession? s = Session;
        ProfileCatalog catalog = s?.Catalog ?? LoadCatalog();
        var state = new JsonObject
        {
            ["type"] = "state",
            ["playing"] = s is not null,
            ["platform"] = s?.Platform,
            ["retention"] = "ephemeral: frames are analysed in memory and dropped; only numbers you confirm are saved",
            ["canSave"] = _writer is not null,
            ["lastSaved"] = _lastSaved,
            ["profiles"] = new JsonArray([.. catalog.All.Select(p => p.Id).Order(StringComparer.Ordinal).Select(id => (JsonNode)id)]),
        };
        if (s is null)
        {
            return state.ToJsonString();
        }

        state["profile"] = s.ProfileId;
        state["frames"] = s.VideoFrames;
        state["audioSeconds"] = Math.Round(s.AudioSeconds, 1);
        state["audioNote"] = _audioNote;
        state["check"] = Check(s, _ratings is not null, s.Seekable && s.HasPosition && Volatile.Read(ref _ingestSocket) is not null);
        CalibrationProbe probe = s.Probe;
        state["grid"] = new JsonObject { ["cols"] = probe.Columns, ["rows"] = probe.Rows, ["static"] = Cells(probe.StaticCells()) };
        IReadOnlyDictionary<string, int> marks = probe.MarkCounts();
        state["marks"] = JsonSerializer.SerializeToNode(marks, Json);
        if (s.Resolved is not { } profile)
        {
            return state.ToJsonString();
        }

        var regions = new JsonArray();
        foreach (HudRegion r in profile.Regions)
        {
            // An event whose rules read this region proposes from its marks; otherwise the static HUD does.
            string? evt = profile.Events.FirstOrDefault(e => e.When.Concat(e.WhenAny).Any(c => c.Signal.StartsWith($"region.{r.Name}.", StringComparison.Ordinal))
                && marks.ContainsKey(e.Name))?.Name;
            RegionProposal? p = (evt is null ? null : probe.ProposeFromMarks(evt, r)) ?? probe.ProposeStatic(r);
            regions.Add(new JsonObject
            {
                ["name"] = r.Name,
                ["purpose"] = r.Purpose,
                ["current"] = Rect(r.X, r.Y, r.W, r.H),
                ["proposed"] = p is null ? null : Rect(p.X, p.Y, p.W, p.H),
                ["source"] = p?.Source,
                ["evidence"] = p?.Evidence,
            });
        }

        state["regions"] = regions;
        state["events"] = new JsonArray([.. profile.Events.Select(e => (JsonNode)e.Name)]);
        var thresholds = new JsonArray();
        foreach (string evt in marks.Keys)
        {
            foreach (ThresholdProposal t in s.Tuner?.Propose(evt) ?? [])
            {
                thresholds.Add(JsonSerializer.SerializeToNode(new
                {
                    owner = t.Key.Owner, @event = t.Key.Event, index = t.Key.Index, t.Signal, t.Kind, t.Current, t.Proposed,
                    t.Marks, t.Caught, t.FalsePerMinute, t.CurrentCaught, t.CurrentFalsePerMinute,
                }, Json));
            }
        }

        state["thresholds"] = thresholds;
        return state.ToJsonString();
    }

    /// <summary>The clipper check's view: the newest candidates (times in playback seconds) and the scores so far.</summary>
    private static JsonObject Check(OfficialPlayerSession s, bool saving, bool canReplay)
    {
        double freq = s.Check.Frequency;
        double At(long qpc) => Math.Round((qpc - s.OriginQpc) / freq, 1);
        var recent = new JsonArray();
        foreach (Check.CheckCandidate c in s.Check.Recent(50))
        {
            recent.Add(new JsonObject
            {
                ["id"] = c.Id,
                ["start"] = At(c.StartQpc),
                ["end"] = At(c.EndQpc),
                ["peak"] = At(c.PeakQpc),
                ["score"] = Math.Round(c.Score, 2),
                ["reasons"] = new JsonArray([.. c.Reasons.Select(r => (JsonNode)r)]),
                ["qa"] = c.QaPassed,
                ["stars"] = c.Stars,
                ["saved"] = c.Saved,
                ["video"] = s.VideoSecondsAt(c.StartQpc) is { } v ? Math.Round(v, 1) : null, // where it is in the video, when the player reported
            });
        }

        Check.CheckStats st = s.Check.Stats();
        return new JsonObject
        {
            ["note"] = saving
                ? "Your ratings are saved to train the scorer: the stars and each candidate's numbers (its features and score), never the video, its address or any time in it. Missed-highlight marks and these scores stay in memory and are cleared when the player stops."
                : "Nothing here is stored: ratings and marks live in memory and are cleared when the player stops (saving ratings is off).",
            ["saving"] = saving,
            ["canReplay"] = canReplay,
            ["recent"] = recent,
            ["stats"] = JsonSerializer.SerializeToNode(st, Json),
        };
    }

    private static JsonObject Rect(double x, double y, double w, double h) => new() { ["x"] = x, ["y"] = y, ["w"] = w, ["h"] = h };

    private static JsonArray Cells(float[] cells) => new([.. cells.Select(c => (JsonNode)Math.Round(c, 2))]);

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _ingestSend.Dispose();
    }
}
