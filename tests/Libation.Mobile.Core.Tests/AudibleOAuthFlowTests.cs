using Libation.Mobile.Audible;
using Xunit;

namespace Libation.Mobile.Core.Tests;

public sealed class AudibleOAuthFlowTests
{
    [Fact]
    public void CreateIndiaChallenge_UsesLibriSyncIPhoneAdrmAuthorizationShape()
    {
        var flow = AudibleOAuthFlow.Create("in");
        var query = System.Web.HttpUtility.ParseQueryString(flow.AuthorizationUri.Query);

        Assert.Equal("https", flow.AuthorizationUri.Scheme);
        Assert.Equal("www.amazon.in", flow.AuthorizationUri.Host);
        Assert.Equal("/ap/signin", flow.AuthorizationUri.AbsolutePath);
        Assert.Equal("https://www.amazon.in/ap/maplanding", flow.CallbackUri.AbsoluteUri);
        Assert.Equal(flow.CallbackUri.AbsoluteUri, query["openid.return_to"]);
        Assert.Equal("code", query["openid.oa2.response_type"]);
        Assert.Equal("S256", query["openid.oa2.code_challenge_method"]);
        Assert.Equal(flow.CodeChallenge, query["openid.oa2.code_challenge"]);
        Assert.Equal("amzn_audible_ios_in", query["openid.assoc_handle"]);
        Assert.Equal("amzn_audible_ios", query["pageId"]);
        Assert.Equal("AJO3FBRUE6J4S", query["marketPlaceId"]);
        Assert.Equal("true", query["forceMobileLayout"]);
        Assert.Null(query["disableLoginPrepopulate"]);
        Assert.StartsWith("device:", query["openid.oa2.client_id"]);
        Assert.Equal(40, flow.DeviceSerial.Length);
        Assert.All(flow.DeviceSerial, character => Assert.True(char.IsAsciiLetterOrDigit(character)));
        Assert.Equal(AudibleOAuthFlow.IPhoneDeviceType, DecodeClientDeviceType(query["openid.oa2.client_id"]!));
        Assert.Equal(string.Empty, flow.FrcValue);
        Assert.Empty(flow.SignInCookies);
    }

    [Fact]
    public void Create_GeneratesIndependentPkceAndIPhoneDeviceSerialValues()
    {
        var first = AudibleOAuthFlow.Create("in");
        var second = AudibleOAuthFlow.Create("in");

        Assert.NotEqual(first.CodeVerifier, second.CodeVerifier);
        Assert.NotEqual(first.DeviceSerial, second.DeviceSerial);
        Assert.Equal(43, first.CodeVerifier.Length);
        Assert.Equal(43, first.CodeChallenge.Length);
        Assert.Equal(40, first.DeviceSerial.Length);
        Assert.All(first.DeviceSerial, character => Assert.True(char.IsAsciiLetterOrDigit(character)));
    }

    [Theory]
    [InlineData("us", "www.amazon.com", "AF2M0KC94RCEA")]
    [InlineData("uk", "www.amazon.co.uk", "A2I9A3Q2GNFNGQ")]
    [InlineData("in", "www.amazon.in", "AJO3FBRUE6J4S")]
    public void Create_UsesMarketplaceSpecificAmazonCallback(string market, string host, string marketplaceId)
    {
        var flow = AudibleOAuthFlow.Create(market);
        var query = System.Web.HttpUtility.ParseQueryString(flow.AuthorizationUri.Query);

        Assert.Equal(host, flow.AuthorizationUri.Host);
        Assert.Equal(host, flow.CallbackUri.Host);
        Assert.Equal(marketplaceId, query["marketPlaceId"]);
        Assert.True(AudibleOAuthFlow.IsMarketplaceCallback(market, flow.CallbackUri));
    }

    [Theory]
    [InlineData("https://www.amazon.in/ap/maplanding?openid.oa2.authorization_code=openid-code", "openid-code")]
    [InlineData("https://www.amazon.in/ap/maplanding?code=standard-code", "standard-code")]
    public void ReadAuthorizationCode_AcceptsBothMarketplaceCallbackFormats(string callback, string expected)
    {
        var uri = new Uri(callback);
        Assert.True(AudibleOAuthFlow.HasAuthorizationResponse(uri));
        Assert.Equal(expected, AudibleOAuthFlow.ReadAuthorizationCode(uri));
    }

    [Fact]
    public void ReadAuthorizationCode_ReportsMarketplaceOAuthError()
    {
        var uri = new Uri("https://www.amazon.in/ap/maplanding?error=access_denied&error_description=Account+not+eligible");
        var error = Assert.Throws<InvalidOperationException>(() => AudibleOAuthFlow.ReadAuthorizationCode(uri));
        Assert.Contains("access_denied", error.Message);
        Assert.Contains("Account not eligible", error.Message);
    }

    [Fact]
    public void Create_RejectsUnknownMarketplace()
    {
        Assert.Throws<ArgumentException>(() => AudibleOAuthFlow.Create("xx"));
    }

    private static string DecodeClientDeviceType(string clientId)
    {
        var hex = clientId["device:".Length..];
        var bytes = Convert.FromHexString(hex);
        var serialAndType = System.Text.Encoding.UTF8.GetString(bytes);
        return serialAndType[(serialAndType.IndexOf('#') + 1)..];
    }
}
