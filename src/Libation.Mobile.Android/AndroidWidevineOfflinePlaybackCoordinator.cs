using System.Security.Cryptography;
using Libation.Mobile.Audible;
using Libation.Mobile.Core;
using Libation.Mobile.UI.Views;

namespace Libation.Mobile.Android;

/// <summary>
/// Coordinates the provider-authorized Audible Widevine licence flow with Android
/// MediaDrm and Media3 playback. Only the opaque offline key-set ID is persisted.
/// </summary>
public sealed class AndroidWidevineOfflinePlaybackCoordinator : IAudibleWidevinePlaybackCoordinator
{
    private readonly AudibleAudioDownloadSource source;
    private readonly AudibleWidevineManifestClient manifestClient;
    private readonly AudibleOfflineLicenseKeySetStore keySetStore;
    private readonly string accountId;

    public AndroidWidevineOfflinePlaybackCoordinator(
        AudibleAudioDownloadSource source,
        AudibleWidevineManifestClient manifestClient,
        AudibleOfflineLicenseKeySetStore keySetStore,
        string accountId)
    {
        this.source = source ?? throw new ArgumentNullException(nameof(source));
        this.manifestClient = manifestClient ?? throw new ArgumentNullException(nameof(manifestClient));
        this.keySetStore = keySetStore ?? throw new ArgumentNullException(nameof(keySetStore));
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        this.accountId = accountId;
    }

    public async Task<bool> HasOfflineLicenseAsync(
        string asin,
        CancellationToken cancellationToken = default)
    {
        var keySetId = await keySetStore.LoadAsync(accountId, asin, cancellationToken)
            .ConfigureAwait(false);
        if (keySetId is null)
            return false;

        CryptographicOperations.ZeroMemory(keySetId);
        return true;
    }

    public async Task<bool> HasOfflineMediaAsync(
        string asin,
        CancellationToken cancellationToken = default)
    {
        var isComplete = await keySetStore.IsDashDownloadCompleteAsync(accountId, asin, cancellationToken)
            .ConfigureAwait(false);
        if (!isComplete)
            return false;

        var reference = await keySetStore.LoadDashManifestAsync(accountId, asin, cancellationToken)
            .ConfigureAwait(false);
        if (reference is null)
            return false;

        return new AndroidWidevineDashOfflineStore(global::Android.App.Application.Context)
            .HasCachedResources(accountId, asin);
    }

    /// <summary>
    /// Obtains an Audible-issued Widevine manifest, creates a platform offline
    /// challenge, supplies Audible's opaque response to MediaDrm, and persists
    /// only the resulting key-set ID through the Keystore-backed secret store.
    /// </summary>
    public async Task AcquireOfflineLicenseAsync(
        string asin,
        CancellationToken cancellationToken = default)
    {
        var content = await source.ResolveWidevineAsync(asin, cancellationToken).ConfigureAwait(false);
        var manifest = await manifestClient.FetchAsync(content.ManifestUri, cancellationToken)
            .ConfigureAwait(false);

        using var drmSession = new AndroidWidevineOfflineSession();
        byte[]? challenge = null;
        byte[]? response = null;
        byte[]? keySetId = null;
        var persisted = false;

        try
        {
            challenge = drmSession.CreateOfflineKeyRequest(
                manifest.InitializationData, manifest.MimeType);

            response = await source.RequestWidevineLicenseBytesAsync(
                asin, challenge, cancellationToken).ConfigureAwait(false);

            keySetId = drmSession.ProvideOfflineLicenseResponse(response);
            await keySetStore.SaveAsync(accountId, asin, keySetId, cancellationToken)
                .ConfigureAwait(false);
            persisted = true;
        }
        catch
        {
            // If MediaDrm accepted a licence but secure persistence failed, remove
            // the just-created device-side key set rather than leave an orphan.
            if (!persisted && keySetId is not null)
            {
                try { drmSession.RemoveOfflineKeys(); }
                catch (Exception) { /* Preserve the original failure; never log DRM state. */ }
            }
            throw;
        }
        finally
        {
            if (challenge is not null) CryptographicOperations.ZeroMemory(challenge);
            if (response is not null) CryptographicOperations.ZeroMemory(response);
            if (keySetId is not null) CryptographicOperations.ZeroMemory(keySetId);
        }
    }

    /// <summary>
    /// Downloads only the selected provider-encrypted DASH audio representation
    /// into the app-private Media3 cache. No segment is decrypted or exported.
    /// The encrypted manifest reference is saved before transfer so interrupted
    /// work can be retried without putting signed URLs in ordinary queue storage.
    /// </summary>
    public async Task DownloadOfflineMediaAsync(
        string asin,
        CancellationToken cancellationToken = default)
    {
        if (!await HasOfflineLicenseAsync(asin, cancellationToken).ConfigureAwait(false))
            await AcquireOfflineLicenseAsync(asin, cancellationToken).ConfigureAwait(false);

        if (await HasOfflineMediaAsync(asin, cancellationToken).ConfigureAwait(false))
            return;

        var content = await source.ResolveWidevineAsync(asin, cancellationToken).ConfigureAwait(false);
        var manifest = await manifestClient.FetchAsync(content.ManifestUri, cancellationToken)
            .ConfigureAwait(false);

        await keySetStore.SaveDashManifestAsync(
            accountId, asin, content.ManifestUri, manifest.AudioStreamKeys, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            var offlineStore = new AndroidWidevineDashOfflineStore(global::Android.App.Application.Context);
            await offlineStore.DownloadAsync(
                accountId, asin, content.ManifestUri, manifest.AudioStreamKeys, cancellationToken)
                .ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            await keySetStore.MarkDashDownloadCompleteAsync(accountId, asin, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            // Keep partial encrypted segments and the encrypted reference so the
            // next attempt can resume. The completion marker remains absent.
            throw;
        }
    }

    /// <summary>
    /// Starts DRM-aware DASH playback from the app-private encrypted Media3
    /// cache using the persisted Android Widevine offline key-set identifier.
    /// The cache-only source fails closed if any resource is missing.
    /// </summary>
    public async Task PlayAsync(
        string asin,
        string title,
        long startPositionMilliseconds = 0,
        bool autoPlay = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        var keySetId = await keySetStore.LoadAsync(accountId, asin, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                "No saved Audible Widevine offline licence exists for this audiobook.");

        try
        {
            var mediaReference = await keySetStore.LoadDashManifestAsync(accountId, asin, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    "No offline Audible DASH media is saved for this audiobook.");

            if (!await keySetStore.IsDashDownloadCompleteAsync(accountId, asin, cancellationToken)
                    .ConfigureAwait(false) ||
                !new AndroidWidevineDashOfflineStore(global::Android.App.Application.Context)
                    .HasCachedResources(accountId, asin))
                throw new InvalidOperationException(
                    "The Audible DASH download is incomplete. Connect to the internet and retry the offline download.");

            cancellationToken.ThrowIfCancellationRequested();
            var streamKeyValues = mediaReference.StreamKeys
                .SelectMany(key => new[] { key.PeriodIndex, key.GroupIndex, key.TrackIndex })
                .ToArray();

            AndroidLocalAudioPlayer.StartWidevineDash(
                mediaReference.ManifestUri,
                keySetId,
                title,
                accountId,
                asin,
                streamKeyValues,
                startPositionMilliseconds,
                autoPlay);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(keySetId);
        }
    }

    /// <summary>
    /// Releases the provider-authorized offline licence on this device before
    /// removing its encrypted local identifier. On failure, the identifier is
    /// retained so the operation can be retried.
    /// </summary>
    public async Task RemoveOfflineLicenseAsync(
        string asin,
        CancellationToken cancellationToken = default)
    {
        var keySetId = await keySetStore.LoadAsync(accountId, asin, cancellationToken)
            .ConfigureAwait(false);
        if (keySetId is null)
            return;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var drmSession = new AndroidWidevineOfflineSession();
            drmSession.RestoreOfflineKeys(keySetId);
            drmSession.RemoveOfflineKeys();
            new AndroidWidevineDashOfflineStore(global::Android.App.Application.Context)
                .RemoveTitle(accountId, asin);
            await keySetStore.RemoveDashManifestAsync(accountId, asin, cancellationToken)
                .ConfigureAwait(false);
            await keySetStore.RemoveAsync(accountId, asin, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(keySetId);
        }
    }
}
