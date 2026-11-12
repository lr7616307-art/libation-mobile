using Libation.Mobile.Audible;
using Xunit;

namespace Libation.Mobile.Core.Tests;

public sealed class AudibleIdentitySnapshotSerializerTests
{
    [Fact]
    public void SerializeAndDeserialize_RoundTripsCompleteSnapshot()
    {
        var expected = CreateSnapshot();

        var json = AudibleIdentitySnapshotSerializer.Serialize(expected);
        var actual = AudibleIdentitySnapshotSerializer.Deserialize(json);

        Assert.Equal(expected.SchemaVersion, actual.SchemaVersion);
        Assert.Equal(expected.Provider, actual.Provider);
        Assert.Equal(expected.AudibleApiVersion, actual.AudibleApiVersion);
        Assert.Equal(expected.RegistrationProfileId, actual.RegistrationProfileId);
        Assert.Equal(expected.LocaleName, actual.LocaleName);
        Assert.Equal(expected.AccessToken, actual.AccessToken);
        Assert.Equal(expected.AccessTokenExpiresAtUtc, actual.AccessTokenExpiresAtUtc);
        Assert.Equal(expected.RefreshToken, actual.RefreshToken);
        Assert.Equal(expected.PrivateKey, actual.PrivateKey);
        Assert.Equal(expected.AdpToken, actual.AdpToken);
        Assert.Equal(expected.Cookies, actual.Cookies);
        Assert.Equal(expected.DeviceSerialNumber, actual.DeviceSerialNumber);
        Assert.Equal(expected.DeviceType, actual.DeviceType);
        Assert.Equal(expected.AmazonAccountId, actual.AmazonAccountId);
        Assert.Equal(expected.DeviceName, actual.DeviceName);
        Assert.Equal(expected.StoreAuthenticationCookie, actual.StoreAuthenticationCookie);
        Assert.Equal(expected.Cookies, actual.Cookies);
        Assert.Equal(expected.AccessTokenExpiresAtUtc, actual.AccessTokenExpiresAtUtc);
    }

    [Fact]
    public void Serialize_RejectsObsoleteRegistrationProfile()
    {
        var snapshot = CreateSnapshot() with
        {
            RegistrationProfileId = "audible-iphone-3.56.2-ios-15.0.0-35602678"
        };

        Assert.Throws<InvalidDataException>(
            () => AudibleIdentitySnapshotSerializer.Serialize(snapshot));
    }

    [Fact]
    public void Deserialize_PreservesDuplicateCookieNamesAndOrder()
    {
        var snapshot = CreateSnapshot() with
        {
            Cookies =
            [
                new AudibleCookieSnapshot { Name = "session", Value = "first" },
                new AudibleCookieSnapshot { Name = "other", Value = "middle" },
                new AudibleCookieSnapshot { Name = "session", Value = "second" }
            ]
        };

        var actual = AudibleIdentitySnapshotSerializer.Deserialize(
            AudibleIdentitySnapshotSerializer.Serialize(snapshot));

        Assert.Equal(new[] { "first", "middle", "second" }, actual.Cookies.Select(x => x.Value));
        Assert.Equal(new[] { "session", "other", "session" }, actual.Cookies.Select(x => x.Name));
    }

    [Fact]
    public void Deserialize_RejectsUnsupportedSchemaVersion()
    {
        var json = AudibleIdentitySnapshotSerializer.Serialize(CreateSnapshot())
            .Replace("\"schemaVersion\":2", "\"schemaVersion\":99");

        Assert.Throws<InvalidDataException>(() => AudibleIdentitySnapshotSerializer.Deserialize(json));
    }

    [Fact]
    public void Deserialize_RejectsUnknownFields()
    {
        var json = AudibleIdentitySnapshotSerializer.Serialize(CreateSnapshot())
            .Replace("\"provider\":", "\"unexpected\":true,\"provider\":");

        Assert.Throws<InvalidDataException>(() => AudibleIdentitySnapshotSerializer.Deserialize(json));
    }

    [Fact]
    public void Serialize_RejectsMalformedTokenPrefix()
    {
        var snapshot = CreateSnapshot() with { RefreshToken = "not-a-refresh-token" };

        Assert.Throws<InvalidDataException>(() => AudibleIdentitySnapshotSerializer.Serialize(snapshot));
    }

    [Fact]
    public void Serialize_RejectsMissingPrivateKey()
    {
        var snapshot = CreateSnapshot() with { PrivateKey = " " };

        Assert.Throws<InvalidDataException>(() => AudibleIdentitySnapshotSerializer.Serialize(snapshot));
    }

    [Fact]
    public void Serialize_RejectsPayloadThatExceedsLimitByUtf8Bytes()
    {
        var snapshot = CreateSnapshot() with
        {
            Cookies =
            [
                new AudibleCookieSnapshot
                {
                    Name = "large",
                    Value = new string('é', AudibleIdentitySnapshot.MaximumSerializedBytes)
                }
            ]
        };

        Assert.Throws<InvalidDataException>(
            () => AudibleIdentitySnapshotSerializer.Serialize(snapshot));
    }

    [Fact]
    public void Serialize_AcceptsPayloadAtMaximumSerializedByteLimit()
    {
        var template = CreateSnapshot();
        var low = 0;
        var high = AudibleIdentitySnapshot.MaximumSerializedBytes;

        while (low < high)
        {
            var candidateLength = low + (high - low + 1) / 2;
            var candidate = template with
            {
                Cookies =
                [
                    new AudibleCookieSnapshot
                    {
                        Name = "boundary",
                        Value = new string('a', candidateLength)
                    }
                ]
            };

            try
            {
                _ = AudibleIdentitySnapshotSerializer.Serialize(candidate);
                low = candidateLength;
            }
            catch (InvalidDataException)
            {
                high = candidateLength - 1;
            }
        }

        var accepted = template with
        {
            Cookies =
            [
                new AudibleCookieSnapshot { Name = "boundary", Value = new string('a', low) }
            ]
        };
        var serialized = AudibleIdentitySnapshotSerializer.Serialize(accepted);

        Assert.Equal(
            AudibleIdentitySnapshot.MaximumSerializedBytes,
            System.Text.Encoding.UTF8.GetByteCount(serialized));

        var rejected = accepted with
        {
            Cookies =
            [
                new AudibleCookieSnapshot { Name = "boundary", Value = new string('a', low + 1) }
            ]
        };
        Assert.Throws<InvalidDataException>(
            () => AudibleIdentitySnapshotSerializer.Serialize(rejected));
    }

    [Fact]
    public void Deserialize_RejectsOversizedPayload()
    {
        var json = new string('x', AudibleIdentitySnapshot.MaximumSerializedBytes + 1);

        Assert.Throws<InvalidDataException>(() => AudibleIdentitySnapshotSerializer.Deserialize(json));
    }

    private static AudibleIdentitySnapshot CreateSnapshot() => new()
    {
        SchemaVersion = AudibleIdentitySnapshot.CurrentSchemaVersion,
        Provider = AudibleIdentitySnapshot.ProviderName,
        AudibleApiVersion = AudibleIdentitySnapshot.SupportedAudibleApiVersion,
        RegistrationProfileId = AudibleOAuthFlow.RegistrationProfileId,
        LocaleName = "us",
        AccessToken = "Atna|test-access",
        AccessTokenExpiresAtUtc = new DateTimeOffset(2027, 4, 5, 6, 7, 8, TimeSpan.Zero),
        RefreshToken = "Atnr|test-refresh",
        PrivateKey = "test-private-key-material",
        AdpToken = "{enc:YWJj,key:ZGVm,iv:Z2hp,name:QURQVG9rZW5FbmNyeXB0aW9uS2V5,serial:Mg==}",
        Cookies =
        [
            new AudibleCookieSnapshot { Name = "session", Value = "cookie-value" }
        ],
        DeviceSerialNumber = "device-serial",
        DeviceType = "device-type",
        AmazonAccountId = "account-id",
        DeviceName = "device-name",
        StoreAuthenticationCookie = "store-cookie"
    };
}
