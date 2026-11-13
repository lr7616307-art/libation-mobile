using Libation.Mobile.Audible;
using Xunit;

namespace Libation.Mobile.Core.Tests;

public sealed class AudibleWidevineLicenseDurationTests
{
    [Fact]
    public void FromKeyStatus_ParsesWidevineRemainingDurations()
    {
        var duration = AudibleWidevineLicenseDuration.FromKeyStatus(
            new Dictionary<string, string>
            {
                [AudibleWidevineLicenseDuration.LicenseDurationProperty] = "86400",
                [AudibleWidevineLicenseDuration.PlaybackDurationProperty] = "3600"
            });

        Assert.Equal(86400, duration.LicenseRemainingSeconds);
        Assert.Equal(3600, duration.PlaybackRemainingSeconds);
        Assert.False(duration.IsDefinitelyExpired);
    }

    [Fact]
    public void FromKeyStatus_ReportsExpiredWhenLicenseDurationIsZero()
    {
        var duration = AudibleWidevineLicenseDuration.FromKeyStatus(
            new Dictionary<string, string>
            {
                [AudibleWidevineLicenseDuration.LicenseDurationProperty] = "0",
                [AudibleWidevineLicenseDuration.PlaybackDurationProperty] = "120"
            });

        Assert.True(duration.IsDefinitelyExpired);
    }

    [Fact]
    public void FromKeyStatus_ReportsExpiredWhenPlaybackDurationIsZero()
    {
        var duration = AudibleWidevineLicenseDuration.FromKeyStatus(
            new Dictionary<string, string>
            {
                [AudibleWidevineLicenseDuration.LicenseDurationProperty] = "120",
                [AudibleWidevineLicenseDuration.PlaybackDurationProperty] = "0"
            });

        Assert.True(duration.IsDefinitelyExpired);
    }

    [Fact]
    public void FromKeyStatus_TreatsMissingMalformedAndNegativeFieldsAsUnknown()
    {
        var duration = AudibleWidevineLicenseDuration.FromKeyStatus(
            new Dictionary<string, string>
            {
                [AudibleWidevineLicenseDuration.LicenseDurationProperty] = "not-reported",
                [AudibleWidevineLicenseDuration.PlaybackDurationProperty] = "-1"
            });

        Assert.Null(duration.LicenseRemainingSeconds);
        Assert.Null(duration.PlaybackRemainingSeconds);
        Assert.False(duration.IsDefinitelyExpired);
    }

    [Fact]
    public void FromKeyStatus_WithNoWidevineFieldsDoesNotAssumeExpiry()
    {
        var duration = AudibleWidevineLicenseDuration.FromKeyStatus(
            new Dictionary<string, string>());

        Assert.Null(duration.LicenseRemainingSeconds);
        Assert.Null(duration.PlaybackRemainingSeconds);
        Assert.False(duration.IsDefinitelyExpired);
    }
}
