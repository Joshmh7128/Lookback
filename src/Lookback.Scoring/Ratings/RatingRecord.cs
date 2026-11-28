using System.Text.Json.Serialization;

namespace Lookback.Scoring.Ratings;

/// <summary>
/// One line of <c>datasets/&lt;game&gt;/ratings.jsonl</c>: a person's content
/// rating of one clip candidate, with what training needs to learn from it
/// (the candidate's features and heuristic score) and where it came from
/// (retention and permission). Lines are append-only; the last line for a
/// (session, candidate) pair is the current rating, and stars 0 clears it.
/// <para>
/// Holds numbers and ids only. Never frames, audio, signal series or video
/// ids; <see cref="RatingStore"/> refuses any session that may not persist,
/// except the user's own clipper-check ratings, which keep only the rating
/// and the candidate's numbers (<see cref="RatingStore.AppendCheck"/>).
/// </para>
/// </summary>
public sealed record RatingRecord
{
    /// <summary>Format version of the line.</summary>
    public const string CurrentVersion = "lookback.rating/1";

    [JsonPropertyName("v")]
    public string Version { get; init; } = CurrentVersion;

    /// <summary>Root profile id, for example <c>overwatch</c>. Names the folder.</summary>
    public required string Game { get; init; }

    /// <summary>The deepest profile of the candidate, for example <c>overwatch.push</c>.</summary>
    public string Profile { get; init; } = "";

    public required string SessionId { get; init; }

    public required string CandidateId { get; init; }

    /// <summary>1 to 5, or 0 to clear an earlier rating.</summary>
    public required int Stars { get; init; }

    /// <summary>Soft training label, (stars − 1) / 4; null when <see cref="Stars"/> is 0.</summary>
    public double? Label { get; init; }

    /// <summary>The session's retention, kebab-case (<c>owned</c>, <c>permissioned</c>, <c>open-license</c>). Training refuses anything else.</summary>
    public required string Retention { get; init; }

    /// <summary>The session's source kind, kebab-case (<c>live-game</c>, <c>file</c>).</summary>
    public string SourceKind { get; init; } = "";

    /// <summary>Where permission or a licence is recorded, for third-party footage.</summary>
    public string PermissionRef { get; init; } = "";

    public ulong StartQpc { get; init; }

    public ulong EndQpc { get; init; }

    public ulong PeakQpc { get; init; }

    /// <summary>Ticks per second of the QPC values, from the session header.</summary>
    public ulong QpcFrequency { get; init; }

    public float HeuristicScore { get; init; }

    /// <summary>The highlight probability the candidate showed when it was rated.</summary>
    public float HighlightProbability { get; init; }

    /// <summary>Schema id of <see cref="Features"/>, for example <c>clip-features/1</c>.</summary>
    public string FeatureSchema { get; init; } = "";

    /// <summary>Positional against <see cref="FeatureSchema"/>; null for a missing value.</summary>
    public IReadOnlyList<float?> Features { get; init; } = [];

    /// <summary>Wall-clock time of the rating, UTC. Bookkeeping only, never event time.</summary>
    public DateTimeOffset RatedAt { get; init; }

    /// <summary>What recorded the rating: <c>shell</c>, <c>testbench</c> or <c>clipper-check</c>.</summary>
    public string Source { get; init; } = "";

    /// <summary>
    /// Set only on a clipper-check rating of a video the user watched
    /// (<c>user-rating-with-features</c>, <see cref="RatingStore.CheckFileName"/>):
    /// the session was ephemeral and only the rating and the candidate's numbers
    /// were kept. Its times are 0 and its session id is a random run id.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DerivedWrite { get; init; }

    /// <summary>A rated highlight: the long candidate it was picked from. Null for a candidate rated as a whole.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ParentCandidateId { get; init; }

    /// <summary>The official player a clipper-check rating came from (<c>youtube</c>, <c>twitch</c>); null otherwise. Never the video.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Platform { get; init; }

    /// <summary>The soft label for <paramref name="stars"/>: (stars − 1) / 4, or null for 0.</summary>
    public static double? LabelFor(int stars) => stars switch
    {
        0 => null,
        >= 1 and <= 5 => (stars - 1) / 4.0,
        _ => throw new ArgumentOutOfRangeException(nameof(stars), stars, "Stars are 0 to 5."),
    };
}
