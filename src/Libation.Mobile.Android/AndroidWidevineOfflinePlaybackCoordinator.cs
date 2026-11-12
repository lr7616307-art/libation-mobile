using System.Security.Cryptography;
using Libation.Mobile.Audible;
using Libation.Mobile.Core;

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

    /// <summary>
    /// Obtains an Audible-issued Widevine manifest, creates a platform offline
    /// challenge, supplies Audible's opaque response to MediaDrm, and persists
    /// only the resulting key-set ID through the Keystore-backed secret store.
    /// This does not download DASH media segments.
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
    /// Starts DRM-aware DASH playback using a fresh provider manifest and a
    /// previously persisted offline key-set ID. DASH media itself is still
    /// fetched by Media3; this is not yet a local/offline segment cache.
    /// </summary>
    public async Task PlayAsync(
        string asin,
        string title,
        long startPositionMilliseconds = 0,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        var keySetId = await keySetStore.LoadAsync(accountId, asin, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                "No saved Audible Widevine offline licence exists for this audiobook.");

        try
        {
            var content = await source.ResolveWidevineAsync(asin, cancellationToken)
                .ConfigureAwait(false);
            var manifest = await manifestClient.FetchAsync(content.ManifestUri, cancellationToken)
                .ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            AndroidLocalAudioPlayer.StartWidevineDash(
                content.ManifestUri, keySetId, title, startPositionMilliseconds);
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
            await keySetStore.RemoveAsync(accountId, asin, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(keySetId);
        }
    }
}
