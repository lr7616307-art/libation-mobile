using System.Security.Cryptography;
using Android.Media;
using Android.OS;
using Java.Util;

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
            mediaDrm.Close();
            mediaDrm.Dispose();
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

        var challenge = keyRequest.Data;
        if (challenge is null || challenge.Length == 0)
            throw new InvalidDataException(
                "Android MediaDrm returned an empty Widevine key request.");

        hasPendingOfflineRequest = true;
        return challenge.ToArray();
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
            mediaDrm.Close();
            mediaDrm.Dispose();
        }
    }

    private void EnsureNotDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }
}
