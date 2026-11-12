using System.Security.Cryptography;
using Android.Media;
using Android.OS;
using Java.Util;
using Libation.Mobile.Audible;

namespace Libation.Mobile.Android;

/// <summary>
/// Narrow wrapper around Android's Widevine MediaDrm offline-key lifecycle.
/// It exposes opaque challenge and key-set byte arrays only; it never parses
/// licence contents or accesses content keys.
/// </summary>
public sealed class AndroidWidevineOfflineSession : IDisposable
{
    private static readonly UUID WidevineUuid =
        UUID.FromString("edef8ba9-79d6-4ace-a3c8-27dcd51d21ed");

    private const int OfflineKeyRequestType = (int)MediaDrmKeyType.Offline;
    private const int ReleaseKeyRequestType = 3; // Android MediaDrm.KEY_TYPE_RELEASE
    private const int MaximumInitializationDataBytes = 1024 * 1024;
    private const int MaximumLicenseResponseBytes = 1024 * 1024;

    private readonly MediaDrm mediaDrm;
    private readonly byte[] sessionId;
    private bool hasPendingOfflineRequest;
    private bool disposed;

    public AndroidWidevineOfflineSession()
    {
        if (Build.VERSION.SdkInt < BuildVersionCodes.Q)
            throw new NotSupportedException(
                "VØRATH offline DRM storage requires Android 10 (API 29) or later.");

        if (!MediaDrm.IsCryptoSchemeSupported(WidevineUuid))
            throw new NotSupportedException(
                "This device does not report support for the Widevine DRM scheme.");

        mediaDrm = new MediaDrm(WidevineUuid);
        try
        {
            sessionId = mediaDrm.OpenSession();
            if (sessionId is null || sessionId.Length == 0)
                throw new InvalidOperationException(
                    "Android MediaDrm returned an empty Widevine session identifier.");
        }
        catch
        {
            try
            {
                mediaDrm.Close();
            }
            finally
            {
                mediaDrm.Dispose();
            }
            throw;
        }
    }

    /// <summary>
    /// Creates an opaque offline key request from the content initialization data
    /// supplied by a DRM-aware DASH parser. The returned bytes are sensitive and
    /// must remain transient; do not log or persist them.
    /// </summary>
    public byte[] CreateOfflineKeyRequest(byte[] initializationData, string mimeType)
    {
        EnsureNotDisposed();
        ArgumentNullException.ThrowIfNull(initializationData);
        ArgumentException.ThrowIfNullOrWhiteSpace(mimeType);

        if (initializationData.Length is 0 or > MaximumInitializationDataBytes)
            throw new ArgumentOutOfRangeException(
                nameof(initializationData), "The Widevine initialization data has an unsupported size.");

        if (hasPendingOfflineRequest)
            throw new InvalidOperationException(
                "This DRM session already has an outstanding offline key request.");

        var keyRequest = mediaDrm.GetKeyRequest(
            sessionId,
            initializationData,
            mimeType,
            OfflineKeyRequestType,
            optionalParameters: null);

        var challenge = keyRequest.GetData();
        if (challenge is null || challenge.Length == 0)
            throw new InvalidDataException(
                "Android MediaDrm returned an empty Widevine key request.");

        hasPendingOfflineRequest = true;
        return challenge.ToArray();
    }

    /// <summary>
    /// Creates an offline renewal challenge for a previously saved key set.
    /// Existing keys are restored into this session first; Android permits a
    /// null init/MIME type for a renewal request. The returned bytes remain
    /// opaque and must be sent only through the authenticated provider transport.
    /// </summary>
    public byte[] CreateOfflineRenewalRequest(byte[] keySetId)
    {
        EnsureNotDisposed();
        ArgumentNullException.ThrowIfNull(keySetId);
        if (keySetId.Length is 0 or > 1024)
            throw new ArgumentOutOfRangeException(nameof(keySetId));
        if (hasPendingOfflineRequest)
            throw new InvalidOperationException(
                "This DRM session already has an outstanding offline key request.");

        mediaDrm.RestoreKeys(sessionId, keySetId);
        var keyRequest = mediaDrm.GetKeyRequest(
            sessionId,
            init: null,
            mimeType: null,
            OfflineKeyRequestType,
            optionalParameters: null);
        var challenge = keyRequest.GetData();
        if (challenge is null || challenge.Length == 0)
            throw new InvalidDataException(
                "Android MediaDrm returned an empty Widevine renewal challenge.");

        hasPendingOfflineRequest = true;
        return challenge.ToArray();
    }

    /// <summary>
    /// Creates a provider release challenge for one saved offline key set.
    /// Android marks that key set RELEASED when this request is created, making
    /// it unusable until the provider's release response is supplied. Do not
    /// call until the authenticated provider release exchange is ready to run.
    /// </summary>
    public byte[] CreateOfflineReleaseRequest(byte[] keySetId)
    {
        EnsureNotDisposed();
        ArgumentNullException.ThrowIfNull(keySetId);
        if (keySetId.Length is 0 or > 1024)
            throw new ArgumentOutOfRangeException(nameof(keySetId));

        var keyRequest = mediaDrm.GetKeyRequest(
            keySetId,
            init: null,
            mimeType: null,
            ReleaseKeyRequestType,
            optionalParameters: null);
        var challenge = keyRequest.GetData();
        if (challenge is null || challenge.Length == 0)
            throw new InvalidDataException(
                "Android MediaDrm returned an empty Widevine release challenge.");

        return challenge.ToArray();
    }

    /// <summary>
    /// Completes a release exchange with the exact key-set scope used to create
    /// the challenge. Callers must delete the encrypted identifier/cache only
    /// after this method accepts the provider response. A rejected response
    /// leaves local state for recovery; it is never converted to local removal.
    /// </summary>
    public void ProvideOfflineReleaseResponse(byte[] keySetId, byte[] releaseResponse)
    {
        EnsureNotDisposed();
        ArgumentNullException.ThrowIfNull(keySetId);
        ArgumentNullException.ThrowIfNull(releaseResponse);
        if (keySetId.Length is 0 or > 1024)
            throw new ArgumentOutOfRangeException(nameof(keySetId));
        if (releaseResponse.Length is 0 or > MaximumLicenseResponseBytes)
            throw new ArgumentOutOfRangeException(nameof(releaseResponse));

        var responseScope = mediaDrm.ProvideKeyResponse(keySetId, releaseResponse);
        try
        {
            if (responseScope is { Length: > 0 })
                throw new InvalidDataException(
                    "Android MediaDrm returned an unexpected key-set identifier for a release response.");
        }
        finally
        {
            if (responseScope is not null)
                CryptographicOperations.ZeroMemory(responseScope);
        }
    }

    /// <summary>
    /// Supplies the provider's opaque response bytes to Android MediaDrm.
    /// Returns the opaque persistent key-set identifier; callers should store
    /// that identifier only through the encrypted offline-licence store.
    /// </summary>
    public byte[] ProvideOfflineLicenseResponse(byte[] licenseResponse)
    {
        EnsureNotDisposed();
        ArgumentNullException.ThrowIfNull(licenseResponse);

        if (!hasPendingOfflineRequest)
            throw new InvalidOperationException(
                "No offline Widevine key request is awaiting a response.");

        if (licenseResponse.Length is 0 or > MaximumLicenseResponseBytes)
            throw new ArgumentOutOfRangeException(
                nameof(licenseResponse), "The Widevine licence response has an unsupported size.");

        var keySetId = mediaDrm.ProvideKeyResponse(sessionId, licenseResponse);
        if (keySetId is null || keySetId.Length == 0)
            throw new InvalidDataException(
                "Android MediaDrm did not return a persistent offline key-set identifier.");

        hasPendingOfflineRequest = false;
        return keySetId.ToArray();
    }

    /// <summary>
    /// Restores a previously stored offline key set into this new DRM session.
    /// The key-set identifier must come from the encrypted app-private store.
    /// </summary>
    /// <summary>Removes the offline licence associated with the currently restored session.</summary>
    /// <summary>
    /// Reports Android's persistent offline-licence state. Unknown is kept
    /// distinct from released so a transient DRM/plugin error cannot cause
    /// VØRATH to overwrite a potentially valid saved key set.
    /// </summary>
    public int GetOfflineLicenseState(byte[] keySetId)
    {
        EnsureNotDisposed();
        ArgumentNullException.ThrowIfNull(keySetId);
        if (keySetId.Length is 0 or > 1024)
            throw new ArgumentOutOfRangeException(nameof(keySetId));
        return (int)mediaDrm.GetOfflineLicenseState(keySetId);
    }

    /// <summary>
    /// Restores the opaque key set into this session and reads Widevine's
    /// documented remaining-duration fields. Missing vendor fields remain
    /// unknown; callers must not treat them as a licence failure.
    /// </summary>
    public AudibleWidevineLicenseDuration GetOfflineLicenseDuration(byte[] keySetId)
    {
        EnsureNotDisposed();
        ArgumentNullException.ThrowIfNull(keySetId);
        if (keySetId.Length is 0 or > 1024)
            throw new ArgumentOutOfRangeException(nameof(keySetId));

        mediaDrm.RestoreKeys(sessionId, keySetId);
        var keyStatus = mediaDrm.QueryKeyStatus(sessionId);
        return AudibleWidevineLicenseDuration.FromKeyStatus(keyStatus);
    }

    /// <summary>
    /// Removes a persistent offline licence from this device. Android's direct
    /// removal API does not send a release confirmation to the licence server;
    /// use only for local cleanup when account identity is being disconnected
    /// or a key set is already inactive.
    /// </summary>
    public void RemoveOfflineLicense(byte[] keySetId)
    {
        EnsureNotDisposed();
        ArgumentNullException.ThrowIfNull(keySetId);
        if (keySetId.Length is 0 or > 1024)
            throw new ArgumentOutOfRangeException(nameof(keySetId));
        mediaDrm.RemoveOfflineLicense(keySetId);
        hasPendingOfflineRequest = false;
    }

    /// <summary>Removes only temporary keys held by this active DRM session.</summary>
    public void RemoveOfflineKeys()
    {
        EnsureNotDisposed();
        mediaDrm.RemoveKeys(sessionId);
        hasPendingOfflineRequest = false;
    }

    public void RestoreOfflineKeys(byte[] keySetId)
    {
        EnsureNotDisposed();
        ArgumentNullException.ThrowIfNull(keySetId);
        if (keySetId.Length is 0 or > 1024)
            throw new ArgumentOutOfRangeException(nameof(keySetId));

        mediaDrm.RestoreKeys(sessionId, keySetId);
    }

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;
        try
        {
            mediaDrm.CloseSession(sessionId);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(sessionId);
            try
            {
                mediaDrm.Close();
            }
            finally
            {
                mediaDrm.Dispose();
            }
        }
    }

    private void EnsureNotDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }
}
