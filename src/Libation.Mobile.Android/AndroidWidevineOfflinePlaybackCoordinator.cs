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
    private const int OfflineLicenseStateUnknown = 0;
    private const int OfflineLicenseStateUsable = 1;
    private const int OfflineLicenseStateReleased = 2;

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
        await RecoverPromotableRenewalKeySetAsync(asin, cancellationToken).ConfigureAwait(false);
        var keySetId = await keySetStore.LoadAsync(accountId, asin, cancellationToken)
            .ConfigureAwait(false);
        if (keySetId is null)
            return false;

        try
        {
            using var drmSession = new AndroidWidevineOfflineSession();
            var state = drmSession.GetOfflineLicenseState(keySetId);
            return state switch
            {
                OfflineLicenseStateUsable => true,
                OfflineLicenseStateReleased => false,
                OfflineLicenseStateUnknown => throw new InvalidOperationException(
                    "Android could not verify the saved Audible offline licence state."),
                _ => throw new InvalidDataException(
                    "Android returned an unsupported Audible offline licence state.")
            };
        }
        catch (Java.Lang.IllegalArgumentException)
        {
            // The key-set identifier no longer maps to a persistent device licence.
            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(keySetId);
        }
    }

    /// <summary>
    /// Performs a diagnostic Audible renewal exchange for an existing offline
    /// key set. It sends Android's opaque renewal challenge to Audible and
    /// returns only non-secret response metadata. The returned licence is
    /// deliberately NOT supplied to MediaDrm or persisted; this probe therefore
    /// cannot replace or invalidate the currently active key set.
    /// </summary>
    public async Task<AudibleWidevineRenewalProbeResult> ProbeOfflineLicenseRenewalAsync(
        string asin,
        CancellationToken cancellationToken = default)
    {
        await RecoverPromotableRenewalKeySetAsync(asin, cancellationToken).ConfigureAwait(false);

        var keySetId = await keySetStore.LoadAsync(accountId, asin, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                "There is no saved Audible offline licence to renew.");

        byte[]? challenge = null;
        try
        {
            var reference = await keySetStore.LoadProviderLicenseReferenceAsync(
                accountId, asin, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    "The saved Audible content reference is missing; a renewal challenge cannot be built.");

            using var drmSession = new AndroidWidevineOfflineSession();
            var state = drmSession.GetOfflineLicenseState(keySetId);
            if (state != OfflineLicenseStateUsable)
                throw new InvalidOperationException(
                    state == OfflineLicenseStateReleased
                        ? "The saved Audible offline licence is marked for release and cannot be renewed."
                        : state == OfflineLicenseStateUnknown
                            ? "Android could not verify the saved Audible offline licence state."
                            : "The saved Audible offline licence is not in a usable state.");

            challenge = drmSession.CreateOfflineRenewalRequest(keySetId);
            var refresh = await source.RefreshWidevineLicenseAsync(
                asin, reference, challenge, cancellationToken).ConfigureAwait(false);

            // Never expose or store the opaque licence payload in diagnostics.
            // Status is intentionally reported verbatim: this build does not
            // assume a success code for Audible's community-documented endpoint.
            return new AudibleWidevineRenewalProbeResult(
                asin,
                refresh.StatusCode,
                !string.IsNullOrWhiteSpace(refresh.License),
                !string.IsNullOrWhiteSpace(refresh.LicenseId),
                refresh.LicenseRefreshDate,
                refresh.LicenseRemovalDate,
                refresh.LicenseExpirationDate,
                refresh.ErrorMessage)
            {
                GrantedRight = refresh.GrantedRight,
                AccessExpiryDate = refresh.AccessExpiryDate,
                LicenseDenialReasons = refresh.LicenseDenialReasons
            };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(keySetId);
            if (challenge is not null)
                CryptographicOperations.ZeroMemory(challenge);
        }
    }

    /// <summary>
    /// Recovers a renewal that was accepted by MediaDrm but whose active
    /// encrypted record was not promoted before process termination. Only a
    /// platform-usable, non-expired staged key set is promoted. Unknown,
    /// released, missing or expired staged state is retained for diagnosis and
    /// never replaces the current active record.
    /// </summary>
    private async Task RecoverPromotableRenewalKeySetAsync(
        string asin,
        CancellationToken cancellationToken)
    {
        var pendingKeySetId = await keySetStore.LoadPendingRenewalKeySetAsync(
            accountId, asin, cancellationToken).ConfigureAwait(false);
        if (pendingKeySetId is null)
            return;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var drmSession = new AndroidWidevineOfflineSession();
            if (drmSession.GetOfflineLicenseState(pendingKeySetId) != OfflineLicenseStateUsable)
                return;

            var duration = drmSession.GetOfflineLicenseDuration(pendingKeySetId);
            if (duration.IsDefinitelyExpired)
                return;

            await keySetStore.PromotePendingRenewalKeySetAsync(
                accountId, asin, cancellationToken).ConfigureAwait(false);
        }
        catch (Java.Lang.IllegalArgumentException)
        {
            // The staged identifier is not present in Android's persistent DRM
            // store. Keep both records intact; do not guess which is authoritative.
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pendingKeySetId);
        }
    }

    public async Task<bool> HasOfflineMediaAsync(
        string asin,
        CancellationToken cancellationToken = default)
    {
        var hasCompletionMarker = await keySetStore.IsDashDownloadCompleteAsync(accountId, asin, cancellationToken)
            .ConfigureAwait(false);
        if (!hasCompletionMarker)
            return false;

        var reference = await keySetStore.LoadDashManifestAsync(accountId, asin, cancellationToken)
            .ConfigureAwait(false);
        if (reference is null)
            return false;

        var offlineStore = new AndroidWidevineDashOfflineStore(global::Android.App.Application.Context);
        var hasCachedResources = await offlineStore.HasCachedResourcesAsync(accountId, asin, cancellationToken)
            .ConfigureAwait(false);
        if (!hasCachedResources)
        {
            await keySetStore.InvalidateDashDownloadCompleteAsync(accountId, asin, cancellationToken)
                .ConfigureAwait(false);
        }
        return hasCachedResources;
    }

    /// <summary>
    /// Obtains an Audible-issued Widevine manifest, creates a platform offline
    /// challenge, supplies Audible's opaque response to MediaDrm, and persists
    /// only the resulting key-set ID through the Keystore-backed secret store.
    /// Missing/released local state is cleaned before a new provider grant;
    /// confirmed expiry is retained until a provider renewal/release flow exists.
    /// An unknown DRM state fails closed instead of overwriting it.
    /// </summary>
    public async Task AcquireOfflineLicenseAsync(
        string asin,
        CancellationToken cancellationToken = default)
    {
        if (await RemoveInactiveLicenseBeforeReplacementAsync(asin, cancellationToken)
                .ConfigureAwait(false))
            return;

        var content = await source.ResolveWidevineAsync(asin, cancellationToken).ConfigureAwait(false);
        await keySetStore.SaveProviderLicenseReferenceAsync(
            accountId, asin, content.Acr, content.FileVersion, cancellationToken).ConfigureAwait(false);
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
            await keySetStore.SaveProviderLicenseReferenceAsync(
                accountId, asin, content.Acr, content.FileVersion, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // If MediaDrm accepted a persistent licence but secure persistence
            // failed, remove that exact key set rather than only clearing the
            // active session's temporary keys.
            if (!persisted && keySetId is not null)
            {
                try { drmSession.RemoveOfflineLicense(keySetId); }
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

    private async Task<bool> RemoveInactiveLicenseBeforeReplacementAsync(
        string asin,
        CancellationToken cancellationToken)
    {
        var existingKeySetId = await keySetStore.LoadAsync(accountId, asin, cancellationToken)
            .ConfigureAwait(false);
        if (existingKeySetId is null)
            return false;

        try
        {
            using var drmSession = new AndroidWidevineOfflineSession();
            var state = drmSession.GetOfflineLicenseState(existingKeySetId);
            if (state == OfflineLicenseStateUsable)
            {
                var duration = drmSession.GetOfflineLicenseDuration(existingKeySetId);
                if (duration.IsDefinitelyExpired)
                    throw new InvalidOperationException(
                        "The saved Audible offline licence has expired. VØRATH verified the expiry but cannot renew this provider licence yet; the protected copy has been preserved.");
                return true;
            }

            if (state == OfflineLicenseStateUnknown)
                throw new InvalidOperationException(
                    "Android could not verify the saved Audible offline licence. The existing licence was retained; try again later.");

            if (state == OfflineLicenseStateReleased)
                drmSession.RemoveOfflineLicense(existingKeySetId);
        }
        catch (Java.Lang.IllegalArgumentException)
        {
            // The stored identifier no longer exists in the platform DRM store.
        }
        finally
        {
            CryptographicOperations.ZeroMemory(existingKeySetId);
        }

        new AndroidWidevineDashOfflineStore(global::Android.App.Application.Context)
            .RemoveTitle(accountId, asin);
        await keySetStore.RemoveAsync(accountId, asin, cancellationToken)
            .ConfigureAwait(false);
        return false;
    }

    /// <summary>
    /// Checks the platform's offline-key state and Widevine-specific remaining
    /// duration fields. If the device does not expose duration metadata, the
    /// normal MediaDrm state remains authoritative.
    /// </summary>
    private static void EnsureOfflineLicenseNotExpired(byte[] keySetId)
    {
        using var drmSession = new AndroidWidevineOfflineSession();
        var state = drmSession.GetOfflineLicenseState(keySetId);
        if (state == OfflineLicenseStateUnknown)
            throw new InvalidOperationException(
                "Android could not verify the saved Audible offline licence state.");
        if (state == OfflineLicenseStateReleased)
            throw new InvalidOperationException(
                "The saved Audible offline licence has been marked for release and cannot be used.");
        if (state != OfflineLicenseStateUsable)
            throw new InvalidDataException(
                "Android returned an unsupported Audible offline licence state.");

        var duration = drmSession.GetOfflineLicenseDuration(keySetId);
        if (duration.IsDefinitelyExpired)
            throw new InvalidOperationException(
                "The saved Audible offline licence has expired. VØRATH verified the expiry but cannot renew this provider licence yet; the protected copy has been preserved.");
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

        var currentKeySetId = await keySetStore.LoadAsync(accountId, asin, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("The Audible offline key set was not saved.");
        try
        {
            EnsureOfflineLicenseNotExpired(currentKeySetId);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(currentKeySetId);
        }

        if (await HasOfflineMediaAsync(asin, cancellationToken).ConfigureAwait(false))
            return;

        var content = await source.ResolveWidevineAsync(asin, cancellationToken).ConfigureAwait(false);
        await keySetStore.SaveProviderLicenseReferenceAsync(
            accountId, asin, content.Acr, content.FileVersion, cancellationToken).ConfigureAwait(false);
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
            if (!await offlineStore.HasCachedResourcesAsync(accountId, asin, cancellationToken)
                    .ConfigureAwait(false))
                throw new InvalidDataException(
                    "The Audible DASH download returned without every selected initialization, index and media byte range being cached. Retry the download.");

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
        if (!await HasOfflineLicenseAsync(asin, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException(
                "The Audible offline licence is missing, expired or inactive. Connect to the internet to request a new licence.");

        var keySetId = await keySetStore.LoadAsync(accountId, asin, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                "No saved Audible Widevine offline licence exists for this audiobook.");

        try
        {
            EnsureOfflineLicenseNotExpired(keySetId);

            var mediaReference = await keySetStore.LoadDashManifestAsync(accountId, asin, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    "No offline Audible DASH media is saved for this audiobook.");

            if (!await keySetStore.IsDashDownloadCompleteAsync(accountId, asin, cancellationToken)
                    .ConfigureAwait(false) ||
                !await new AndroidWidevineDashOfflineStore(global::Android.App.Application.Context)
                    .HasCachedResourcesAsync(accountId, asin, cancellationToken).ConfigureAwait(false))
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
    /// Removes one title's persistent licence from Android and deletes its
    /// encrypted key-set metadata and DASH cache. Android's direct removal API
    /// is device-local and does not confirm release to Audible's licence server.
    /// </summary>
    public async Task RemoveOfflineLicenseAsync(
        string asin,
        CancellationToken cancellationToken = default)
    {
        await AndroidLocalAudioPlaybackService.StopWidevinePlaybackIfCurrentAsync(
            asin, cancellationToken).ConfigureAwait(false);

        var keySetId = await keySetStore.LoadAsync(accountId, asin, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (keySetId is not null)
            {
                try
                {
                    using var drmSession = new AndroidWidevineOfflineSession();
                    drmSession.RemoveOfflineLicense(keySetId);
                }
                catch (Java.Lang.IllegalArgumentException)
                {
                    // The platform no longer has this key set; local metadata can
                    // still be safely removed after confirming that condition.
                }
            }

            new AndroidWidevineDashOfflineStore(global::Android.App.Application.Context)
                .RemoveTitle(accountId, asin);
            await keySetStore.RemoveAsync(accountId, asin, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            if (keySetId is not null)
                CryptographicOperations.ZeroMemory(keySetId);
        }
    }

    /// <summary>
    /// Cleans every offline title indexed for the signed-in Audible account.
    /// The snapshot of ASINs is taken from encrypted storage, and each removal
    /// updates the index only after that title's local key set and cache are gone.
    /// </summary>
    public async Task RemoveAllOfflineLicensesAsync(CancellationToken cancellationToken = default)
    {
        var asins = await keySetStore.ListAccountTitlesAsync(accountId, cancellationToken)
            .ConfigureAwait(false);
        foreach (var asin in asins)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await RemoveOfflineLicenseAsync(asin, cancellationToken).ConfigureAwait(false);
        }
    }
}
