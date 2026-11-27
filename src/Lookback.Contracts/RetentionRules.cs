using Lookback.Contracts.V1;

namespace Lookback.Contracts;

/// <summary>
/// The single place that decides whether a session's data may touch disk.
/// Official-player sources are always ephemeral: they can be analysed live,
/// but nothing derived from them is persisted.
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
}
