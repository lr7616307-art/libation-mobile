using Dinah.Core;
using Libation.Mobile.Audible;
using Libation.Mobile.Core;
using Xunit;

namespace Libation.Mobile.Core.Tests;

public sealed class AudibleAccountAuthenticatorTests
{
    [Fact]
    public async Task BeginSignIn_AcceptsIndiaMarketplaceCountryCode()
    {
        var authenticator = CreateAuthenticator(new InMemorySecretStore());

        var challenge = await authenticator.BeginSignInAsync(new AccountSignInRequest("test", "in"));

        Assert.True(challenge.AuthorizationUri.IsAbsoluteUri);
        Assert.Equal(Uri.UriSchemeHttps, challenge.AuthorizationUri.Scheme);
        var fixedNow = new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(fixedNow.AddMinutes(10), challenge.ExpiresAtUtc);
    }

    [Theory]
    [InlineData("http://www.amazon.com/ap/signin")]
    [InlineData("not-a-url")]
    [InlineData("/relative/callback")]
    public async Task CompleteSignIn_RejectsNonAbsoluteHttpsResponseWithoutPersistingIdentity(string responseUrl)
    {
        var secrets = new InMemorySecretStore();
        var authenticator = CreateAuthenticator(secrets);
        var challenge = await authenticator.BeginSignInAsync(new AccountSignInRequest("test", "us"));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => authenticator.CompleteSignInAsync(challenge.ChallengeId, responseUrl));

        Assert.Contains("absolute HTTPS", error.Message);
        Assert.Equal(0, secrets.SetCalls);
        Assert.Null(await new AudibleIdentitySnapshotStore(secrets).LoadAsync());
    }

    [Theory]
    [InlineData("https://evil.example/ap/maplanding?openid.oa2.authorization_code=fake")]
    [InlineData("https://www.amazon.com.evil.example/ap/maplanding?openid.oa2.authorization_code=fake")]
    [InlineData("https://www.amazon.com:444/ap/maplanding?openid.oa2.authorization_code=fake")]
    public async Task CompleteSignIn_RejectsResponseOutsideSelectedMarketplaceHost(string responseUrl)
    {
        var secrets = new InMemorySecretStore();
        var authenticator = CreateAuthenticator(secrets);
        var challenge = await authenticator.BeginSignInAsync(new AccountSignInRequest("test", "us"));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => authenticator.CompleteSignInAsync(challenge.ChallengeId, responseUrl));

        Assert.Contains("sign-in response", error.Message);
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
            () => authenticator.CompleteSignInAsync(challenge.ChallengeId, "https://www.amazon.com/"));

        Assert.Contains("missing, expired, or already used", replayError.Message);
        Assert.Equal(0, secrets.SetCalls);
    }

    private static AudibleAccountAuthenticator CreateAuthenticator(InMemorySecretStore secrets)
    {
        var clock = new FixedClock(new DateTime(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc));
        return new AudibleAccountAuthenticator(new AudibleIdentitySnapshotStore(secrets), clock);
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
