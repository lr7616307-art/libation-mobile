using System.Net;
using System.Text.Json;
using Libation.Mobile.Audible;
using Xunit;

namespace Libation.Mobile.Core.Tests;

public sealed class AudiblePkceRegistrationClientTests
{
    [Fact]
    public async Task RegisterAsync_SendsAndroidRegistrationContextToAmazonApiHost()
    {
        var handler = new CapturingHandler();
        using var http = new HttpClient(handler);
        var client = new AudiblePkceRegistrationClient(http);
        var challenge = AudibleOAuthFlow.Create("in");
        var profile = new AudibleAndroidDeviceProfile(
            "Test Android Device", "10", "2090263407", "130050002", "Audible for Android");

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.RegisterAsync(challenge, "authorization-code-test", profile));

        Assert.Equal("https://api.amazon.in/auth/register", handler.RequestUri?.AbsoluteUri);
        Assert.NotNull(handler.Body);
        using var body = JsonDocument.Parse(handler.Body!);
        var root = body.RootElement;
        Assert.Equal("www.amazon.in", root.GetProperty("cookies").GetProperty("domain").GetString());
        Assert.Equal("DeviceLegacy", root.GetProperty("registration_data").GetProperty("domain").GetString());
        Assert.Equal("com.audible.application", root.GetProperty("registration_data").GetProperty("app_name").GetString());
        Assert.Equal(challenge.DeviceSerial, root.GetProperty("device_metadata").GetProperty("device_serial").GetString());
        Assert.Equal("android", root.GetProperty("device_metadata").GetProperty("device_os_family").GetString());
        Assert.Equal("true", root.GetProperty("auth_data").GetProperty("use_global_authentication").GetString());
        var frc = root.GetProperty("user_context_map").GetProperty("frc").GetString();
        Assert.False(string.IsNullOrWhiteSpace(frc));
        Assert.True(Convert.FromBase64String(frc!).Length > 32);
        Assert.Equal(challenge.CodeVerifier, root.GetProperty("auth_data").GetProperty("code_verifier").GetString());
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("{\"error\":\"test\"}")
            };
        }
    }
}
