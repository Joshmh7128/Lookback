using Lookback.Contracts.V1;

namespace Lookback.Contracts;

/// <summary>
/// What a write of data derived from a session would contain. Media, signal
/// frames and events are never a derived write: they are the session's data
/// and follow <see cref="RetentionRules.CanPersist"/>.
/// </summary>
public enum DerivedWriteKind
{
    Unspecified = 0,

    /// <summary>
    /// Numbers in a game profile (HUD region rectangles, rule thresholds)
    /// that the user reviewed and confirmed. Coordinates and thresholds only:
    /// no pixels, audio, signal series, timestamps or anything identifying
    /// the source video.
    /// </summary>
    ConfirmedProfileParameters = 1,

    /// <summary>
    /// The user's own 1–5 rating of a clipper candidate on a video they only
    /// watched (the clipper check), with the candidate's fixed feature vector
    /// and heuristic score. The rating is the user's data; the video is not:
    /// no pixels, audio, signal series, timestamps or anything identifying the
    /// source video. Written only by <c>RatingStore.AppendCheck</c>.
    /// </summary>
    UserRatingWithFeatures = 2,
}

/// <summary>
/// The single place that decides whether a session's data may touch disk.
/// Official-player sources are always ephemeral: they can be analysed live,
/// and nothing from them is persisted, with two exceptions the user grants
/// each time: profile parameters they confirmed, and their own ratings of
/// clipper candidates with the candidates' feature numbers (see <see cref="ValidateDerived"/>).
/// </summary>
public static class RetentionRules
{
    /// <summary>Returns null when <paramref name="session"/> is valid, otherwise the reason it is not.</summary>
    public static string? Validate(SessionStarted session)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (session.Retention == Retention.Unspecified)
        {
            return "Retention must be set explicitly.";
        }

        if (session.SourceKind == SourceKind.OfficialPlayer && session.Retention != Retention.Ephemeral)
        {
            return "Official-player sources must be ephemeral: nothing from them is stored.";
        }

        if (session.Retention is Retention.Permissioned or Retention.OpenLicense
            && string.IsNullOrWhiteSpace(session.PermissionRef))
        {
            return "Permissioned and open-licence footage must reference where the permission or licence is recorded.";
        }

        return null;
    }

    /// <summary>True when data from this session may be written to disk.</summary>
    public static bool CanPersist(SessionStarted session) =>
        Validate(session) is null && session.Retention != Retention.Ephemeral;

    /// <summary>
    /// Returns null when a write of derived values of <paramref name="kind"/>
    /// from <paramref name="session"/> is allowed, otherwise the reason it is not.
    /// The derived writes are <see cref="DerivedWriteKind.ConfirmedProfileParameters"/>,
    /// only after the user confirmed exactly what is written, and
    /// <see cref="DerivedWriteKind.UserRatingWithFeatures"/>, only for a rating the
    /// user gave. Both hold for every session and carry numbers only, so an
    /// ephemeral session never yields its pixels, audio, signal series, times
    /// or the identity of its video.
    /// </summary>
    public static string? ValidateDerived(SessionStarted session, DerivedWriteKind kind, bool userConfirmed)
    {
        string? invalid = Validate(session);
        if (invalid is not null)
        {
            return invalid;
        }

        if (kind is not (DerivedWriteKind.ConfirmedProfileParameters or DerivedWriteKind.UserRatingWithFeatures))
        {
            return "The only derived writes are confirmed profile parameters and the user's own ratings with candidate features; anything else follows the session's retention.";
        }

        if (!userConfirmed)
        {
            return kind == DerivedWriteKind.ConfirmedProfileParameters
                ? "Profile parameters are written only after the user confirms them."
                : "A rating is written only when the user gives it.";
        }

        return null;
    }
}
