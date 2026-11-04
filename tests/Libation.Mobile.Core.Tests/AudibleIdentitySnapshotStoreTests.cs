using System.Security.Cryptography;
using Libation.Mobile.Audible;
using Libation.Mobile.Core;
using Xunit;

namespace Libation.Mobile.Core.Tests;

public sealed class AudibleIdentitySnapshotStoreTests
{
    [Fact]
    public async Task SaveAndLoad_StoresOneCompleteSerializedRecord()
    {
        var secrets = new InMemorySecretStore();
        var store = new AudibleIdentitySnapshotStore(secrets);
        var expected = CreateSnapshot();

        await store.SaveAsync(expected);
        var actual = await store.LoadAsync();

        Assert.NotNull(actual);
        Assert.Equal(expected.AccessToken, actual.AccessToken);
        Assert.Equal(expected.RefreshToken, actual.RefreshToken);
        Assert.Equal(expected.PrivateKey, actual.PrivateKey);
        Assert.Equal(expected.AdpToken, actual.AdpToken);
        Assert.Equal(expected.AccessTokenExpiresAtUtc, actual.AccessTokenExpiresAtUtc);
        Assert.Equal(expected.Cookies, actual.Cookies);
        Assert.Equal(1, secrets.Count);
        Assert.True(secrets.Contains(AudibleIdentitySnapshotStore.StorageKey));
    }

    [Fact]
    public async Task Load_WhenMissing_ReturnsNull()
    {
        var store = new AudibleIdentitySnapshotStore(new InMemorySecretStore());

        Assert.Null(await store.LoadAsync());
    }

    [Theory]
    [InlineData("{not-json")]
    [InlineData("{\"schemaVersion\":99}")]
    public async Task Load_WhenSavedPayloadIsInvalid_RequiresAccountRecovery(string payload)
    {
        var secrets = new InMemorySecretStore();
        secrets.Seed(AudibleIdentitySnapshotStore.StorageKey, payload);
        var store = new AudibleIdentitySnapshotStore(secrets);

        var error = await Assert.ThrowsAsync<AudibleIdentityRecoveryRequiredException>(
            () => store.LoadAsync());
        Assert.Contains("Sign in again", error.Message);
    }

    [Fact]
    public async Task Load_WhenAuthenticatedDecryptionFails_RequiresAccountRecovery()
    {
        var secrets = new InMemorySecretStore { GetException = new CryptographicException("bad tag") };
        secrets.Seed(AudibleIdentitySnapshotStore.StorageKey, "encrypted-record");
        var store = new AudibleIdentitySnapshotStore(secrets);

        var error = await Assert.ThrowsAsync<AudibleIdentityRecoveryRequiredException>(
            () => store.LoadAsync());
        Assert.Contains("Sign in again", error.Message);
    }

    [Fact]
    public async Task Save_WhenSecretStoreFails_PropagatesAndDoesNotReportSuccess()
    {
        var secrets = new InMemorySecretStore { SetException = new IOException("storage unavailable") };
        var store = new AudibleIdentitySnapshotStore(secrets);

        await Assert.ThrowsAsync<IOException>(() => store.SaveAsync(CreateSnapshot()));
        Assert.Equal(0, secrets.Count);
    }

    [Fact]
    public async Task Load_WhenCancelled_PropagatesCancellation()
    {
        var secrets = new InMemorySecretStore();
        var store = new AudibleIdentitySnapshotStore(secrets);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => store.LoadAsync(cancellation.Token));
    }

    [Fact]
    public async Task Load_WhenStorageIsUnavailable_PropagatesFailure()
    {
        var secrets = new InMemorySecretStore { GetException = new IOException("storage unavailable") };
        var store = new AudibleIdentitySnapshotStore(secrets);

        await Assert.ThrowsAsync<IOException>(() => store.LoadAsync());
    }

    [Fact]
    public async Task Clear_RemovesTheSingleIdentityRecord()
    {
        var secrets = new InMemorySecretStore();
        var store = new AudibleIdentitySnapshotStore(secrets);
        await store.SaveAsync(CreateSnapshot());

        await store.ClearAsync();

        Assert.Null(await store.LoadAsync());
        Assert.Equal(0, secrets.Count);
    }

    private static AudibleIdentitySnapshot CreateSnapshot() => new()
    {
        SchemaVersion = AudibleIdentitySnapshot.CurrentSchemaVersion,
        Provider = AudibleIdentitySnapshot.ProviderName,
        AudibleApiVersion = AudibleIdentitySnapshot.SupportedAudibleApiVersion,
        LocaleName = "us",
        AccessToken = "Atna|access",
        AccessTokenExpiresAtUtc = new DateTimeOffset(2027, 4, 5, 6, 7, 8, TimeSpan.Zero),
        RefreshToken = "Atnr|refresh",
        PrivateKey = "private-key",
        AdpToken = "{enc:YWJj,key:ZGVm,iv:Z2hp,name:QURQVG9rZW5FbmNyeXB0aW9uS2V5,serial:Mg==}",
        Cookies = [new AudibleCookieSnapshot { Name = "session", Value = "cookie" }],
        StoreAuthenticationCookie = "store-cookie"
    };

    private sealed class InMemorySecretStore : ISecretStore
    {
        private readonly Dictionary<string, string> values = new();
        public int Count => values.Count;
        public Exception? SetException { get; init; }
        public Exception? GetException { get; init; }

        public bool Contains(string key) => values.ContainsKey(key);
        public void Seed(string key, string value) => values[key] = value;

        public Task SetAsync(string key, string value, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (SetException is not null) throw SetException;
            values[key] = value;
            return Task.CompletedTask;
        }

        public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (GetException is not null) throw GetException;
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
