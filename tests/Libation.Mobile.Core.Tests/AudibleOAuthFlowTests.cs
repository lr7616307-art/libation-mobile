using Libation.Mobile.Audible;
using Xunit;

namespace Libation.Mobile.Core.Tests;

public sealed class AudibleOAuthFlowTests
{
    [Fact]
    public void CreateIndiaChallenge_UsesLibriSyncIndiaAuthorizationShape()
    {
        var flow = AudibleOAuthFlow.Create("in");
        var query = System.Web.HttpUtility.ParseQueryString(flow.AuthorizationUri.Query);

        Assert.Equal("https", flow.AuthorizationUri.Scheme);
        Assert.Equal("www.amazon.in", flow.AuthorizationUri.Host);
        Assert.Equal("/ap/signin", flow.AuthorizationUri.AbsolutePath);
        Assert.Equal("https://www.amazon.in/ap/maplanding", flow.CallbackUri.AbsoluteUri);
        Assert.Equal("code", query["openid.oa2.response_type"]);
        Assert.Equal("S256", query["openid.oa2.code_challenge_method"]);
        Assert.Equal(flow.CodeChallenge, query["openid.oa2.code_challenge"]);
        Assert.Equal("amzn_audible_ios_in", query["openid.assoc_handle"]);
        Assert.Equal("AJO3FBRUE6J4S", query["marketPlaceId"]);
        Assert.Equal("true", query["forceMobileLayout"]);
        Assert.StartsWith("device:", query["openid.oa2.client_id"]);
        Assert.Equal(40, flow.DeviceSerial.Length);
        Assert.DoesNotContain("openid.oa2.state", flow.AuthorizationUri.Query);
    }

    [Fact]
    public void Create_GeneratesIndependentPkceAndDeviceValues()
    {
        var first = AudibleOAuthFlow.Create("in");
        var second = AudibleOAuthFlow.Create("in");

        Assert.NotEqual(first.CodeVerifier, second.CodeVerifier);
        Assert.NotEqual(first.DeviceSerial, second.DeviceSerial);
        Assert.Equal(43, first.CodeVerifier.Length);
        Assert.Equal(43, first.CodeChallenge.Length);
    }

    [Theory]
    [InlineData("us", "www.amazon.com", "AF2M0KC94RCEA")]
    [InlineData("uk", "www.amazon.co.uk", "A2I9A3Q2GNFNGQ")]
    [InlineData("in", "www.amazon.in", "AJO3FBRUE6J4S")]
    public void Create_UsesMarketplaceSpecificAmazonCallback(string market, string host, string marketplaceId)
    {
        var flow = AudibleOAuthFlow.Create(market);
        var query = System.Web.HttpUtility.ParseQueryString(flow.AuthorizationUri.Query);

        Assert.Equal(host, flow.CallbackUri.Host);
        Assert.Equal(marketplaceId, query["marketPlaceId"]);
    }

    [Fact]
    public void Create_RejectsUnknownMarketplace()
    {
        Assert.Throws<ArgumentException>(() => AudibleOAuthFlow.Create("xx"));
    }
}
