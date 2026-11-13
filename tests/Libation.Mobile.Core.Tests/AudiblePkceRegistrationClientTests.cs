using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Libation.Mobile.Audible;
using Xunit;

namespace Libation.Mobile.Core.Tests;

public sealed class AudiblePkceRegistrationClientTests
{
    [Fact]
    public async Task RegisterAsync_SendsLibriSyncIPhoneAdrmProfile()
    {
        var handler = new CapturingHandler("{\"error\":\"test\"}", HttpStatusCode.BadRequest);
        using var http = new HttpClient(handler);
        var challenge = AudibleOAuthFlow.Create("in");
        var client = new AudiblePkceRegistrationClient(http);

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.RegisterAsync(challenge, "authorization-code-test"));

        Assert.Equal("https://api.amazon.in/auth/register", handler.RequestUri?.AbsoluteUri);
        using var body = JsonDocument.Parse(handler.Body!);
        var root = body.RootElement;
        Assert.Equal(".amazon.in", root.GetProperty("cookies").GetProperty("domain").GetString());
        Assert.Equal("Device", root.GetProperty("registration_data").GetProperty("domain").GetString());
        Assert.Equal("4.60", root.GetProperty("registration_data").GetProperty("app_version").GetString());
        Assert.Equal("46000856", root.GetProperty("registration_data").GetProperty("software_version").GetString());
        Assert.Equal("26.2", root.GetProperty("registration_data").GetProperty("os_version").GetString());
        Assert.Equal(AudibleOAuthFlow.IPhoneDeviceType,
            root.GetProperty("registration_data").GetProperty("device_type").GetString());
        Assert.Equal("%FIRST_NAME%%FIRST_NAME_POSSESSIVE_STRING%%DUPE_STRATEGY_1ST%Audible for iPhone",
            root.GetProperty("registration_data").GetProperty("device_name").GetString());
        Assert.Equal("iPhone", root.GetProperty("registration_data").GetProperty("device_model").GetString());
        Assert.Equal("Audible", root.GetProperty("registration_data").GetProperty("app_name").GetString());
        Assert.Equal(challenge.DeviceSerial, root.GetProperty("registration_data").GetProperty("device_serial").GetString());
        Assert.Equal("authorization-code-test", root.GetProperty("auth_data").GetProperty("authorization_code").GetString());

        var auth = root.GetProperty("auth_data");
        Assert.Equal("DeviceLegacy", auth.GetProperty("client_domain").GetString());
        Assert.Equal(challenge.CodeVerifier, auth.GetProperty("code_verifier").GetString());
        var expectedClientId = Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(
            challenge.DeviceSerial + "#" + AudibleOAuthFlow.IPhoneDeviceType)).ToLowerInvariant();
        Assert.Equal(expectedClientId, auth.GetProperty("client_id").GetString());
        Assert.Equal("DeviceLegacy", auth.GetProperty("client_domain").GetString());

        Assert.False(root.TryGetProperty("device_metadata", out _));
        Assert.False(root.TryGetProperty("user_context_map", out _));

        var query = System.Web.HttpUtility.ParseQueryString(challenge.AuthorizationUri.Query);
        Assert.Equal("www.amazon.in", challenge.AuthorizationUri.Host);
        Assert.Equal("www.amazon.in", challenge.CallbackUri.Host);
        Assert.Equal("amzn_audible_ios", query["pageId"]);
        Assert.Equal("amzn_audible_ios_in", query["openid.assoc_handle"]);
        Assert.Equal("true", query["forceMobileLayout"]);
        Assert.Equal("device:" + expectedClientId, query["openid.oa2.client_id"]);
        Assert.Equal(challenge.CallbackUri.AbsoluteUri, query["openid.return_to"]);
    }

    [Fact]
    public async Task RegisterAsync_RejectsPkceVerifierThatDoesNotMatchChallengeBeforeNetworkCall()
    {
        var handler = new CapturingHandler("{}", HttpStatusCode.OK);
        using var http = new HttpClient(handler);
        var challenge = AudibleOAuthFlow.Create("in") with
        {
            CodeChallenge = new string('B', 43)
        };
        var client = new AudiblePkceRegistrationClient(http);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.RegisterAsync(challenge, "authorization-code"));

        Assert.Null(handler.RequestUri);
    }

    [Fact]
    public async Task RegisterAsync_RejectsAuthorizationUrlWithMismatchedPkceChallengeBeforeNetworkCall()
    {
        var handler = new CapturingHandler("{}", HttpStatusCode.OK);
        using var http = new HttpClient(handler);
        var challenge = AudibleOAuthFlow.Create("in");
        var tamperedAuthorizationUri = new Uri(challenge.AuthorizationUri.AbsoluteUri.Replace(
            challenge.CodeChallenge, new string('B', 43), StringComparison.Ordinal));
        var tampered = challenge with { AuthorizationUri = tamperedAuthorizationUri };
        var client = new AudiblePkceRegistrationClient(http);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.RegisterAsync(tampered, "authorization-code"));

        Assert.Null(handler.RequestUri);
    }

    [Fact]
    public async Task RegisterAsync_RejectsCallbackFromDifferentMarketplaceBeforeNetworkCall()
    {
        var handler = new CapturingHandler("{}", HttpStatusCode.OK);
        using var http = new HttpClient(handler);
        var challenge = AudibleOAuthFlow.Create("in") with
        {
            CallbackUri = new Uri("https://www.audible.com/ap/maplanding")
        };
        var client = new AudiblePkceRegistrationClient(http);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.RegisterAsync(challenge, "authorization-code"));

        Assert.Null(handler.RequestUri);
    }

    [Fact]
    public async Task RegisterAsync_MapsCompleteIPhoneRegistrationResponseIntoRestorableIdentity()
    {
        using var rsa = RSA.Create(2048);
        var privateKey = rsa.ExportRSAPrivateKeyPem();
        const string adp = "{enc:YWJj}{key:ZGVm}{iv:Z2hp}{name:QURQVG9rZW5FbmNyeXB0aW9uS2V5}{serial:Mg==}";
        var challenge = AudibleOAuthFlow.Create("in");
        var response = """
        {"response":{"success":{"tokens":{
          "bearer":{"access_token":"Atna|test-access","refresh_token":"Atnr|test-refresh","expires_in":3600},
          "mac_dms":{"device_private_key":__KEY__,"adp_token":__ADP__},
          "website_cookies":[{"Name":"session","Value":"cookie-one"},{"Name":"session","Value":"cookie-two"}],
          "store_authentication_cookie":{"cookie":"store-cookie"}},
          "extensions":{"device_info":{"device_name":"Audible for iPhone","device_serial_number":"__SERIAL__","device_type":"A2CZJZGLK2JJVM"},
          "customer_info":{"user_id":"amazon-user-123"}}}}}
        """.Replace("__KEY__", JsonSerializer.Serialize(privateKey), StringComparison.Ordinal)
          .Replace("__ADP__", JsonSerializer.Serialize(adp), StringComparison.Ordinal)
          .Replace("__SERIAL__", challenge.DeviceSerial, StringComparison.Ordinal);
        var handler = new CapturingHandler(response, HttpStatusCode.OK);
        using var http = new HttpClient(handler);
        var client = new AudiblePkceRegistrationClient(http);

        var snapshot = await client.RegisterAsync(challenge, "authorization-code");

        Assert.Equal(AudibleApi.Localization.Get("in").Name, snapshot.LocaleName);
        Assert.Equal(AudibleOAuthFlow.RegistrationProfileId, snapshot.RegistrationProfileId);
        Assert.Equal("Atna|test-access", snapshot.AccessToken);
        Assert.Equal("Atnr|test-refresh", snapshot.RefreshToken);
        Assert.Equal(privateKey, snapshot.PrivateKey);
        Assert.Equal(adp, snapshot.AdpToken);
        Assert.Equal(challenge.DeviceSerial, snapshot.DeviceSerialNumber);
        Assert.Equal(AudibleOAuthFlow.IPhoneDeviceType, snapshot.DeviceType);
        Assert.Equal("amazon-user-123", snapshot.AmazonAccountId);
        Assert.Equal("Audible for iPhone", snapshot.DeviceName);
        Assert.Equal("store-cookie", snapshot.StoreAuthenticationCookie);
        Assert.Equal(new[] { "cookie-one", "cookie-two" }, snapshot.Cookies.Select(cookie => cookie.Value));

        var restored = AudibleIdentitySnapshotMapper.Restore(AudibleIdentitySnapshotSerializer.Deserialize(
            AudibleIdentitySnapshotSerializer.Serialize(snapshot)));
        Assert.True(restored.IsValid);
        Assert.Equal(snapshot.AccessToken, restored.ExistingAccessToken.Reveal());
        Assert.Equal(snapshot.RefreshToken, restored.RefreshToken?.Reveal());
    }

    [Fact]
    public async Task RegisterAsync_RejectsRegistrationResponseWithNonIPhoneDeviceType()
    {
        var challenge = AudibleOAuthFlow.Create("in");
        var handler = new CapturingHandler(
            """{"response":{"success":{"tokens":{"bearer":{"access_token":"Atna|x","refresh_token":"Atnr|y","expires_in":3600},"mac_dms":{"device_private_key":"private-key","adp_token":"{enc:YWJj}{key:ZGVm}{iv:Z2hp}{name:QURQVG9rZW5FbmNyeXB0aW9uS2V5}{serial:Mg==}"}},"extensions":{"device_info":{"device_type":"A10KISP2GWF0E4","device_serial_number":"__SERIAL__"},"customer_info":{"user_id":"user"}}}}}"""
                .Replace("__SERIAL__", challenge.DeviceSerial, StringComparison.Ordinal),
            HttpStatusCode.OK);
        using var http = new HttpClient(handler);
        var client = new AudiblePkceRegistrationClient(http);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            client.RegisterAsync(challenge, "authorization-code"));

        Assert.Contains("iPhone device type", error.Message);
    }

    [Fact]
    public async Task RegisterAsync_RejectsIncompleteSuccessfulResponse()
    {
        var challenge = AudibleOAuthFlow.Create("in");
        var response = """{"response":{"success":{"tokens":{"bearer":{"access_token":"Atna|x","refresh_token":"Atnr|y","expires_in":3600}},"extensions":{"device_info":{"device_type":"A2CZJZGLK2JJVM","device_serial_number":"__SERIAL__"},"customer_info":{}}}}}"""
            .Replace("__SERIAL__", challenge.DeviceSerial, StringComparison.Ordinal);
        var handler = new CapturingHandler(response, HttpStatusCode.OK);
        using var http = new HttpClient(handler);
        var client = new AudiblePkceRegistrationClient(http);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            client.RegisterAsync(challenge, "authorization-code"));

        Assert.Contains("device credentials", error.Message);
    }

    private sealed class CapturingHandler(string responseBody, HttpStatusCode statusCode) : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(statusCode) { Content = new StringContent(responseBody) };
        }
    }
}
