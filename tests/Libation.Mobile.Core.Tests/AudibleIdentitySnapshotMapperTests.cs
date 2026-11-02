using System.Security.Cryptography;
using AudibleApi;
using Libation.Mobile.Audible;
using Xunit;

namespace Libation.Mobile.Core.Tests;

public sealed class AudibleIdentitySnapshotMapperTests
{
    [Fact]
    public void Restore_ReconstructsValidatedAudibleApiIdentity()
    {
        using var rsa = RSA.Create(2048);
        var pem = rsa.ExportRSAPrivateKeyPem();
        var snapshot = CreateSnapshot(pem);

        var identity = AudibleIdentitySnapshotMapper.Restore(snapshot);

        Assert.True(identity.IsValid);
        Assert.Equal("us", identity.Locale.Name);
        Assert.Equal(snapshot.AccessToken, identity.ExistingAccessToken.Reveal());
        Assert.Equal(snapshot.AccessTokenExpiresAtUtc.UtcDateTime, identity.ExistingAccessToken.Expires);
        Assert.Equal(snapshot.RefreshToken, identity.RefreshToken?.Reveal());
        Assert.Equal(snapshot.PrivateKey, identity.PrivateKey?.Reveal());
        Assert.Equal(snapshot.AdpToken, identity.AdpToken?.Reveal());
        Assert.Equal(new[] { "first", "second" }, identity.Cookies.Select(x => x.Value.Reveal()));
    }

    [Fact]
    public void Restore_RejectsInvalidRsaPrivateKey()
    {
        var snapshot = CreateSnapshot("not-a-private-key");

        Assert.Throws<InvalidDataException>(() => AudibleIdentitySnapshotMapper.Restore(snapshot));
    }

    [Fact]
    public void Restore_RejectsInvalidAdpToken()
    {
        using var rsa = RSA.Create(2048);
        var snapshot = CreateSnapshot(rsa.ExportRSAPrivateKeyPem()) with
        {
            AdpToken = "{enc:abc}{key:def}{iv:ghi}{name:wrong}{serial:Mg==}"
        };

        Assert.Throws<InvalidDataException>(() => AudibleIdentitySnapshotMapper.Restore(snapshot));
    }

    [Fact]
    public void Restore_RejectsUnknownLocale()
    {
        using var rsa = RSA.Create(2048);
        var snapshot = CreateSnapshot(rsa.ExportRSAPrivateKeyPem()) with { LocaleName = "not-a-locale" };

        Assert.Throws<InvalidDataException>(() => AudibleIdentitySnapshotMapper.Restore(snapshot));
    }

    private static AudibleIdentitySnapshot CreateSnapshot(string privateKey) => new()
    {
        SchemaVersion = AudibleIdentitySnapshot.CurrentSchemaVersion,
        Provider = AudibleIdentitySnapshot.ProviderName,
        AudibleApiVersion = AudibleIdentitySnapshot.SupportedAudibleApiVersion,
        LocaleName = "us",
        AccessToken = "Atna|test-access",
        AccessTokenExpiresAtUtc = new DateTimeOffset(2027, 4, 5, 6, 7, 8, TimeSpan.Zero),
        RefreshToken = "Atnr|test-refresh",
        PrivateKey = privateKey,
        AdpToken = "{enc:YWJj}{key:ZGVm}{iv:Z2hp}{name:QURQVG9rZW5FbmNyeXB0aW9uS2V5}{serial:Mg==}",
        Cookies =
        [
            new AudibleCookieSnapshot { Name = "session", Value = "first" },
            new AudibleCookieSnapshot { Name = "session", Value = "second" }
        ],
        DeviceSerialNumber = "device-serial",
        DeviceType = "device-type",
        AmazonAccountId = "account-id",
        DeviceName = "device-name",
        StoreAuthenticationCookie = "store-cookie"
    };
}
