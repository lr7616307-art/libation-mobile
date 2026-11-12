using Libation.Mobile.Audible;
using Libation.Mobile.Core;
using Xunit;

namespace Libation.Mobile.Core.Tests;

public sealed class AudibleOfflineLicenseKeySetStoreTests
{
    [Fact]
    public async Task SaveAndLoad_RoundTripsOpaqueKeySetId()
    {
        var secrets = new MemorySecretStore();
        var store = new AudibleOfflineLicenseKeySetStore(secrets);
        byte[] expected = [0x01, 0x23, 0x45, 0x67, 0x89, 0xAB];

        await store.SaveAsync("account-123", "B012345678", expected);
        var actual = await store.LoadAsync("account-123", "B012345678");

        Assert.Equal(expected, actual);
        Assert.Single(secrets.Keys);
        Assert.DoesNotContain("B012345678", secrets.Keys.Single(), StringComparison.Ordinal);
        Assert.DoesNotContain("account-123", secrets.Keys.Single(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Load_IsScopedToAccountAndAsin()
    {
        var store = new AudibleOfflineLicenseKeySetStore(new MemorySecretStore());
        byte[] first = [0x01, 0x02];
        byte[] second = [0x03, 0x04];

        await store.SaveAsync("account-a", "B012345678", first);
        await store.SaveAsync("account-b", "B012345678", second);
        await store.SaveAsync("account-a", "B098765432", second);

        Assert.Equal(first, await store.LoadAsync("account-a", "B012345678"));
        Assert.Equal(second, await store.LoadAsync("account-b", "B012345678"));
        Assert.Equal(second, await store.LoadAsync("account-a", "B098765432"));
        Assert.Null(await store.LoadAsync("account-c", "B012345678"));
    }

    [Fact]
    public async Task Remove_OnlyRemovesMatchingAccountAndTitle()
    {
        var store = new AudibleOfflineLicenseKeySetStore(new MemorySecretStore());
        byte[] keySetId = [0x11, 0x22];

        await store.SaveAsync("account-a", "B012345678", keySetId);
        await store.SaveAsync("account-b", "B012345678", keySetId);

        await store.RemoveAsync("account-a", "B012345678");

        Assert.Null(await store.LoadAsync("account-a", "B012345678"));
        Assert.Equal(keySetId, await store.LoadAsync("account-b", "B012345678"));
    }

    [Fact]
    public async Task Save_RejectsEmptyOrOversizedKeySetId()
    {
        var store = new AudibleOfflineLicenseKeySetStore(new MemorySecretStore());

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            store.SaveAsync("account-a", "B012345678", []));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            store.SaveAsync("account-a", "B012345678", new byte[1025]));
    }

    [Fact]
    public async Task Save_RejectsInvalidAsin()
    {
        var store = new AudibleOfflineLicenseKeySetStore(new MemorySecretStore());

        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.SaveAsync("account-a", "not-an-asin", [0x01]));
    }

    [Fact]
    public async Task Load_RejectsMalformedStoredValue()
    {
        var secrets = new MemorySecretStore();
        var store = new AudibleOfflineLicenseKeySetStore(secrets);
        await secrets.SetAsync(
            "audible_widevine_keyset_v1_" +
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes("account-a\nB012345678"))).ToLowerInvariant(),
            "not-base64!");

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.LoadAsync("account-a", "B012345678"));
    }

    private sealed class MemorySecretStore : ISecretStore
    {
        private readonly Dictionary<string, string> values = new();
        public IReadOnlyCollection<string> Keys => values.Keys.ToArray();

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
