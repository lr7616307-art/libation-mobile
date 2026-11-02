using Libation.Mobile.Core;
using Xunit;

namespace Libation.Mobile.Core.Tests;

public sealed class AccountSessionStoreTests
{
    [Fact]
    public async Task SaveAndLoad_RoundTripsSession()
    {
        var secrets = new InMemorySecretStore();
        var store = new AccountSessionStore(secrets);
        var expiry = new DateTimeOffset(2027, 3, 4, 5, 6, 7, TimeSpan.FromHours(5.5));
        var expected = new AccountSession("account-123", "access-token", "refresh-token", expiry);

        await store.SaveAsync(expected);
        var actual = await store.LoadAsync();

        Assert.NotNull(actual);
        Assert.Equal(expected.AccountId, actual.AccountId);
        Assert.Equal(expected.AccessToken, actual.AccessToken);
        Assert.Equal(expected.RefreshToken, actual.RefreshToken);
        Assert.Equal(expected.ExpiresAtUtc.ToUniversalTime(), actual.ExpiresAtUtc);
        Assert.Equal(1, secrets.Count);
    }

    [Fact]
    public async Task Load_WhenNoSession_ReturnsNull()
    {
        var store = new AccountSessionStore(new InMemorySecretStore());

        Assert.Null(await store.LoadAsync());
    }

    [Fact]
    public async Task Clear_RemovesPersistedSession()
    {
        var secrets = new InMemorySecretStore();
        var store = new AccountSessionStore(secrets);
        await store.SaveAsync(new AccountSession("id", "access", "refresh", DateTimeOffset.UtcNow));

        await store.ClearAsync();

        Assert.Null(await store.LoadAsync());
        Assert.Equal(0, secrets.Count);
    }

    [Fact]
    public async Task Save_OverwritesPreviousSessionAsOneRecord()
    {
        var secrets = new InMemorySecretStore();
        var store = new AccountSessionStore(secrets);
        await store.SaveAsync(new AccountSession("first", "a1", "r1", DateTimeOffset.UtcNow));
        await store.SaveAsync(new AccountSession("second", "a2", "r2", DateTimeOffset.UtcNow));

        var actual = await store.LoadAsync();

        Assert.Equal("second", actual?.AccountId);
        Assert.Equal(1, secrets.Count);
    }

    private sealed class InMemorySecretStore : ISecretStore
    {
        private readonly Dictionary<string, string> values = new();
        public int Count => values.Count;

        public Task SetAsync(string key, string value, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            values[key] = value;
            return Task.CompletedTask;
        }

        public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            values.TryGetValue(key, out var value);
            return Task.FromResult(value);
        }

        public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            values.Remove(key);
            return Task.CompletedTask;
        }
    }
}
