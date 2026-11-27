using Lookback.Contracts;
using Lookback.Contracts.V1;

namespace Lookback.Core.Tests;

public class RetentionRulesTests
{
    [Fact]
    public void OfficialPlayerMustBeEphemeral()
    {
        var s = new SessionStarted { SourceKind = SourceKind.OfficialPlayer, Retention = Retention.Owned };
        Assert.NotNull(RetentionRules.Validate(s));
        Assert.False(RetentionRules.CanPersist(s));
    }

    [Fact]
    public void OfficialPlayerEphemeralIsValidButNotPersistable()
    {
        var s = new SessionStarted { SourceKind = SourceKind.OfficialPlayer, Retention = Retention.Ephemeral };
        Assert.Null(RetentionRules.Validate(s));
        Assert.False(RetentionRules.CanPersist(s));
    }

    [Theory]
    [InlineData(Retention.Permissioned)]
    [InlineData(Retention.OpenLicense)]
    public void ThirdPartyFootageNeedsAPermissionReference(Retention retention)
    {
        var s = new SessionStarted { SourceKind = SourceKind.File, Retention = retention };
        Assert.False(RetentionRules.CanPersist(s));

        s.PermissionRef = "permissions/streamer-a.md";
        Assert.True(RetentionRules.CanPersist(s));
    }

    [Fact]
    public void UnspecifiedRetentionIsRejected() =>
        Assert.NotNull(RetentionRules.Validate(new SessionStarted { SourceKind = SourceKind.LiveGame }));

    [Fact]
    public void OwnedLiveGameCanPersist() =>
        Assert.True(RetentionRules.CanPersist(new SessionStarted { SourceKind = SourceKind.LiveGame, Retention = Retention.Owned }));

    [Fact]
    public void OfficialPlayerMayWriteOnlyConfirmedProfileParametersAndTheUsersRatings()
    {
        var s = new SessionStarted { SourceKind = SourceKind.OfficialPlayer, Retention = Retention.Ephemeral };
        Assert.Null(RetentionRules.ValidateDerived(s, DerivedWriteKind.ConfirmedProfileParameters, userConfirmed: true));
        Assert.NotNull(RetentionRules.ValidateDerived(s, DerivedWriteKind.ConfirmedProfileParameters, userConfirmed: false));
        Assert.NotNull(RetentionRules.ValidateDerived(s, DerivedWriteKind.Unspecified, userConfirmed: true));
        Assert.Null(RetentionRules.ValidateDerived(s, DerivedWriteKind.UserRatingWithFeatures, userConfirmed: true));
        Assert.NotNull(RetentionRules.ValidateDerived(s, DerivedWriteKind.UserRatingWithFeatures, userConfirmed: false));
        Assert.False(RetentionRules.CanPersist(s)); // the session's own data still never touches disk
    }

    [Fact]
    public void AnInvalidSessionMayWriteNothingDerived()
    {
        var s = new SessionStarted { SourceKind = SourceKind.OfficialPlayer, Retention = Retention.Owned };
        Assert.NotNull(RetentionRules.ValidateDerived(s, DerivedWriteKind.ConfirmedProfileParameters, userConfirmed: true));
    }
}
