using Libation.Mobile.Audible;
using Xunit;

namespace Libation.Mobile.Core.Tests;

public sealed class AudibleOAuthFlowTests
{
    [Fact]
    public void CreateIndiaChallenge_UsesAudibleMarketplaceCallbackAndAndroidAuthorizationShape()
    {
        var flow = AudibleOAuthFlow.Create("in");
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
        Assert.All(flow.DeviceSerial, c => Assert.Contains(c, "0123456789abcdef"));
        Assert.DoesNotContain("forceMobileLayout", flow.AuthorizationUri.Query);
    }

    [Fact]
    public void Create_GeneratesIndependentPkceAndAndroidDeviceValues()
    {
        var first = AudibleOAuthFlow.Create("in");
        var second = AudibleOAuthFlow.Create("in");

        Assert.NotEqual(first.CodeVerifier, second.CodeVerifier);
        Assert.NotEqual(first.DeviceSerial, second.DeviceSerial);
        Assert.Equal(43, first.CodeVerifier.Length);
        Assert.Equal(43, first.CodeChallenge.Length);
    }

    [Theory]
    [InlineData("us", "www.audible.com", "AF2M0KC94RCEA")]
    [InlineData("uk", "www.audible.co.uk", "A2I9A3Q2GNFNGQ")]
    [InlineData("in", "www.audible.in", "AJO3FBRUE6J4S")]
    public void Create_UsesMarketplaceSpecificAudibleCallback(string market, string host, string marketplaceId)
    {
        var flow = AudibleOAuthFlow.Create(market);
        var query = System.Web.HttpUtility.ParseQueryString(flow.AuthorizationUri.Query);

        Assert.Equal(host, flow.CallbackUri.Host);
        Assert.Equal(marketplaceId, query["marketPlaceId"]);
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
        Assert.Throws<ArgumentException>(() => AudibleOAuthFlow.Create("xx"));
    }
}
