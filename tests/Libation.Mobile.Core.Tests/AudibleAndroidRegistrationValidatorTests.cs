using Libation.Mobile.Audible;
using Xunit;

namespace Libation.Mobile.Core.Tests;

public sealed class AudibleAndroidRegistrationValidatorTests
{
    [Fact]
    public void IsCurrentAndroidRegistration_AcceptsCurrentAndroidProfileAndDeviceType()
    {
        var snapshot = CreateSnapshot(
            AudibleOAuthFlow.RegistrationProfileId,
            AudibleOAuthFlow.AndroidDeviceType);

        Assert.True(AudibleAndroidRegistrationValidator.IsCurrentAndroidRegistration(snapshot));
        AudibleAndroidRegistrationValidator.EnsureCurrentAndroidRegistration(snapshot);
    }

    [Fact]
    public void EnsureCurrentAndroidRegistration_RejectsLegacyIPhoneProfile()
    {
        var snapshot = CreateSnapshot(
            AudibleOAuthFlow.LegacyIPhoneRegistrationProfileId,
            "A2CZJZGLK2JJVM");

        Assert.False(AudibleAndroidRegistrationValidator.IsCurrentAndroidRegistration(snapshot));

        Assert.Throws<AudibleAndroidRegistrationRequiredException>(() =>
            AudibleAndroidRegistrationValidator.EnsureCurrentAndroidRegistration(snapshot));
    }

    [Fact]
    public void EnsureCurrentAndroidRegistration_RejectsProfileIdAndDeviceTypeMismatch()
    {
        var snapshot = CreateSnapshot(
            AudibleOAuthFlow.RegistrationProfileId,
            "A2CZJZGLK2JJVM");

        Assert.Throws<AudibleAndroidRegistrationRequiredException>(() =>
            AudibleAndroidRegistrationValidator.EnsureCurrentAndroidRegistration(snapshot));
    }

    [Fact]
    public void EnsureCurrentAndroidRegistration_RejectsMissingDeviceType()
    {
        var snapshot = CreateSnapshot(
            AudibleOAuthFlow.RegistrationProfileId,
            null);

        Assert.Throws<AudibleAndroidRegistrationRequiredException>(() =>
            AudibleAndroidRegistrationValidator.EnsureCurrentAndroidRegistration(snapshot));
    }

    private static AudibleIdentitySnapshot CreateSnapshot(string profileId, string? deviceType)
        => new()
        {
            SchemaVersion = AudibleIdentitySnapshot.CurrentSchemaVersion,
            Provider = AudibleIdentitySnapshot.ProviderName,
            AudibleApiVersion = AudibleIdentitySnapshot.SupportedAudibleApiVersion,
            RegistrationProfileId = profileId,
            LocaleName = "in",
            AccessToken = "Atna|test-access",
            AccessTokenExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(1),
            RefreshToken = "Atnr|test-refresh",
            PrivateKey = "test-private-key",
            AdpToken = "{enc:YWJj}{key:ZGVm}{iv:Z2hp}{name:QURQVG9rZW5FbmNyeXB0aW9uS2V5}{serial:Mg==}",
            Cookies = [],
            DeviceSerialNumber = "test-device",
            DeviceType = deviceType,
            AmazonAccountId = "test-account",
            DeviceName = "test-device"
        };
}
