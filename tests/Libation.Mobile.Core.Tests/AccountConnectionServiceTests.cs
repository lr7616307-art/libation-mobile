using Libation.Mobile.Core;
using Xunit;

namespace Libation.Mobile.Core.Tests;

public sealed class AccountConnectionServiceTests
{
    [Fact]
    public async Task BeginSignIn_ValidatesRequestAndReturnsHttpsChallenge()
    {
        var authenticator = new FakeAuthenticator();
        var service = CreateService(authenticator);

        var challenge = await service.BeginSignInAsync(new("user@example.com", "in"));

        Assert.Equal("challenge-1", challenge.ChallengeId);
        Assert.Equal("https", challenge.AuthorizationUri.Scheme);
        Assert.Equal(1, authenticator.BeginCount);
    }

    [Fact]
    public async Task BeginSignIn_RejectsEmptyLoginOrMarketplace()
    {
        var service = CreateService(new FakeAuthenticator());

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.BeginSignInAsync(new("", "in")));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.BeginSignInAsync(new("user@example.com", "")));
    }

    [Fact]
    public async Task CompleteSignIn_PersistsSessionAndReturnsCredentialFreeState()
    {
        var session = NewSession("acct-1", "access-secret", "refresh-secret");
        var secrets = new InMemorySecretStore();
        var authenticator = new FakeAuthenticator(session);
        var service = new AccountConnectionService(authenticator, new AccountSessionStore(secrets));

        var state = await service.CompleteSignInAsync("challenge-1", "https://example.invalid/return?code=redacted");

        Assert.Equal(AccountConnectionStatus.Connected, state.Status);
        Assert.Equal("acct-1", state.AccountId);
        Assert.DoesNotContain("access-secret", System.Text.Json.JsonSerializer.Serialize(state));
        Assert.DoesNotContain("refresh-secret", System.Text.Json.JsonSerializer.Serialize(state));
        Assert.Equal("acct-1", (await new AccountSessionStore(secrets).LoadAsync())?.AccountId);
    }

    [Fact]
    public async Task GetState_WhenNoSavedSession_IsDisconnected()
    {
        var service = CreateService(new FakeAuthenticator());

        var state = await service.GetStateAsync();

        Assert.Equal(AccountConnectionStatus.Disconnected, state.Status);
        Assert.Null(state.AccountId);
    }

    [Fact]
    public async Task GetState_WhenSessionExpired_ReportsExpired()
    {
        var secrets = new InMemorySecretStore();
        var store = new AccountSessionStore(secrets);
        await store.SaveAsync(NewSession("acct-2", "a", "r", DateTimeOffset.UtcNow.AddMinutes(-1)));
        var service = new AccountConnectionService(new FakeAuthenticator(), store);

        Assert.Equal(AccountConnectionStatus.Expired, (await service.GetStateAsync()).Status);
    }

    [Fact]
    public async Task Refresh_UsesAuthenticatorAndPersistsRotatedSession()
    {
        var secrets = new InMemorySecretStore();
        var store = new AccountSessionStore(secrets);
        await store.SaveAsync(NewSession("acct-3", "old-access", "old-refresh", DateTimeOffset.UtcNow.AddMinutes(-1)));
        var authenticator = new FakeAuthenticator(NewSession("acct-3", "new-access", "new-refresh"));
        var service = new AccountConnectionService(authenticator, store);

        var state = await service.RefreshAsync();
        var saved = await store.LoadAsync();

        Assert.Equal(AccountConnectionStatus.Connected, state.Status);
        Assert.Equal("new-access", saved?.AccessToken);
        Assert.Equal("new-refresh", saved?.RefreshToken);
    }

    [Fact]
    public async Task Disconnect_ClearsSavedSession()
    {
        var secrets = new InMemorySecretStore();
        var store = new AccountSessionStore(secrets);
        await store.SaveAsync(NewSession("acct-4", "a", "r"));
        var service = new AccountConnectionService(new FakeAuthenticator(), store);

        await service.DisconnectAsync();

        Assert.Equal(AccountConnectionStatus.Disconnected, (await service.GetStateAsync()).Status);
    }

    private static AccountConnectionService CreateService(FakeAuthenticator authenticator)
        => new(authenticator, new AccountSessionStore(new InMemorySecretStore()));

    private static AccountSession NewSession(
        string id, string access, string refresh, DateTimeOffset? expiry = null)
        => new(id, access, refresh, expiry ?? DateTimeOffset.UtcNow.AddHours(1));

    private sealed class FakeAuthenticator(AccountSession? completion = null) : IAccountAuthenticator
    {
        public int BeginCount { get; private set; }

        public Task<ExternalSignInChallenge> BeginSignInAsync(
            AccountSignInRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BeginCount++;
            return Task.FromResult(new ExternalSignInChallenge(
                "challenge-1", new Uri("https://example.invalid/login"), DateTimeOffset.UtcNow.AddMinutes(5)));
        }

        public Task<AccountSession> CompleteSignInAsync(
            string challengeId, string responseUrl, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(completion ?? throw new InvalidOperationException("No fake completion result configured."));
        }

        public Task<AccountSession> RefreshAsync(
            AccountSession currentSession, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(completion ?? throw new InvalidOperationException("No fake refresh result configured."));
        }
    }

    private sealed class InMemorySecretStore : ISecretStore
    {
        private readonly Dictionary<string, string> _values = new();
        public Task SetAsync(string key, string value, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); _values[key] = value; return Task.CompletedTask; }
        public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); _values.TryGetValue(key, out var value); return Task.FromResult(value); }
        public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); _values.Remove(key); return Task.CompletedTask; }
    }
}
