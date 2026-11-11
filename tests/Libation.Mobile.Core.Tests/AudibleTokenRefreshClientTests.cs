using System.Net;
using System.Web;
using AudibleApi;
using AudibleApi.Authorization;
using Dinah.Core;
using Dinah.Core.Security;
using Libation.Mobile.Audible;
using Xunit;

namespace Libation.Mobile.Core.Tests;

public sealed class AudibleTokenRefreshClientTests
{
    [Fact]
    public async Task RefreshAsync_UsesCurrentLibriSyncRefreshPayloadAndMapsRotatedTokens()
    {
        var handler = new CapturingHandler(
            """{"access_token":"Atna|refreshed","refresh_token":"Atnr|rotated","expires_in":3600,"token_type":"Bearer"}""",
            HttpStatusCode.OK);
        using var http = new HttpClient(handler);
        var now = new DateTime(2026, 11, 11, 12, 0, 0, DateTimeKind.Utc);
        var client = new AudibleTokenRefreshClient(http, new FixedClock(now));

        var result = await client.RefreshAsync(
            Localization.Get("in"),
            new RefreshToken(new SecretString("Atnr|original")));

        Assert.Equal("https://api.amazon.in/auth/token", handler.RequestUri?.AbsoluteUri);
        Assert.Equal("api.amazon.in", handler.IdentityAuthDomain);
        var form = HttpUtility.ParseQueryString(handler.Body!);
        Assert.Equal("Audible", form["app_name"]);
        Assert.Equal("4.60", form["app_version"]);
        Assert.Equal("Atnr|original", form["source_token"]);
        Assert.Equal("refresh_token", form["source_token_type"]);
        Assert.Equal("access_token", form["requested_token_type"]);
        Assert.Equal("Atna|refreshed", result.AccessToken.Reveal());
        Assert.Equal(now.AddHours(1), result.AccessToken.Expires);
        Assert.Equal("Atnr|rotated", result.RefreshToken?.Reveal());
    }

    [Fact]
    public async Task RefreshAsync_PreservesCurrentRefreshTokenWhenAmazonDoesNotRotateIt()
    {
        var handler = new CapturingHandler(
            """{"access_token":"Atna|refreshed","expires_in":"1800","token_type":"Bearer"}""",
            HttpStatusCode.OK);
        using var http = new HttpClient(handler);
        var now = new DateTime(2026, 11, 11, 12, 0, 0, DateTimeKind.Utc);
        var client = new AudibleTokenRefreshClient(http, new FixedClock(now));

        var result = await client.RefreshAsync(
            Localization.Get("us"),
            new RefreshToken(new SecretString("Atnr|original")));

        Assert.Equal("Atna|refreshed", result.AccessToken.Reveal());
        Assert.Equal(now.AddMinutes(30), result.AccessToken.Expires);
        Assert.Null(result.RefreshToken);
    }

    [Fact]
    public async Task RefreshAsync_RejectsMalformedSuccessfulResponse()
    {
        var handler = new CapturingHandler(
            """{"access_token":"not-an-audible-token","expires_in":3600}""",
            HttpStatusCode.OK);
        using var http = new HttpClient(handler);
        var client = new AudibleTokenRefreshClient(
            http, new FixedClock(new DateTime(2026, 11, 11, 12, 0, 0, DateTimeKind.Utc)));

        await Assert.ThrowsAsync<InvalidDataException>(() => client.RefreshAsync(
            Localization.Get("us"),
            new RefreshToken(new SecretString("Atnr|original"))));
    }

    private sealed class FixedClock(DateTime utcNow) : ISystemDateTime
    {
        public DateTime Now => utcNow.ToLocalTime();
        public DateTime UtcNow => utcNow;
    }

    private sealed class CapturingHandler(string responseBody, HttpStatusCode statusCode) : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }
        public string? Body { get; private set; }
        public string? IdentityAuthDomain { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            IdentityAuthDomain = request.Headers.TryGetValues("x-amzn-identity-auth-domain", out var values)
                ? values.SingleOrDefault()
                : null;
            return new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(responseBody)
            };
        }
    }
}
