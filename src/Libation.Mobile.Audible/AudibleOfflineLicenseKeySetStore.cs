using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Libation.Mobile.Core;

namespace Libation.Mobile.Audible;

/// <summary>
/// Stores the opaque Android MediaDrm key-set identifier and protected DASH
/// playback reference through the encrypted secret store. DRM challenges,
/// provider licence responses and content keys are never persisted here.
/// </summary>
public sealed class AudibleOfflineLicenseKeySetStore(ISecretStore secrets)
{
    private const string StorageKeyPrefix = "audible_widevine_keyset_v1_";
    private const int MaximumKeySetIdBytes = 1024;
    private const int MaximumManifestUriCharacters = 8192;
    private const int MaximumStreamKeys = 128;
    private const string ManifestStorageKeySuffix = "_dash_manifest_v1";
    private const string ManifestCompleteStorageKeySuffix = "_dash_manifest_complete_v1";
    private static readonly Regex AsinPattern = new(
        "^[A-Za-z0-9]{10}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public async Task SaveAsync(
        string accountId,
        string asin,
        byte[] keySetId,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(accountId, asin);
        ArgumentNullException.ThrowIfNull(keySetId);
        if (keySetId.Length is 0 or > MaximumKeySetIdBytes)
            throw new ArgumentOutOfRangeException(
                nameof(keySetId), "The Android DRM key-set identifier has an unsupported size.");

        cancellationToken.ThrowIfCancellationRequested();
        var encoded = Convert.ToBase64String(keySetId.ToArray());
        await secrets.SetAsync(GetStorageKey(accountId, asin), encoded, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<byte[]?> LoadAsync(
        string accountId,
        string asin,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(accountId, asin);
        cancellationToken.ThrowIfCancellationRequested();

        var encoded = await secrets.GetAsync(
            GetStorageKey(accountId, asin), cancellationToken).ConfigureAwait(false);
        if (encoded is null)
            return null;

        byte[] keySetId;
        try
        {
            keySetId = Convert.FromBase64String(encoded);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException(
                "The stored Android DRM key-set identifier is malformed.", exception);
        }

        if (keySetId.Length is 0 or > MaximumKeySetIdBytes)
            throw new InvalidDataException(
                "The stored Android DRM key-set identifier has an unsupported size.");

        return keySetId;
    }

    /// <summary>
    /// Stores only the short-lived HTTPS manifest reference and selected audio
    /// stream indexes. The payload is encrypted by ISecretStore and bounded to
    /// the secret store's per-value size limit.
    /// </summary>
    public async Task SaveDashManifestAsync(
        string accountId,
        string asin,
        Uri manifestUri,
        IReadOnlyList<AudibleWidevineStreamKey> streamKeys,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(accountId, asin);
        ValidateManifestReference(manifestUri, streamKeys);
        cancellationToken.ThrowIfCancellationRequested();

        var payload = new StoredDashManifest(
            manifestUri.AbsoluteUri,
            streamKeys.Select(key => new StoredStreamKey(
                key.PeriodIndex, key.GroupIndex, key.TrackIndex)).ToArray());
        var json = JsonSerializer.Serialize(payload);
        if (Encoding.UTF8.GetByteCount(json) > 12 * 1024)
            throw new InvalidDataException("The Audible DASH playback reference exceeds the secure storage limit.");

        await secrets.SetAsync(
            GetManifestStorageKey(accountId, asin), json, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AudibleWidevineOfflineMediaReference?> LoadDashManifestAsync(
        string accountId,
        string asin,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(accountId, asin);
        cancellationToken.ThrowIfCancellationRequested();

        var json = await secrets.GetAsync(
            GetManifestStorageKey(accountId, asin), cancellationToken).ConfigureAwait(false);
        if (json is null)
            return null;
        if (Encoding.UTF8.GetByteCount(json) > 12 * 1024)
            throw new InvalidDataException("The stored Audible DASH playback reference is oversized.");

        StoredDashManifest? payload;
        try
        {
            payload = JsonSerializer.Deserialize<StoredDashManifest>(json);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The stored Audible DASH playback reference is malformed.", exception);
        }

        if (payload is null ||
            !Uri.TryCreate(payload.ManifestUri, UriKind.Absolute, out var manifestUri))
            throw new InvalidDataException("The stored Audible DASH manifest URI is invalid.");

        var streamKeys = payload.StreamKeys?
            .Select(key => new AudibleWidevineStreamKey(key.PeriodIndex, key.GroupIndex, key.TrackIndex))
            .ToArray();
        if (streamKeys is null)
            throw new InvalidDataException("The stored Audible DASH stream selection is missing.");

        ValidateManifestReference(manifestUri, streamKeys);
        return new AudibleWidevineOfflineMediaReference(manifestUri, streamKeys);
    }

    public async Task MarkDashDownloadCompleteAsync(
        string accountId,
        string asin,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(accountId, asin);
        cancellationToken.ThrowIfCancellationRequested();
        await secrets.SetAsync(
            GetManifestCompleteStorageKey(accountId, asin), "1", cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> IsDashDownloadCompleteAsync(
        string accountId,
        string asin,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(accountId, asin);
        cancellationToken.ThrowIfCancellationRequested();
        var value = await secrets.GetAsync(
            GetManifestCompleteStorageKey(accountId, asin), cancellationToken).ConfigureAwait(false);
        return string.Equals(value, "1", StringComparison.Ordinal);
    }

    public async Task RemoveDashManifestAsync(
        string accountId,
        string asin,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(accountId, asin);
        await secrets.RemoveAsync(GetManifestStorageKey(accountId, asin), cancellationToken).ConfigureAwait(false);
        await secrets.RemoveAsync(GetManifestCompleteStorageKey(accountId, asin), cancellationToken).ConfigureAwait(false);
    }

    public Task RemoveAsync(
        string accountId,
        string asin,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(accountId, asin);
        return secrets.RemoveAsync(GetStorageKey(accountId, asin), cancellationToken);
    }

    private static string GetManifestStorageKey(string accountId, string asin)
        => GetStorageKey(accountId, asin) + ManifestStorageKeySuffix;

    private static string GetManifestCompleteStorageKey(string accountId, string asin)
        => GetStorageKey(accountId, asin) + ManifestCompleteStorageKeySuffix;

    private static string GetStorageKey(string accountId, string asin)
    {
        var identity = Encoding.UTF8.GetBytes(
            accountId.Trim() + "\n" + asin.ToUpperInvariant());
        var digest = SHA256.HashData(identity);
        return StorageKeyPrefix + Convert.ToHexString(digest).ToLowerInvariant();
    }

    private static void ValidateManifestReference(
        Uri manifestUri,
        IReadOnlyList<AudibleWidevineStreamKey> streamKeys)
    {
        ArgumentNullException.ThrowIfNull(manifestUri);
        ArgumentNullException.ThrowIfNull(streamKeys);
        if (!manifestUri.IsAbsoluteUri ||
            !string.Equals(manifestUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            manifestUri.AbsoluteUri.Length > MaximumManifestUriCharacters ||
            manifestUri.UserInfo.Length != 0)
            throw new ArgumentException(
                "The Audible DASH manifest reference must be a bounded HTTPS URI.", nameof(manifestUri));

        if (streamKeys.Count is 0 or > MaximumStreamKeys ||
            streamKeys.Any(key =>
                key.PeriodIndex < 0 || key.GroupIndex < 0 || key.TrackIndex < 0))
            throw new ArgumentException(
                "The Audible DASH audio stream selection is invalid.", nameof(streamKeys));
    }

    private static void ValidateIdentity(string accountId, string asin)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        if (accountId.Length > 512)
            throw new ArgumentOutOfRangeException(nameof(accountId));
        if (string.IsNullOrWhiteSpace(asin) || !AsinPattern.IsMatch(asin))
            throw new ArgumentException(
                "The Audible audiobook identifier is not a valid 10-character ASIN.",
                nameof(asin));
    }

    private sealed record StoredDashManifest(string ManifestUri, StoredStreamKey[] StreamKeys);
    private sealed record StoredStreamKey(int PeriodIndex, int GroupIndex, int TrackIndex);
}

public sealed record AudibleWidevineOfflineMediaReference(
    Uri ManifestUri,
    IReadOnlyList<AudibleWidevineStreamKey> StreamKeys);
