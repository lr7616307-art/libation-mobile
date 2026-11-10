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
    [InlineData("https://www.amazon.com/ap/maplanding?openid.oa2.authorization_code=fake")]
    [InlineData("https://www.audible.com/not-maplanding?openid.oa2.authorization_code=fake")]
    [InlineData("https://www.audible.com/ap/maplanding")]
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
    public async Task InvalidResponse_ConsumesOneTimeChallenge()
    {
        var secrets = new InMemorySecretStore();
        var authenticator = CreateAuthenticator(secrets);
        var challenge = await authenticator.BeginSignInAsync(new AccountSignInRequest("test", "us"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => authenticator.CompleteSignInAsync(challenge.ChallengeId, "http://example.com/callback"));

        var replayError = await Assert.ThrowsAsync<InvalidOperationException>(
            () => authenticator.CompleteSignInAsync(challenge.ChallengeId, "https://www.audible.com/ap/maplanding?openid.oa2.authorization_code=fake"));

        Assert.Contains("missing, expired, or already used", replayError.Message);
        Assert.Equal(0, secrets.SetCalls);
    }

    private static AudibleAccountAuthenticator CreateAuthenticator(InMemorySecretStore secrets)
    {
        var clock = new FixedClock(new DateTime(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc));
        var profile = new AudibleAndroidDeviceProfile(
            "Test Android Device", "10", "1.0.0", "1.0.0", "Test Android Device");
        return new AudibleAccountAuthenticator(
            new AudibleIdentitySnapshotStore(secrets),
            new AudiblePkceRegistrationClient(new HttpClient()),
            profile,
            clock);
    }

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
