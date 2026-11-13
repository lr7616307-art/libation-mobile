using System.Web;
using Dinah.Core;
using Libation.Mobile.Audible;
using Libation.Mobile.Core;
using Xunit;

namespace Libation.Mobile.Core.Tests;

public sealed class AudibleAccountAuthenticatorTests
{
    [Fact]
    public async Task BeginSignIn_AcceptsIndiaMarketplaceAndUsesItsProviderReturnHost()
    {
        var authenticator = CreateAuthenticator(new InMemorySecretStore());
        var challenge = await authenticator.BeginSignInAsync(new AccountSignInRequest("test", "in"));

        Assert.True(challenge.AuthorizationUri.IsAbsoluteUri);
        Assert.Equal(Uri.UriSchemeHttps, challenge.AuthorizationUri.Scheme);
        var returnTo = HttpUtility.ParseQueryString(challenge.AuthorizationUri.Query)["openid.return_to"];
        Assert.True(Uri.TryCreate(returnTo, UriKind.Absolute, out var callback));
        Assert.Equal("www.audible.in", callback!.Host);
        Assert.Equal("/ap/maplanding", callback.AbsolutePath);
        Assert.Equal("https://www.audible.in/ap/maplanding", callback.AbsoluteUri);
        var fixedNow = new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(fixedNow.AddMinutes(10), challenge.ExpiresAtUtc);
    }

    [Theory]
    [InlineData("http://www.audible.com/ap/maplanding?openid.oa2.authorization_code=fake")]
    [InlineData("not-a-url")]
    [InlineData("/relative/callback")]
    public async Task CompleteSignIn_RejectsNonAbsoluteHttpsResponseWithoutPersistingIdentity(string responseUrl)
    {
        var secrets = new InMemorySecretStore();
        var authenticator = CreateAuthenticator(secrets);
        var challenge = await authenticator.BeginSignInAsync(new AccountSignInRequest("test", "us"));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => authenticator.CompleteSignInAsync(challenge.ChallengeId, responseUrl));

        Assert.Contains("callback", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, secrets.SetCalls);
        Assert.Null(await new AudibleIdentitySnapshotStore(secrets).LoadAsync());
    }

    [Theory]
    [InlineData("https://evil.example/ap/maplanding?openid.oa2.authorization_code=fake")]
    [InlineData("https://www.audible.com.evil.example/ap/maplanding?openid.oa2.authorization_code=fake")]
    [InlineData("https://www.audible.com:444/ap/maplanding?openid.oa2.authorization_code=fake")]
    [InlineData("https://www.audible.in/ap/maplanding?openid.oa2.authorization_code=fake")]
    [InlineData("https://www.audible.com/not-maplanding?openid.oa2.authorization_code=fake")]
    public async Task CompleteSignIn_RejectsResponseOutsideExactMarketplaceCallback(string responseUrl)
    {
        var secrets = new InMemorySecretStore();
        var authenticator = CreateAuthenticator(secrets);
        var challenge = await authenticator.BeginSignInAsync(new AccountSignInRequest("test", "us"));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => authenticator.CompleteSignInAsync(challenge.ChallengeId, responseUrl));

        Assert.Contains("marketplace callback", error.Message);
        Assert.Equal(0, secrets.SetCalls);
        Assert.Null(await new AudibleIdentitySnapshotStore(secrets).LoadAsync());
    }

    [Fact]
    public async Task CompleteSignIn_RejectsValidCallbackWithoutAuthorizationCode()
    {
        var secrets = new InMemorySecretStore();
        var authenticator = CreateAuthenticator(secrets);
        var challenge = await authenticator.BeginSignInAsync(new AccountSignInRequest("test", "us"));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => authenticator.CompleteSignInAsync(
                challenge.ChallengeId, "https://www.audible.com/ap/maplanding"));

        Assert.Contains("authorization code", error.Message);
        Assert.Equal(0, secrets.SetCalls);
        Assert.Null(await new AudibleIdentitySnapshotStore(secrets).LoadAsync());
    }

    [Fact]
    public async Task InvalidResponse_ConsumesOneTimeChallenge()
    {
        var secrets = new InMemorySecretStore();
        var authenticator = CreateAuthenticator(secrets);
        var challenge = await authenticator.BeginSignInAsync(new AccountSignInRequest("test", "us"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => authenticator.CompleteSignInAsync(challenge.ChallengeId, "http://example.com/callback"));

        var replayError = await Assert.ThrowsAsync<InvalidOperationException>(
            () => authenticator.CompleteSignInAsync(challenge.ChallengeId,
                "https://www.audible.com/ap/maplanding?openid.oa2.authorization_code=fake"));

        Assert.Contains("missing, expired, or already used", replayError.Message);
        Assert.Equal(0, secrets.SetCalls);
    }

    [Fact]
    public async Task BeginSignIn_ReusesStableInstallationScopedDeviceSerial()
    {
        const string stableSerial = "0123456789abcdef0123";
        var clock = new FixedClock(new DateTime(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc));
        var authenticator = new AudibleAccountAuthenticator(
            new AudibleIdentitySnapshotStore(new InMemorySecretStore()),
            new AudiblePkceRegistrationClient(new HttpClient()),
            new AudibleTokenRefreshClient(new HttpClient(), clock),
            clock,
            TestDeviceInfo,
            () => stableSerial);

        var first = await authenticator.BeginSignInAsync(new AccountSignInRequest("test", "in"));
        var second = await authenticator.BeginSignInAsync(new AccountSignInRequest("test", "in"));
        var firstClientId = HttpUtility.ParseQueryString(first.AuthorizationUri.Query)["openid.oa2.client_id"]!;
        var secondClientId = HttpUtility.ParseQueryString(second.AuthorizationUri.Query)["openid.oa2.client_id"]!;

        Assert.Equal(stableSerial, DecodeClientSerial(firstClientId));
        Assert.Equal(stableSerial, DecodeClientSerial(secondClientId));
        Assert.NotEqual(
            HttpUtility.ParseQueryString(first.AuthorizationUri.Query)["openid.oa2.code_challenge"],
            HttpUtility.ParseQueryString(second.AuthorizationUri.Query)["openid.oa2.code_challenge"]);
    }

    private static string DecodeClientSerial(string clientId)
    {
        var decoded = System.Text.Encoding.UTF8.GetString(Convert.FromHexString(clientId["device:".Length..]));
        return decoded[..decoded.IndexOf('#')];
    }

    private static AudibleAccountAuthenticator CreateAuthenticator(InMemorySecretStore secrets)
    {
        var clock = new FixedClock(new DateTime(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc));
        return new AudibleAccountAuthenticator(
            new AudibleIdentitySnapshotStore(secrets),
            new AudiblePkceRegistrationClient(new HttpClient()),
            new AudibleTokenRefreshClient(new HttpClient(), clock),
            clock,
            TestDeviceInfo);
    }

    private static AudibleAndroidDeviceInfo TestDeviceInfo() => new(
        "Test Manufacturer", "Test Brand", "Test Model", "test-device", "test-product",
        "test/test/test:14/UP1A.1/test:user/release-keys", "14", "34",
        "Test Android Device", "en-IN", 1080, 2400);

    private sealed class FixedClock(DateTime utcNow) : ISystemDateTime
    {
        public DateTime Now => utcNow.ToLocalTime();
        public DateTime UtcNow => utcNow;
    }

    private sealed class InMemorySecretStore : ISecretStore
    {
        private readonly Dictionary<string, string> values = new();
        public int SetCalls { get; private set; }

        public Task SetAsync(string key, string value, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SetCalls++;
            values[key] = value;
            return Task.CompletedTask;
        }

        public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            values.TryGetValue(key, out var value);
            return Task.FromResult<string?>(value);
        }

        public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            values.Remove(key);
            return Task.CompletedTask;
        }
    }
}
