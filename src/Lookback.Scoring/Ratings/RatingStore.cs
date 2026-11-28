using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Lookback.Contracts;
using Lookback.Contracts.V1;
using Lookback.Scoring.Features;

namespace Lookback.Scoring.Ratings;

/// <summary>What happened to one rating.</summary>
/// <param name="Written">True when the line was appended.</param>
/// <param name="Reason">Why it was refused, when it was.</param>
/// <param name="Record">The line written, when it was.</param>
/// <param name="Path">The file it was written to, when it was.</param>
public sealed record RatingWriteResult(bool Written, string? Reason, RatingRecord? Record, string? Path)
{
    internal static RatingWriteResult Refused(string reason) => new(false, reason, null, null);
}

/// <summary>
/// The user's rating of one clipper-check candidate (a video they only watched),
/// with what training can learn from: its heuristic score and the clipper's named
/// features. Nothing here identifies the video or a moment in it.
/// </summary>
public sealed record CheckRatingInput
{
    /// <summary>Random per player run, <c>check-</c> and 32 hex digits (<see cref="RatingStore.NewRunId"/>); groups the run's ratings for cross-validation.</summary>
    public required string RunId { get; init; }

    /// <summary>The candidate's place in the run (0, 1, 2...), so a re-rating replaces the earlier one.</summary>
    public required int Ordinal { get; init; }

    /// <summary>The profile the clipper ran on, for example <c>cs2.competitive</c>; null or empty for the generic curve.</summary>
    public string? Profile { get; init; }

    /// <summary><c>youtube</c> or <c>twitch</c>.</summary>
    public required string Platform { get; init; }

    /// <summary>1 to 5, or 0 to clear.</summary>
    public required int Stars { get; init; }

    public float HeuristicScore { get; init; }

    /// <summary>The clipper's named features (<c>clipper-features/1</c>); null leaves only the heuristic score.</summary>
    public IReadOnlyDictionary<string, float>? ClipperFeatures { get; init; }

    /// <summary>The session had no audio stream: audio features are stored as missing.</summary>
    public bool VideoOnly { get; init; } = true;
}

/// <summary>Every readable line of a ratings file, and how many were not.</summary>
public sealed record RatingReadResult(IReadOnlyList<RatingRecord> Records, int SkippedLines);

/// <summary>
/// Append-only JSON lines of clip ratings, one file per game at
/// <c>&lt;dataset root&gt;/&lt;game&gt;/ratings.jsonl</c> (git-ignored with the
/// rest of a dataset's outputs). Every write first asks
/// <see cref="RetentionRules.CanPersist"/> about the rated session, so a
/// rating of an official-player or other ephemeral session is refused and
/// never becomes training data. Thread-safe; safe to share across sinks.
/// <para>
/// The one write from an ephemeral session is <see cref="AppendCheck"/>: the
/// user's own rating of a clipper-check candidate, kept apart in
/// <c>&lt;game&gt;/check-ratings.jsonl</c>, holding the stars and the candidate's
/// numbers only (see <see cref="DerivedWriteKind.UserRatingWithFeatures"/>).
/// </para>
/// </summary>
public sealed partial class RatingStore
{
    public const string FileName = "ratings.jsonl";

    /// <summary>The clipper check's ratings of watched videos, beside <see cref="FileName"/>.</summary>
    public const string CheckFileName = "check-ratings.jsonl";

    /// <summary><see cref="RatingRecord.DerivedWrite"/> of a clipper-check rating.</summary>
    public const string CheckDerivedWrite = "user-rating-with-features";

    /// <summary><see cref="RatingRecord.Source"/> of a clipper-check rating.</summary>
    public const string CheckSource = "clipper-check";

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    private static readonly ConcurrentDictionary<string, Lock> FileLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeProvider _time;

    /// <param name="datasetRoot">The <c>datasets</c> folder; one sub-folder per game.</param>
    /// <param name="time">Wall clock for <see cref="RatingRecord.RatedAt"/>.</param>
    public RatingStore(string datasetRoot, TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(datasetRoot);
        DatasetRoot = Path.GetFullPath(datasetRoot);
        _time = time ?? TimeProvider.System;
    }

    public string DatasetRoot { get; }

    /// <summary>The ratings file of <paramref name="game"/> (a root profile id).</summary>
    public string PathFor(string game)
    {
        if (!IsGameId(game))
        {
            throw new ArgumentException($"'{game}' is not a game id (lower-case letters, digits and hyphens).", nameof(game));
        }

        return Path.Combine(DatasetRoot, game, FileName);
    }

    /// <summary>The clipper-check ratings file of <paramref name="game"/>.</summary>
    public string CheckPathFor(string game) => Path.Combine(Path.GetDirectoryName(PathFor(game))!, CheckFileName);

    /// <summary>A fresh random run id for <see cref="CheckRatingInput.RunId"/>.</summary>
    public static string NewRunId() => "check-" + Guid.NewGuid().ToString("N");

    /// <summary>
    /// Persists <paramref name="rating"/> of a candidate from the session
    /// <paramref name="session"/> describes, or refuses it with a reason.
    /// Never throws for a refusal; throws only for an I/O failure.
    /// </summary>
    /// <param name="session">The rated session's header. Retention is checked against it.</param>
    /// <param name="rating">Session id, candidate id and stars (0 clears).</param>
    /// <param name="candidate">The rated candidate, when known: supplies times, scores, features and the profile.</param>
    /// <param name="source">What recorded the rating, for example <c>shell</c>.</param>
    public RatingWriteResult Append(SessionStarted? session, ClipRating rating, ClipCandidate? candidate, string source)
    {
        ArgumentNullException.ThrowIfNull(rating);
        if (session is null)
        {
            return RatingWriteResult.Refused($"Session '{rating.SessionId}' is unknown, so its retention cannot be checked.");
        }

        if (!RetentionRules.CanPersist(session))
        {
            return RatingWriteResult.Refused(
                RetentionRules.Validate(session) ?? "The session is ephemeral: nothing from it is stored, ratings included.");
        }

        if (rating.Stars > 5)
        {
            return RatingWriteResult.Refused($"Stars are 0 to 5, got {rating.Stars}.");
        }

        if (string.IsNullOrWhiteSpace(rating.SessionId) || string.IsNullOrWhiteSpace(rating.CandidateId))
        {
            return RatingWriteResult.Refused("A rating needs a session id and a candidate id.");
        }

        if (candidate is not null && candidate.CandidateId != rating.CandidateId)
        {
            return RatingWriteResult.Refused($"Candidate '{candidate.CandidateId}' is not the rated candidate '{rating.CandidateId}'.");
        }

        string profile = candidate?.GameProfile is { Length: > 0 } p ? p : session.GameProfile;
        string? game = GameOf(profile);
        if (game is null)
        {
            return RatingWriteResult.Refused($"The game of candidate '{rating.CandidateId}' is unknown, so the rating has no dataset to join.");
        }

        RatingRecord record = Build(session, rating, candidate, profile, game, source);
        string path = PathFor(game);
        AppendLine(path, record);
        return new RatingWriteResult(true, null, record, path);
    }

    /// <summary>
    /// Persists the user's rating of a clipper-check candidate from an
    /// official-player session, or refuses it with a reason. The line holds the
    /// stars, the profile, the platform, the heuristic score and the candidate's
    /// <c>clip-features/1</c> vector; its times are 0 and its session is the random
    /// run id, so nothing in it identifies the video or a moment in it. Gated by
    /// <see cref="RetentionRules.ValidateDerived"/> with
    /// <see cref="DerivedWriteKind.UserRatingWithFeatures"/>.
    /// </summary>
    public RatingWriteResult AppendCheck(SessionStarted? session, CheckRatingInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (session is null)
        {
            return RatingWriteResult.Refused("The player session is unknown, so its retention cannot be checked.");
        }

        if (RetentionRules.ValidateDerived(session, DerivedWriteKind.UserRatingWithFeatures, userConfirmed: true) is { } refused)
        {
            return RatingWriteResult.Refused(refused);
        }

        if (session.SourceKind != SourceKind.OfficialPlayer)
        {
            return RatingWriteResult.Refused("Clipper-check ratings come from the official player; footage you own is rated with Append.");
        }

        if (!RunIdPattern().IsMatch(input.RunId))
        {
            return RatingWriteResult.Refused("The run id must be random (check- and 32 hex digits), never anything from the video.");
        }

        if (input.Platform is not ("youtube" or "twitch"))
        {
            return RatingWriteResult.Refused($"Platform must be youtube or twitch, got '{input.Platform}'.");
        }

        if (input.Stars is < 0 or > 5 || input.Ordinal < 0)
        {
            return RatingWriteResult.Refused($"Stars are 0 to 5 and the ordinal is not negative, got {input.Stars} and {input.Ordinal}.");
        }

        string profile = string.IsNullOrWhiteSpace(input.Profile) ? "generic" : input.Profile;
        if (GameOf(profile) is not { } game)
        {
            return RatingWriteResult.Refused($"'{profile}' is not a profile id.");
        }

        float[] features = input.ClipperFeatures is null ? Floor(input.HeuristicScore) : ClipperFeatureMap.FromClipper(input.ClipperFeatures);
        if (input.VideoOnly)
        {
            foreach (string audio in ClipFeatureSchema.AudioDerived)
            {
                features[ClipFeatureSchema.IndexOf(audio)] = float.NaN;
            }
        }

        var record = new RatingRecord
        {
            Game = game,
            Profile = profile,
            SessionId = input.RunId,
            CandidateId = $"c{input.Ordinal}",
            Stars = input.Stars,
            Label = RatingRecord.LabelFor(input.Stars),
            Retention = Kebab(session.Retention.ToString()),
            SourceKind = Kebab(session.SourceKind.ToString()),
            HeuristicScore = input.HeuristicScore,
            FeatureSchema = ClipFeatureSchema.Id,
            Features = [.. features.Select(f => float.IsFinite(f) ? f : (float?)null)],
            RatedAt = _time.GetUtcNow(),
            Source = CheckSource,
            DerivedWrite = CheckDerivedWrite,
            Platform = input.Platform,
        };

        string path = CheckPathFor(game);
        AppendLine(path, record);
        return new RatingWriteResult(true, null, record, path);
    }

    /// <summary>Reads every line of <paramref name="game"/>'s ratings. A missing file is empty; unreadable lines (a torn last write) are skipped and counted.</summary>
    public RatingReadResult Read(string game) => ReadFile(PathFor(game));

    /// <summary>Reads every line of <paramref name="game"/>'s clipper-check ratings, like <see cref="Read"/>.</summary>
    public RatingReadResult ReadChecks(string game) => ReadFile(CheckPathFor(game));

    private static float[] Floor(float heuristic)
    {
        float[] v = ClipFeatureSchema.Empty();
        v[ClipFeatureSchema.IndexOf(ClipFeatureSchema.HeuristicScore)] = heuristic;
        return v;
    }

    private static void AppendLine(string path, RatingRecord record)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(record, Json) + "\n");
        Lock gate = FileLocks.GetOrAdd(path, _ => new Lock());
        lock (gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            stream.Write(bytes); // one write per line, so a reader never sees half of two lines
            stream.Flush(flushToDisk: true);
        }
    }

    private static RatingReadResult ReadFile(string path)
    {
        if (!File.Exists(path))
        {
            return new RatingReadResult([], 0);
        }

        var records = new List<RatingRecord>();
        int skipped = 0;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                RatingRecord? r = JsonSerializer.Deserialize<RatingRecord>(line, Json);
                if (r is null)
                {
                    skipped++;
                }
                else
                {
                    records.Add(r);
                }
            }
            catch (JsonException)
            {
                skipped++;
            }
        }

        return new RatingReadResult(records, skipped);
    }

    /// <summary>The current rating per (session, candidate): the last line wins, and stars 0 removes the pair.</summary>
    public static IReadOnlyList<RatingRecord> Current(IEnumerable<RatingRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        var last = new Dictionary<(string, string), RatingRecord>();
        var order = new List<(string, string)>();
        foreach (RatingRecord r in records)
        {
            var key = (r.SessionId, r.CandidateId);
            if (!last.ContainsKey(key))
            {
                order.Add(key);
            }

            last[key] = r;
        }

        return [.. order.Select(k => last[k]).Where(r => r.Stars > 0)];
    }

    /// <summary>The game (root profile id) of a profile id such as <c>roblox.arsenal</c>; null when there is none.</summary>
    public static string? GameOf(string? profileId)
    {
        if (string.IsNullOrWhiteSpace(profileId))
        {
            return null;
        }

        int dot = profileId.IndexOf('.', StringComparison.Ordinal);
        string game = dot < 0 ? profileId : profileId[..dot];
        return IsGameId(game) ? game : null;
    }

    /// <summary>Lower-case letters, digits and hyphens, starting with a letter or digit: safe as a folder name.</summary>
    public static bool IsGameId(string? game) => game is not null && GameIdPattern().IsMatch(game);

    internal static string Kebab(string enumName) => JsonNamingPolicy.KebabCaseLower.ConvertName(enumName);

    private RatingRecord Build(SessionStarted session, ClipRating rating, ClipCandidate? candidate, string profile, string game, string source)
    {
        int stars = (int)rating.Stars;
        float[]? features = null;
        string schema = "";
        if (candidate is not null)
        {
            if (candidate.Features.Count > 0 && candidate.FeatureSchema.Length > 0)
            {
                features = [.. candidate.Features];
                schema = candidate.FeatureSchema;
            }
            else if (session.QpcFrequency > 0)
            {
                features = ClipFeatureSchema.FromCandidate(candidate, (long)session.QpcFrequency);
                schema = ClipFeatureSchema.Id;
            }
        }

        return new RatingRecord
        {
            Game = game,
            Profile = profile,
            SessionId = rating.SessionId,
            CandidateId = rating.CandidateId,
            Stars = stars,
            Label = RatingRecord.LabelFor(stars),
            Retention = Kebab(session.Retention.ToString()),
            SourceKind = Kebab(session.SourceKind.ToString()),
            PermissionRef = session.PermissionRef,
            StartQpc = candidate?.StartQpc ?? 0,
            EndQpc = candidate?.EndQpc ?? 0,
            PeakQpc = candidate?.PeakQpc ?? 0,
            QpcFrequency = session.QpcFrequency,
            HeuristicScore = candidate?.HeuristicScore ?? 0,
            HighlightProbability = candidate?.HighlightProbability ?? 0,
            FeatureSchema = schema,
            Features = features is null ? [] : [.. features.Select(f => float.IsFinite(f) ? f : (float?)null)],
            RatedAt = _time.GetUtcNow(),
            Source = source,
            ParentCandidateId = candidate?.ParentCandidateId is { Length: > 0 } parent ? parent : null,
        };
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex GameIdPattern();

    [GeneratedRegex("^check-[0-9a-f]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex RunIdPattern();
}
