using Libation.Mobile.Core;
using Xunit;

namespace Libation.Mobile.Core.Tests;

public sealed class AccountConnectionServiceTests
{
    [Fact]
    public async Task Connect_PersistsSessionAndReturnsCredentialFreeState()
    {
        var session = new AccountSession("acct-1", "access-secret", "refresh-secret", DateTimeOffset.UtcNow.AddHours(1));
        var secrets = new InMemorySecretStore();
        var service = new AccountConnectionService(
            new FakeAuthenticator(session), new AccountSessionStore(secrets));

        var state = await service.ConnectAsync();

        Assert.Equal(AccountConnectionStatus.Connected, state.Status);
        Assert.Equal("acct-1", state.AccountId);
        Assert.DoesNotContain("access-secret", System.Text.Json.JsonSerializer.Serialize(state));
        Assert.DoesNotContain("refresh-secret", System.Text.Json.JsonSerializer.Serialize(state));
        Assert.Equal(session.AccountId, (await new AccountSessionStore(secrets).LoadAsync())?.AccountId);
    }

    [Fact]
    public async Task GetState_WhenNoSavedSession_IsDisconnected()
    {
        var service = new AccountConnectionService(
            new FakeAuthenticator(null), new AccountSessionStore(new InMemorySecretStore()));

        var state = await service.GetStateAsync();

        Assert.Equal(AccountConnectionStatus.Disconnected, state.Status);
        Assert.Null(state.AccountId);
    }

    [Fact]
    public async Task GetState_WhenSessionExpired_ReportsExpiredWithoutExposingTokens()
    {
        var secrets = new InMemorySecretStore();
        var store = new AccountSessionStore(secrets);
        await store.SaveAsync(new("acct-2", "a", "r", DateTimeOffset.UtcNow.AddMinutes(-1)));
        var service = new AccountConnectionService(new FakeAuthenticator(null), store);

        var state = await service.GetStateAsync();

        Assert.Equal(AccountConnectionStatus.Expired, state.Status);
        Assert.Equal("acct-2", state.AccountId);
    }

    [Fact]
    public async Task Refresh_UsesAuthenticatorAndPersistsRotatedSession()
    {
        var secrets = new InMemorySecretStore();
        var store = new AccountSessionStore(secrets);
        await store.SaveAsync(new("acct-3", "old-access", "old-refresh", DateTimeOffset.UtcNow.AddMinutes(-1)));
        var rotated = new AccountSession("acct-3", "new-access", "new-refresh", DateTimeOffset.UtcNow.AddHours(1));
        var service = new AccountConnectionService(new FakeAuthenticator(rotated), store);

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
        await store.SaveAsync(new("acct-4", "a", "r", DateTimeOffset.UtcNow.AddHours(1)));
        var service = new AccountConnectionService(new FakeAuthenticator(null), store);

        await service.DisconnectAsync();

        Assert.Equal(AccountConnectionStatus.Disconnected, (await service.GetStateAsync()).Status);
    }

    private sealed class FakeAuthenticator(AccountSession? result) : IAccountAuthenticator
    {
        public Task<AccountSession> SignInAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(result ?? throw new InvalidOperationException("No fake sign-in result configured."));

        public Task<AccountSession> RefreshAsync(AccountSession currentSession, CancellationToken cancellationToken = default)
            => Task.FromResult(result ?? throw new InvalidOperationException("No fake refresh result configured."));
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
