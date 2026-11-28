using System.Text.Json;
using Lookback.Contracts;
using Lookback.Contracts.V1;
using Lookback.Core.Bus;
using Lookback.Core.Time;
using Lookback.Scoring.Ratings;
using Microsoft.Extensions.Logging;

namespace Lookback.Recorder;

/// <summary>
/// Control commands the shell sends on <c>/events</c> (agreed contract, see
/// <c>docs/capture-handoff.md</c> step 6 and the wiki's Desktop Shell page).
/// Every request carries an <c>id</c>; the reply is
/// <c>{"type":"ack","id":…,"ok":true}</c> or <c>ok:false</c> with an
/// <c>error</c> code (<c>ephemeral</c>, <c>unknown-session</c>,
/// <c>unknown-candidate</c>, <c>bad-request</c>, <c>unsupported</c>) and a
/// human-readable <c>message</c>. The reply goes to the sending client only.
/// <list type="bullet">
/// <item><c>rate</c>: stores the rating through <see cref="RatingStore"/>
/// (which applies <c>RetentionRules.CanPersist</c>), then publishes
/// <c>ClipRating</c> on <c>clip.rating</c> so every client sees it.</item>
/// <item><c>analyze</c>: runs a footage file through the profile and the
/// clipper (<see cref="FootageSessions"/>); the ack carries the session id.</item>
/// <item><c>save</c>: cuts the candidate's window from a live recording
/// (RAM ring, else the stored segments) into <c>clips/</c> and publishes
/// <c>ClipSaved</c>; refused with <c>clips-full</c> at the clips budget, and
/// <c>unsupported</c> for footage files or without live capture.</item>
/// </list>
/// </summary>
internal sealed partial class ControlService(IEventBus bus, SessionDirectory sessions, RatingStore ratings, FootageSessions footage, ILogger logger, CaptureService? capture = null)
{
    private const int MaxIdLength = 128;

    /// <summary>Handles one text message from a client; returns the reply JSON.</summary>
    public async ValueTask<string?> HandleAsync(string text, CancellationToken ct)
    {
        _ = ct;
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            return Ack(null, false, "bad-request", "Not JSON.");
        }

        using (doc)
        {
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Ack(null, false, "bad-request", "Expected a JSON object.");
            }

            string? id = Str(root, "id");
            if (id is null || id.Length > MaxIdLength)
            {
                return Ack(null, false, "bad-request", "Every command needs an \"id\" (at most 128 characters).");
            }

            return Str(root, "type") switch
            {
                "rate" => Rate(id, root),
                "analyze" => Analyze(id, root),
                "save" => await SaveAsync(id, root).ConfigureAwait(false),
                "preview" => await PreviewAsync(id, root).ConfigureAwait(false),
                "history" => History(id),
                "storage" => Storage(id),
                "storage-delete" => await StorageDeleteAsync(id, root).ConfigureAwait(false),
                "storage-keep" => StorageKeep(id, root),
                string other => Ack(id, false, "bad-request", $"Unknown command \"{other}\"."),
                null => Ack(id, false, "bad-request", "Missing \"type\"."),
            };
        }
    }

    private string Rate(string id, JsonElement root)
    {
        string? sessionId = Str(root, "sessionId");
        string? candidateId = Str(root, "candidateId");
        if (sessionId is null || candidateId is null || !root.TryGetProperty("stars", out JsonElement s) || !s.TryGetInt32(out int stars) || stars is < 0 or > 5)
        {
            return Ack(id, false, "bad-request", "rate needs \"sessionId\", \"candidateId\" and \"stars\" (0 to 5; 0 clears).");
        }

        SessionStarted? session = sessions.Session(sessionId);
        if (session is null)
        {
            return Ack(id, false, "unknown-session", $"The recorder has not seen session '{sessionId}'.");
        }

        if (!RetentionRules.CanPersist(session))
        {
            return Ack(id, false, "ephemeral", "This session is ephemeral (for example an official-player video): nothing from it is stored, ratings included.");
        }

        ClipCandidate? candidate = sessions.Candidate(sessionId, candidateId);
        if (candidate is null)
        {
            return Ack(id, false, "unknown-candidate", $"Session '{sessionId}' has no candidate '{candidateId}'.");
        }

        var rating = new ClipRating { SessionId = sessionId, CandidateId = candidateId, Stars = (uint)stars };
        RatingWriteResult result;
        try
        {
            result = ratings.Append(session, rating, candidate, "shell");
        }
        catch (IOException ex)
        {
            LogRatingFailed(logger, sessionId, candidateId, ex.Message);
            return Ack(id, false, "bad-request", $"The rating could not be written: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            LogRatingFailed(logger, sessionId, candidateId, ex.Message);
            return Ack(id, false, "bad-request", $"The rating could not be written: {ex.Message}");
        }

        if (!result.Written)
        {
            return Ack(id, false, "bad-request", result.Reason ?? "The rating was refused.");
        }

        // Stored: tell every client (the library shows it as stored).
        Envelope e = new EnvelopeFactory(SystemClock.Instance, sessionId, "shell").Create(Topics.ClipRating);
        e.ClipRating = rating;
        bus.Publish(e);
        LogRated(logger, sessionId, candidateId, stars);
        if (stars > 0 && session.SourceKind == SourceKind.LiveGame)
        {
            capture?.KeepRated(sessionId, candidateId); // a rated moment keeps its clip; unrated ones leave the cache
        }
        return Ack(id, true, null, null, stored: true);
    }

    private async Task<string> SaveAsync(string id, JsonElement root)
    {
        string? sessionId = Str(root, "sessionId");
        string? candidateId = Str(root, "candidateId");
        if (sessionId is null || candidateId is null)
        {
            return Ack(id, false, "bad-request", "save needs \"sessionId\" and \"candidateId\".");
        }

        if (capture is null)
        {
            return Ack(id, false, "unsupported", "Saving clips needs live capture in the recorder, which is off.");
        }

        SaveResult r = await capture.SaveAsync(sessionId, candidateId).ConfigureAwait(false);
        return r.Path is null ? Ack(id, false, r.Error, r.Message) : Ack(id, true, null, null, path: r.Path);
    }

    private async Task<string> PreviewAsync(string id, JsonElement root)
    {
        string? sessionId = Str(root, "sessionId");
        string? candidateId = Str(root, "candidateId");
        if (sessionId is null || candidateId is null)
        {
            return Ack(id, false, "bad-request", "preview needs \"sessionId\" and \"candidateId\".");
        }

        if (capture is null)
        {
            return Ack(id, false, "unsupported", "Previews need live capture in the recorder, which is off.");
        }

        SaveResult r = await capture.PreviewAsync(sessionId, candidateId).ConfigureAwait(false);
        return r.Path is null ? Ack(id, false, r.Error, r.Message) : Ack(id, true, null, null, path: r.Path);
    }

    /// <summary>Recorded sessions' own envelopes (oldest first), for the shell to rebuild its library after a restart.</summary>
    private string History(string id)
    {
        if (capture is null)
        {
            return Ack(id, true, null, null, data: new System.Text.Json.Nodes.JsonArray());
        }

        var lines = new System.Text.Json.Nodes.JsonArray();
        foreach (string line in capture.HistoryLines())
        {
            try
            {
                lines.Add(System.Text.Json.Nodes.JsonNode.Parse(line));
            }
            catch (JsonException)
            {
                // a torn line: skipped
            }
        }

        return Ack(id, true, null, null, data: lines);
    }

    private string Storage(string id) =>
        capture is null
            ? Ack(id, false, "unsupported", "The store needs live capture in the recorder, which is off.")
            : Ack(id, true, null, null, data: capture.Listing());

    private async Task<string> StorageDeleteAsync(string id, JsonElement root)
    {
        string? kind = Str(root, "kind");
        if (capture is null || kind is null)
        {
            return Ack(id, false, capture is null ? "unsupported" : "bad-request", capture is null ? "The store needs live capture in the recorder, which is off." : "storage-delete needs \"kind\": session, clip or cache.");
        }

        string? refused = await capture.DeleteAsync(kind, Str(root, "sessionId"), Str(root, "path")).ConfigureAwait(false);
        string what = Str(root, "sessionId") ?? Str(root, "path") ?? "all";
        if (refused is null)
        {
            LogDeleted(logger, kind, what);
        }

        return refused is null ? Ack(id, true, null, null) : Ack(id, false, "bad-request", refused);
    }

    private string StorageKeep(string id, JsonElement root)
    {
        string? sessionId = Str(root, "sessionId");
        if (capture is null || sessionId is null || !root.TryGetProperty("keep", out JsonElement k) || k.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return Ack(id, false, capture is null ? "unsupported" : "bad-request", "storage-keep needs \"sessionId\" and \"keep\" (true or false).");
        }

        return capture.SetKeep(sessionId, k.GetBoolean()) ? Ack(id, true, null, null) : Ack(id, false, "unknown-session", $"The store has no session '{sessionId}'.");
    }

    private string Analyze(string id, JsonElement root)
    {
        string? path = Str(root, "path");
        if (path is null)
        {
            return Ack(id, false, "bad-request", "analyze needs \"path\", an absolute path to a video file.");
        }

        Retention retention = Str(root, "retention") switch
        {
            null or "owned" => Retention.Owned,
            "permissioned" => Retention.Permissioned,
            "open-license" => Retention.OpenLicense,
            _ => Retention.Unspecified,
        };
        if (retention == Retention.Unspecified)
        {
            return Ack(id, false, "bad-request", "\"retention\" is owned, permissioned or open-license.");
        }

        (string? sessionId, string? error, string? message) = footage.Start(new FootageRequest(path, Str(root, "profile"), retention, Str(root, "permissionRef") ?? ""));
        return sessionId is null ? Ack(id, false, error, message) : Ack(id, true, null, null, sessionId: sessionId);
    }

    private static string? Str(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;

    private static string Ack(string? id, bool ok, string? error, string? message, bool? stored = null, string? sessionId = null, string? path = null, System.Text.Json.Nodes.JsonNode? data = null) =>
        JsonSerializer.Serialize(new AckMessage("ack", id, ok, error, message, stored, sessionId, path, data), AckJson.Default.AckMessage);

    [LoggerMessage(Level = LogLevel.Information, Message = "Storage: deleted {Kind} {What}")]
    private static partial void LogDeleted(ILogger logger, string kind, string what);

    [LoggerMessage(Level = LogLevel.Information, Message = "Rated {Candidate} in {Session}: {Stars} stars")]
    private static partial void LogRated(ILogger logger, string session, string candidate, int stars);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rating {Candidate} in {Session} failed: {Reason}")]
    private static partial void LogRatingFailed(ILogger logger, string session, string candidate, string reason);
}

/// <summary>The reply to a control command.</summary>
internal sealed record AckMessage(string Type, string? Id, bool Ok, string? Error, string? Message, bool? Stored, string? SessionId, string? Path, System.Text.Json.Nodes.JsonNode? Data = null);

[System.Text.Json.Serialization.JsonSourceGenerationOptions(
    PropertyNamingPolicy = System.Text.Json.Serialization.JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
[System.Text.Json.Serialization.JsonSerializable(typeof(AckMessage))]
internal sealed partial class AckJson : System.Text.Json.Serialization.JsonSerializerContext;
