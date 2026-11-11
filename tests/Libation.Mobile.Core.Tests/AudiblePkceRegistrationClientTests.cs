using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Libation.Mobile.Audible;
using Xunit;

namespace Libation.Mobile.Core.Tests;

public sealed class AudiblePkceRegistrationClientTests
{
    [Fact]
    public async Task RegisterAsync_SendsAndroidRegistrationContextToAmazonApiHost()
    {
        var handler = new CapturingHandler("{\"error\":\"test\"}", HttpStatusCode.BadRequest);
        using var http = new HttpClient(handler);
        var challenge = AudibleOAuthFlow.Create("in");
        var client = new AudiblePkceRegistrationClient(http);

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.RegisterAsync(challenge, "authorization-code-test", CreateProfile()));

        Assert.Equal("https://api.amazon.in/auth/register", handler.RequestUri?.AbsoluteUri);
        using var body = JsonDocument.Parse(handler.Body!);
        var root = body.RootElement;
        Assert.Equal("www.amazon.in", root.GetProperty("cookies").GetProperty("domain").GetString());
        Assert.Equal("DeviceLegacy", root.GetProperty("registration_data").GetProperty("domain").GetString());
        Assert.Equal("com.audible.application", root.GetProperty("registration_data").GetProperty("app_name").GetString());
        Assert.Equal(challenge.DeviceSerial, root.GetProperty("device_metadata").GetProperty("device_serial").GetString());
        Assert.Equal("android", root.GetProperty("device_metadata").GetProperty("device_os_family").GetString());
        Assert.Equal("true", root.GetProperty("auth_data").GetProperty("use_global_authentication").GetString());
        Assert.Equal(challenge.CodeVerifier, root.GetProperty("auth_data").GetProperty("code_verifier").GetString());
        Assert.True(Convert.FromBase64String(root.GetProperty("user_context_map").GetProperty("frc").GetString()!).Length > 32);
    }

    [Fact]
    public async Task RegisterAsync_MapsCompleteRegistrationResponseIntoRestorableIdentity()
    {
        using var rsa = RSA.Create(2048);
        var privateKey = rsa.ExportRSAPrivateKeyPem();
        const string adp = "{enc:YWJj}{key:ZGVm}{iv:Z2hp}{name:QURQVG9rZW5FbmNyeXB0aW9uS2V5}{serial:Mg==}";
        var response = """
        {"response":{"success":{"tokens":{
          "bearer":{"access_token":"Atna|test-access","refresh_token":"Atnr|test-refresh","expires_in":3600},
          "mac_dms":{"device_private_key":__KEY__,"adp_token":"__ADP__"},
          "website_cookies":[{"Name":"session","Value":"cookie-one"},{"Name":"session","Value":"cookie-two"}],
          "store_authentication_cookie":{"cookie":"store-cookie"}},
          "extensions":{"device_info":{"device_name":"Audible for Android","device_serial_number":"registered-serial","device_type":"registered-device-type"},
          "customer_info":{"user_id":"amazon-user-123"}}}}}
        """.Replace("__KEY__", JsonSerializer.Serialize(privateKey), StringComparison.Ordinal)
          .Replace("__ADP__", adp, StringComparison.Ordinal);
        var handler = new CapturingHandler(response, HttpStatusCode.OK);
        using var http = new HttpClient(handler);
        var challenge = AudibleOAuthFlow.Create("in");
        var client = new AudiblePkceRegistrationClient(http);

        var snapshot = await client.RegisterAsync(challenge, "authorization-code", CreateProfile());

        Assert.Equal(AudibleApi.Localization.Get("in").Name, snapshot.LocaleName);
        Assert.Equal("Atna|test-access", snapshot.AccessToken);
        Assert.Equal("Atnr|test-refresh", snapshot.RefreshToken);
        Assert.Equal(privateKey, snapshot.PrivateKey);
        Assert.Equal(adp, snapshot.AdpToken);
        Assert.Equal("registered-serial", snapshot.DeviceSerialNumber);
        Assert.Equal("registered-device-type", snapshot.DeviceType);
        Assert.Equal("amazon-user-123", snapshot.AmazonAccountId);
        Assert.Equal("Audible for Android", snapshot.DeviceName);
        Assert.Equal("store-cookie", snapshot.StoreAuthenticationCookie);
        Assert.Equal(new[] { "cookie-one", "cookie-two" }, snapshot.Cookies.Select(c => c.Value));

        var restored = AudibleIdentitySnapshotMapper.Restore(AudibleIdentitySnapshotSerializer.Deserialize(
            AudibleIdentitySnapshotSerializer.Serialize(snapshot)));
        Assert.True(restored.IsValid);
        Assert.Equal(snapshot.AccessToken, restored.ExistingAccessToken.Reveal());
        Assert.Equal(snapshot.RefreshToken, restored.RefreshToken?.Reveal());
    }

    [Fact]
    public async Task RegisterAsync_RejectsIncompleteSuccessfulResponse()
    {
        var handler = new CapturingHandler(
            """{"response":{"success":{"tokens":{"bearer":{"access_token":"Atna|x","refresh_token":"Atnr|y","expires_in":3600}},"extensions":{"device_info":{},"customer_info":{}}}}}""",
            HttpStatusCode.OK);
        using var http = new HttpClient(handler);
        var client = new AudiblePkceRegistrationClient(http);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            client.RegisterAsync(AudibleOAuthFlow.Create("in"), "authorization-code", CreateProfile()));

        Assert.Contains("device credentials", error.Message);
    }

    private static AudibleAndroidDeviceProfile CreateProfile() =>
        new("Test Android Device", "10", "2090263407", "130050002", "Audible for Android");

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
