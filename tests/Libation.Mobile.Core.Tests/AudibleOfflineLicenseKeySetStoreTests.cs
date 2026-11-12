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
        Assert.Equal(new[] { "B012345678" },
            await store.ListAccountTitlesAsync("account-123"));
        Assert.Equal(2, secrets.Keys.Count);
        Assert.All(secrets.Keys, key =>
        {
            Assert.DoesNotContain("B012345678", key, StringComparison.Ordinal);
            Assert.DoesNotContain("account-123", key, StringComparison.Ordinal);
        });
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

    [Fact]
    public async Task DashManifestReference_RoundTripsEncryptedStoreMetadata()
    {
        var secrets = new MemorySecretStore();
        var store = new AudibleOfflineLicenseKeySetStore(secrets);
        var expectedUri = new Uri("https://media.example.invalid/path/manifest.mpd?signature=short-lived");
        AudibleWidevineStreamKey[] expectedKeys =
        [
            new AudibleWidevineStreamKey(0, 0, 2),
            new AudibleWidevineStreamKey(1, 0, 1)
        ];

        await store.SaveDashManifestAsync("account-a", "B012345678", expectedUri, expectedKeys);
        var actual = await store.LoadDashManifestAsync("account-a", "B012345678");

        Assert.NotNull(actual);
        Assert.Equal(expectedUri, actual.ManifestUri);
        Assert.Equal(expectedKeys, actual.StreamKeys);
        Assert.DoesNotContain("B012345678", secrets.Keys.Single(), StringComparison.Ordinal);
        Assert.Contains("signature=short-lived", secrets.Values.Single(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RemoveDashManifest_OnlyRemovesMatchingAccountAndTitle()
    {
        var secrets = new MemorySecretStore();
        var store = new AudibleOfflineLicenseKeySetStore(secrets);
        var uri = new Uri("https://media.example.invalid/title/manifest.mpd");
        AudibleWidevineStreamKey[] keys = [new AudibleWidevineStreamKey(0, 0, 0)];

        await store.SaveDashManifestAsync("account-a", "B012345678", uri, keys);
        await store.SaveDashManifestAsync("account-b", "B012345678", uri, keys);

        await store.RemoveDashManifestAsync("account-a", "B012345678");

        Assert.Null(await store.LoadDashManifestAsync("account-a", "B012345678"));
        Assert.NotNull(await store.LoadDashManifestAsync("account-b", "B012345678"));
    }

    [Fact]
    public async Task SaveDashManifest_RejectsNonHttpsUriAndInvalidSelection()
    {
        var store = new AudibleOfflineLicenseKeySetStore(new MemorySecretStore());

        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.SaveDashManifestAsync(
                "account-a", "B012345678",
                new Uri("http://media.example.invalid/manifest.mpd"),
                [new AudibleWidevineStreamKey(0, 0, 0)]));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.SaveDashManifestAsync(
                "account-a", "B012345678",
                new Uri("https://media.example.invalid/manifest.mpd"),
                [new AudibleWidevineStreamKey(-1, 0, 0)]));
    }

    [Fact]
    public async Task InvalidateDashDownloadComplete_RemovesOnlyTheMatchingTitleMarker()
    {
        var store = new AudibleOfflineLicenseKeySetStore(new MemorySecretStore());
        await store.SaveDashManifestAsync(
            "account-a", "B012345678",
            new Uri("https://media.example.invalid/a.mpd"),
            [new AudibleWidevineStreamKey(0, 0, 0)]);
        await store.SaveDashManifestAsync(
            "account-b", "B012345678",
            new Uri("https://media.example.invalid/b.mpd"),
            [new AudibleWidevineStreamKey(0, 0, 0)]);
        await store.MarkDashDownloadCompleteAsync("account-a", "B012345678");
        await store.MarkDashDownloadCompleteAsync("account-b", "B012345678");

        await store.InvalidateDashDownloadCompleteAsync("account-a", "B012345678");

        Assert.False(await store.IsDashDownloadCompleteAsync("account-a", "B012345678"));
        Assert.True(await store.IsDashDownloadCompleteAsync("account-b", "B012345678"));
    }

    [Fact]
    public async Task AccountTitleIndex_IsScopedAndTracksSavedOfflineLicences()
    {
        var store = new AudibleOfflineLicenseKeySetStore(new MemorySecretStore());
        await store.SaveAsync("account-a", "B012345678", [0x01, 0x02]);
        await store.SaveAsync("account-a", "B098765432", [0x03, 0x04]);
        await store.SaveAsync("account-b", "B012345678", [0x05, 0x06]);

        Assert.Equal(
            new[] { "B012345678", "B098765432" },
            await store.ListAccountTitlesAsync("account-a"));
        Assert.Equal(
            new[] { "B012345678" },
            await store.ListAccountTitlesAsync("account-b"));
        Assert.Empty(await store.ListAccountTitlesAsync("account-c"));
    }

    [Fact]
    public async Task Remove_DeletesTitleFromAccountIndexAndLeavesOtherTitles()
    {
        var store = new AudibleOfflineLicenseKeySetStore(new MemorySecretStore());
        await store.SaveAsync("account-a", "B012345678", [0x01]);
        await store.SaveAsync("account-a", "B098765432", [0x02]);

        await store.RemoveAsync("account-a", "B012345678");

        Assert.Equal(
            new[] { "B098765432" },
            await store.ListAccountTitlesAsync("account-a"));
        Assert.Null(await store.LoadAsync("account-a", "B012345678"));
        Assert.NotNull(await store.LoadAsync("account-a", "B098765432"));
    }

    [Fact]
    public async Task SaveDashManifest_InvalidatePreviousCompletionMarkerBeforeInterruptedRetry()
    {
        var store = new AudibleOfflineLicenseKeySetStore(new MemorySecretStore());
        var uri = new Uri("https://media.example.invalid/title/manifest.mpd");
        AudibleWidevineStreamKey[] keys = [new AudibleWidevineStreamKey(0, 0, 0)];

        await store.SaveDashManifestAsync("account-a", "B012345678", uri, keys);
        await store.MarkDashDownloadCompleteAsync("account-a", "B012345678");
        Assert.True(await store.IsDashDownloadCompleteAsync("account-a", "B012345678"));

        // Starting a replacement/retry invalidates the previous marker first.
        await store.SaveDashManifestAsync("account-a", "B012345678", uri, keys);

        Assert.False(await store.IsDashDownloadCompleteAsync("account-a", "B012345678"));
    }

    [Fact]
    public async Task ProviderLicenseReference_RoundTripsAndIsScopedByAccountAndTitle()
    {
        var secrets = new MemorySecretStore();
        var store = new AudibleOfflineLicenseKeySetStore(secrets);

        await store.SaveProviderLicenseReferenceAsync(
            "account-a", "B012345678", "CR!audible-content-reference", "7");
        await store.SaveProviderLicenseReferenceAsync(
            "account-b", "B012345678", "CR!other-account-reference", "2");

        var actual = await store.LoadProviderLicenseReferenceAsync("account-a", "B012345678");

        Assert.NotNull(actual);
        Assert.Equal("CR!audible-content-reference", actual.Acr);
        Assert.Equal("7", actual.FileVersion);
        Assert.Null(await store.LoadProviderLicenseReferenceAsync("account-a", "B098765432"));
        Assert.DoesNotContain("B012345678", secrets.Keys.Single(key =>
            key.Contains("account", StringComparison.Ordinal) == false), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SaveProviderLicenseReference_WhenMetadataMissingClearsStaleValue()
    {
        var store = new AudibleOfflineLicenseKeySetStore(new MemorySecretStore());
        await store.SaveProviderLicenseReferenceAsync(
            "account-a", "B012345678", "CR!old-reference", "1");

        await store.SaveProviderLicenseReferenceAsync(
            "account-a", "B012345678", null, null);

        Assert.Null(await store.LoadProviderLicenseReferenceAsync("account-a", "B012345678"));
    }

    [Fact]
    public async Task SaveProviderLicenseReference_RequiresBothAcrAndFileVersion()
    {
        var store = new AudibleOfflineLicenseKeySetStore(new MemorySecretStore());

        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.SaveProviderLicenseReferenceAsync(
                "account-a", "B012345678", "CR!reference", null));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.SaveProviderLicenseReferenceAsync(
                "account-a", "B012345678", null, "1"));
    }

    [Fact]
    public async Task Remove_AlsoDeletesProviderRenewalReference()
    {
        var store = new AudibleOfflineLicenseKeySetStore(new MemorySecretStore());
        await store.SaveProviderLicenseReferenceAsync(
            "account-a", "B012345678", "CR!reference", "1");
        await store.SaveAsync("account-a", "B012345678", [0x01]);

        await store.RemoveAsync("account-a", "B012345678");

        Assert.Null(await store.LoadProviderLicenseReferenceAsync("account-a", "B012345678"));
    }

    private sealed class MemorySecretStore : ISecretStore
    {
        private readonly Dictionary<string, string> values = new();
        public IReadOnlyCollection<string> Keys => values.Keys.ToArray();
        public IReadOnlyCollection<string> Values => values.Values.ToArray();

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
