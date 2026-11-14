using Libation.Mobile.Audible;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Libation.Mobile.Core.Tests;

public sealed class AudibleOAuthFlowTests
{
    [Fact]
    public void CreateIndiaChallenge_UsesCurrentAndroidWidevineAuthorizationShape()
    {
        var flow = AudibleOAuthFlow.Create("in", TestDeviceInfo());
        var query = System.Web.HttpUtility.ParseQueryString(flow.AuthorizationUri.Query);

        Assert.Equal("https", flow.AuthorizationUri.Scheme);
        Assert.Equal("www.amazon.in", flow.AuthorizationUri.Host);
        Assert.Equal("/ap/signin", flow.AuthorizationUri.AbsolutePath);
        Assert.Equal("https://www.audible.in/ap/maplanding", flow.CallbackUri.AbsoluteUri);
        Assert.Equal(flow.CallbackUri.AbsoluteUri, query["openid.return_to"]);
        Assert.Equal("code", query["openid.oa2.response_type"]);
        Assert.Equal("S256", query["openid.oa2.code_challenge_method"]);
        Assert.Equal(flow.CodeChallenge, query["openid.oa2.code_challenge"]);
        Assert.Equal("amzn_audible_android_aui_in", query["openid.assoc_handle"]);
        Assert.Equal("amzn_audible_android_aui_v2_dark_in", query["pageId"]);
        Assert.Equal("AJO3FBRUE6J4S", query["marketPlaceId"]);
        Assert.Equal("1", query["disableLoginPrepopulate"]);
        Assert.Null(query["forceMobileLayout"]);
        Assert.StartsWith("device:", query["openid.oa2.client_id"]);
        Assert.Equal(20, flow.DeviceSerial.Length);
        Assert.All(flow.DeviceSerial, character => Assert.True(char.IsAsciiDigit(character) || character is >= 'a' and <= 'f'));
        Assert.Equal(AudibleOAuthFlow.AndroidDeviceType, DecodeClientDeviceType(query["openid.oa2.client_id"]!));
        Assert.False(string.IsNullOrWhiteSpace(flow.FrcValue));
        Assert.True(Convert.FromBase64String(flow.FrcValue).Length > 32);
    }

    [Fact]
    public void Create_GeneratesIndependentPkceDeviceAndRegistrationContextValues()
    {
        var first = AudibleOAuthFlow.Create("in", TestDeviceInfo());
        var second = AudibleOAuthFlow.Create("in", TestDeviceInfo());

        Assert.NotEqual(first.CodeVerifier, second.CodeVerifier);
        Assert.NotEqual(first.DeviceSerial, second.DeviceSerial);
        Assert.NotEqual(first.FrcValue, second.FrcValue);
        Assert.Equal(43, first.CodeVerifier.Length);
        Assert.Equal(43, first.CodeChallenge.Length);
        Assert.Equal(20, first.DeviceSerial.Length);
    }

    [Theory]
    [InlineData("us", "www.audible.com", "AF2M0KC94RCEA")]
    [InlineData("uk", "www.audible.co.uk", "A2I9A3Q2GNFNGQ")]
    [InlineData("in", "www.audible.in", "AJO3FBRUE6J4S")]
    public void Create_UsesMarketplaceSpecificAudibleCallback(string market, string host, string marketplaceId)
    {
        var flow = AudibleOAuthFlow.Create(market, TestDeviceInfo());
        var query = System.Web.HttpUtility.ParseQueryString(flow.AuthorizationUri.Query);

        Assert.Equal(host, flow.CallbackUri.Host);
        Assert.Equal(marketplaceId, query["marketPlaceId"]);
        Assert.True(AudibleOAuthFlow.IsMarketplaceCallback(market, flow.CallbackUri));
    }

    [Theory]
    [InlineData("https://www.audible.in/ap/maplanding?openid.oa2.authorization_code=openid-code", "openid-code")]
    [InlineData("https://www.audible.in/ap/maplanding?code=standard-code", "standard-code")]
    public void ReadAuthorizationCode_AcceptsBothMarketplaceCallbackFormats(string callback, string expected)
    {
        var uri = new Uri(callback);
        Assert.True(AudibleOAuthFlow.HasAuthorizationResponse(uri));
        Assert.Equal(expected, AudibleOAuthFlow.ReadAuthorizationCode(uri));
    }

    [Fact]
    public void ReadAuthorizationCode_ReportsMarketplaceOAuthError()
    {
        var uri = new Uri("https://www.audible.in/ap/maplanding?error=access_denied&error_description=Account+not+eligible");
        var error = Assert.Throws<InvalidOperationException>(() => AudibleOAuthFlow.ReadAuthorizationCode(uri));
        Assert.Contains("access_denied", error.Message);
        Assert.Contains("Account not eligible", error.Message);
    }

    [Fact]
    public void Create_RejectsUnknownMarketplace()
    {
        Assert.Throws<ArgumentException>(() => AudibleOAuthFlow.Create("xx", TestDeviceInfo()));
    }

    [Fact]
    public void Create_KeepsHostMetadataButUsesCanonicalAudibleAndroidRegistrationProfile()
    {
        var info = TestDeviceInfo() with
        {
            Manufacturer = "Xiaomi",
            Brand = "Redmi",
            Model = "Redmi 9i",
            Device = "dandelion",
            Product = "dandelion_in",
            BuildFingerprint = "redmi/dandelion_in/dandelion:10/QKQ1.200419.002/V12.0.5.0.QCPINXM:user/release-keys",
            OsRelease = "10",
            SdkVersion = "29",
            DeviceName = "Xiaomi Redmi 9i",
            DeviceLanguage = "en-IN",
            ScreenWidthPixels = 720,
            ScreenHeightPixels = 1600
        };

        var flow = AudibleOAuthFlow.Create("in", info);

        // Physical host facts remain available to the app, but are deliberately
        // not mixed into Audible's virtual CurrentAndroid registration profile.
        Assert.Equal(info, flow.DeviceInfo);
        Assert.Equal(AudibleOAuthFlow.AndroidDeviceModel, AudibleOAuthFlow.AndroidDeviceProduct);
        Assert.Contains("Android 14", AudibleOAuthFlow.AndroidUserAgent);
        Assert.Contains("sdk_gphone64_x86_64", AudibleOAuthFlow.AndroidDeviceModel);
        Assert.Equal(20, flow.DeviceSerial.Length);
    }

    [Fact]
    public void Create_EncodesCanonicalCurrentAndroidProfileInFrcContext()
    {
        var info = TestDeviceInfo() with
        {
            Manufacturer = "Xiaomi",
            Brand = "Redmi",
            Model = "Redmi 9i",
            Device = "dandelion",
            Product = "dandelion_in",
            BuildFingerprint = "redmi/dandelion_in/dandelion:10/test:user/release-keys",
            OsRelease = "10",
            SdkVersion = "29",
            DeviceName = "Xiaomi Redmi 9i",
            DeviceLanguage = "hi",
            ScreenWidthPixels = 720,
            ScreenHeightPixels = 1600
        };
        // Pin these values to AudibleApi's CurrentAndroid reference profile.
        // Comparing only the generated FRC fields with the same app constants
        // would not catch an incorrect virtual-device fingerprint.
        Assert.Equal(
            @"google\/raven\/raven:14\/UPB5.230623.003\/10615560:user\/release-keys",
            AudibleOAuthFlow.AndroidOsVersion);
        Assert.Equal(
            @"ranchu\/Google\/sdk_gphone64_x86_64",
            AudibleOAuthFlow.AndroidFrcDeviceName);

        var flow = AudibleOAuthFlow.Create("in", info);
        var bytes = Convert.FromBase64String(flow.FrcValue);
        var iv = bytes.AsSpan(9, 16).ToArray();
        var cipherText = bytes.AsSpan(25).ToArray();
        var key = Rfc2898DeriveBytes.Pbkdf2(
            flow.DeviceSerial.AsSpan(), "AES/CBC/PKCS7Padding"u8,
            1000, HashAlgorithmName.SHA1, 16);

        byte[] plain;
        using (var aes = Aes.Create())
        {
            aes.Key = key;
            plain = aes.DecryptCbc(cipherText, iv, PaddingMode.PKCS7);
        }
        CryptographicOperations.ZeroMemory(key);

        using var compressed = new MemoryStream(plain);
        using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
        using var json = JsonDocument.Parse(gzip);
        var root = json.RootElement;

        Assert.Equal("com.audible.application", root.GetProperty("ApplicationName").GetString());
        Assert.Equal(AudibleOAuthFlow.AndroidOsVersion, root.GetProperty("DeviceOSVersion").GetString());
        Assert.Equal(AudibleOAuthFlow.AndroidFrcDeviceName, root.GetProperty("DeviceName").GetString());
        Assert.Equal("1080", root.GetProperty("ScreenWidthPixels").GetString());
        Assert.Equal("2400", root.GetProperty("ScreenHeightPixels").GetString());
        Assert.Equal(AudibleApi.Localization.Get("in").Language, root.GetProperty("DeviceLanguage").GetString());
        Assert.Equal("T-Mobile", root.GetProperty("Carrier").GetString());
        Assert.Equal(flow.DeviceSerial, root.GetProperty("FirstPartyDeviceId").GetString());
        Assert.Equal(flow.DeviceSerial, root.GetProperty("ThirdPartyDeviceId").GetString());
        Assert.True(root.TryGetProperty("IpAddress", out _));
    }

    [Fact]
    public void Create_RejectsIncompleteOrImpossibleHostMetadata()
    {
        var info = TestDeviceInfo() with { ScreenWidthPixels = 0 };

        Assert.Throws<ArgumentOutOfRangeException>(() => AudibleOAuthFlow.Create("in", info));
    }

    private static AudibleAndroidDeviceInfo TestDeviceInfo() => new(
        "Test Manufacturer", "Test Brand", "Test Model", "test-device", "test-product",
        "test/test/test:14/UP1A.1/test:user/release-keys", "14", "34",
        "Test Android Device", "en-IN", 1080, 2400);

    private static string DecodeClientDeviceType(string clientId)
    {
        var hex = clientId["device:".Length..];
        var bytes = Convert.FromHexString(hex);
        var serialAndType = System.Text.Encoding.UTF8.GetString(bytes);
        return serialAndType[(serialAndType.IndexOf('#') + 1)..];
    }
}
