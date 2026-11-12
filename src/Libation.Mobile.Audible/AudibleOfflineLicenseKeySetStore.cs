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
    private const string AccountTitlesStorageKeyPrefix = "audible_widevine_titles_v1_";
    private const int MaximumKeySetIdBytes = 1024;
    private const int MaximumManifestUriCharacters = 8192;
    private const int MaximumStreamKeys = 128;
    private const int MaximumAccountTitles = 5000;
    private const string ManifestStorageKeySuffix = "_dash_manifest_v1";
    private const string ManifestCompleteStorageKeySuffix = "_dash_manifest_complete_v1";
    private const string ProviderReferenceStorageKeySuffix = "_provider_license_reference_v1";
    private const string PendingRenewalStorageKeySuffix = "_pending_renewal_keyset_v1";
    private static readonly Regex AsinPattern = new(
        "^[A-Za-z0-9]{10}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private readonly SemaphoreSlim accountIndexGate = new(1, 1);

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
        await accountIndexGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Write the encrypted account/title index first. If writing the
            // key-set value subsequently fails, the extra index entry is harmless
            // and lets disconnect cleanup safely discover and prune the orphan.
            await AddAccountTitleUnderGateAsync(accountId, asin, cancellationToken)
                .ConfigureAwait(false);

            var encoded = Convert.ToBase64String(keySetId.ToArray());
            await secrets.SetAsync(GetStorageKey(accountId, asin), encoded, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            accountIndexGate.Release();
        }
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
    /// Stores a newly issued renewal key-set separately from the active key set.
    /// This staging record makes renewal recoverable if the app stops before
    /// the active identifier is replaced.
    /// </summary>
    public async Task StageRenewalKeySetAsync(
        string accountId,
        string asin,
        byte[] renewedKeySetId,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(accountId, asin);
        ArgumentNullException.ThrowIfNull(renewedKeySetId);
        if (renewedKeySetId.Length is 0 or > MaximumKeySetIdBytes)
            throw new ArgumentOutOfRangeException(
                nameof(renewedKeySetId), "The Android DRM key-set identifier has an unsupported size.");

        cancellationToken.ThrowIfCancellationRequested();
        await accountIndexGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await AddAccountTitleUnderGateAsync(accountId, asin, cancellationToken)
                .ConfigureAwait(false);
            await secrets.SetAsync(
                GetPendingRenewalStorageKey(accountId, asin),
                Convert.ToBase64String(renewedKeySetId),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            accountIndexGate.Release();
        }
    }

    public async Task<byte[]?> LoadPendingRenewalKeySetAsync(
        string accountId,
        string asin,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(accountId, asin);
        cancellationToken.ThrowIfCancellationRequested();
        var encoded = await secrets.GetAsync(
            GetPendingRenewalStorageKey(accountId, asin), cancellationToken).ConfigureAwait(false);
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
                "The staged Android DRM renewal key-set identifier is malformed.", exception);
        }

        if (keySetId.Length is 0 or > MaximumKeySetIdBytes)
            throw new InvalidDataException(
                "The staged Android DRM renewal key-set identifier has an unsupported size.");
        return keySetId;
    }

    /// <summary>
    /// Promotes the staged identifier only after it has been durably written.
    /// If writing the active record fails, the old active record and staged
    /// recovery record both remain available. Repeating promotion is safe.
    /// </summary>
    public async Task PromotePendingRenewalKeySetAsync(
        string accountId,
        string asin,
        CancellationToken cancellationToken = default)
    {
        var pending = await LoadPendingRenewalKeySetAsync(accountId, asin, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("No staged Audible renewal key set exists.");
        try
        {
            await SaveAsync(accountId, asin, pending, cancellationToken).ConfigureAwait(false);
            await secrets.RemoveAsync(
                GetPendingRenewalStorageKey(accountId, asin), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pending);
        }
    }

    public async Task DiscardPendingRenewalKeySetAsync(
        string accountId,
        string asin,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(accountId, asin);
        await secrets.RemoveAsync(
            GetPendingRenewalStorageKey(accountId, asin), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns the ASINs with key-set records for this account. The list itself
    /// is stored as an encrypted secret and its storage key contains only a hash
    /// of the account identifier. It enables deterministic logout cleanup.
    /// </summary>
    public async Task<IReadOnlyList<string>> ListAccountTitlesAsync(
        string accountId,
        CancellationToken cancellationToken = default)
    {
        ValidateAccountId(accountId);
        await accountIndexGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var titles = await ReadAccountTitlesUnderGateAsync(accountId, cancellationToken)
                .ConfigureAwait(false);
            return titles.OrderBy(value => value, StringComparer.Ordinal).ToArray();
        }
        finally
        {
            accountIndexGate.Release();
        }
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

        // Invalidate the previous completion marker before replacing its manifest
        // or stream selection. A failed new transfer must never look complete.
        await secrets.RemoveAsync(
            GetManifestCompleteStorageKey(accountId, asin), cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    /// Persists the minimum Audible content-reference values required by the
    /// documented licence-refresh request. These identifiers are encrypted
    /// through ISecretStore and are not placed in ordinary catalogue metadata.
    /// Missing values clear an older reference rather than leaving stale renewal
    /// metadata associated with a different content grant.
    /// </summary>
    public async Task SaveProviderLicenseReferenceAsync(
        string accountId,
        string asin,
        string? acr,
        long? fileVersion,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(accountId, asin);
        cancellationToken.ThrowIfCancellationRequested();

        var hasAcr = !string.IsNullOrWhiteSpace(acr);
        var hasFileVersion = fileVersion.HasValue && fileVersion.Value >= 0;
        if ((fileVersion.HasValue && fileVersion.Value < 0) || hasAcr != hasFileVersion)
            throw new ArgumentException(
                "Audible renewal metadata must include both ACR and file version, or neither.");

        var storageKey = GetProviderReferenceStorageKey(accountId, asin);
        if (!hasAcr)
        {
            await secrets.RemoveAsync(storageKey, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (acr!.Length > 8192)
            throw new ArgumentOutOfRangeException(
                nameof(acr), "The Audible content-reference metadata exceeds its supported size.");

        var json = JsonSerializer.Serialize(new StoredProviderLicenseReference(acr, fileVersion!.Value));
        if (Encoding.UTF8.GetByteCount(json) > 12 * 1024)
            throw new InvalidDataException("The Audible content-reference metadata exceeds the secure storage limit.");

        await secrets.SetAsync(storageKey, json, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AudibleWidevineProviderLicenseReference?> LoadProviderLicenseReferenceAsync(
        string accountId,
        string asin,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(accountId, asin);
        cancellationToken.ThrowIfCancellationRequested();

        var json = await secrets.GetAsync(
            GetProviderReferenceStorageKey(accountId, asin), cancellationToken).ConfigureAwait(false);
        if (json is null)
            return null;
        if (Encoding.UTF8.GetByteCount(json) > 12 * 1024)
            throw new InvalidDataException("The stored Audible content-reference metadata is oversized.");

        StoredProviderLicenseReference? value;
        try
        {
            value = JsonSerializer.Deserialize<StoredProviderLicenseReference>(json);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The stored Audible content-reference metadata is malformed.", exception);
        }

        if (value is null ||
            string.IsNullOrWhiteSpace(value.Acr) ||
            value.Acr.Length > 8192 ||
            value.FileVersion < 0)
            throw new InvalidDataException("The stored Audible content-reference metadata is invalid.");

        return new AudibleWidevineProviderLicenseReference(value.Acr, value.FileVersion);
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

    public async Task InvalidateDashDownloadCompleteAsync(
        string accountId,
        string asin,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(accountId, asin);
        cancellationToken.ThrowIfCancellationRequested();
        await secrets.RemoveAsync(
            GetManifestCompleteStorageKey(accountId, asin), cancellationToken).ConfigureAwait(false);
    }

    public async Task RemoveDashManifestAsync(
        string accountId,
        string asin,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(accountId, asin);
        await secrets.RemoveAsync(GetManifestStorageKey(accountId, asin), cancellationToken).ConfigureAwait(false);
        await secrets.RemoveAsync(GetManifestCompleteStorageKey(accountId, asin), cancellationToken).ConfigureAwait(false);
        await secrets.RemoveAsync(GetProviderReferenceStorageKey(accountId, asin), cancellationToken).ConfigureAwait(false);
    }

    public async Task RemoveAsync(
        string accountId,
        string asin,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(accountId, asin);
        await accountIndexGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Keep the index entry until all title records are removed. If storage
            // fails part-way through, the next disconnect can retry this ASIN.
            await secrets.RemoveAsync(GetStorageKey(accountId, asin), cancellationToken).ConfigureAwait(false);
            await secrets.RemoveAsync(GetManifestStorageKey(accountId, asin), cancellationToken).ConfigureAwait(false);
            await secrets.RemoveAsync(GetManifestCompleteStorageKey(accountId, asin), cancellationToken).ConfigureAwait(false);
            await secrets.RemoveAsync(GetProviderReferenceStorageKey(accountId, asin), cancellationToken).ConfigureAwait(false);
            await secrets.RemoveAsync(GetPendingRenewalStorageKey(accountId, asin), cancellationToken).ConfigureAwait(false);

            var titles = await ReadAccountTitlesUnderGateAsync(accountId, cancellationToken)
                .ConfigureAwait(false);
            if (titles.Remove(asin.ToUpperInvariant()))
                await WriteAccountTitlesUnderGateAsync(accountId, titles, cancellationToken)
                    .ConfigureAwait(false);
        }
        finally
        {
            accountIndexGate.Release();
        }
    }

    private async Task AddAccountTitleUnderGateAsync(
        string accountId,
        string asin,
        CancellationToken cancellationToken)
    {
        var titles = await ReadAccountTitlesUnderGateAsync(accountId, cancellationToken)
            .ConfigureAwait(false);
        var normalizedAsin = asin.ToUpperInvariant();
        if (titles.Contains(normalizedAsin))
            return;
        if (titles.Count >= MaximumAccountTitles)
            throw new InvalidDataException("The Audible offline-title index reached its safety limit.");

        titles.Add(normalizedAsin);
        await WriteAccountTitlesUnderGateAsync(accountId, titles, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<HashSet<string>> ReadAccountTitlesUnderGateAsync(
        string accountId,
        CancellationToken cancellationToken)
    {
        var json = await secrets.GetAsync(GetAccountTitlesStorageKey(accountId), cancellationToken)
            .ConfigureAwait(false);
        if (json is null)
            return new HashSet<string>(StringComparer.Ordinal);

        if (Encoding.UTF8.GetByteCount(json) > 128 * 1024)
            throw new InvalidDataException("The Audible offline-title index exceeds its supported size.");

        try
        {
            var values = JsonSerializer.Deserialize<string[]>(json)
                ?? throw new InvalidDataException("The Audible offline-title index is empty.");
            if (values.Length > MaximumAccountTitles ||
                values.Any(value => string.IsNullOrWhiteSpace(value) || !AsinPattern.IsMatch(value)))
                throw new InvalidDataException("The Audible offline-title index contains invalid entries.");

            var titles = new HashSet<string>(values.Select(value => value.ToUpperInvariant()), StringComparer.Ordinal);
            if (titles.Count != values.Length)
                throw new InvalidDataException("The Audible offline-title index contains duplicate entries.");
            return titles;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The Audible offline-title index is malformed.", exception);
        }
    }

    private async Task WriteAccountTitlesUnderGateAsync(
        string accountId,
        HashSet<string> titles,
        CancellationToken cancellationToken)
    {
        if (titles.Count == 0)
        {
            await secrets.RemoveAsync(GetAccountTitlesStorageKey(accountId), cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var json = JsonSerializer.Serialize(titles.OrderBy(value => value, StringComparer.Ordinal));
        if (Encoding.UTF8.GetByteCount(json) > 128 * 1024)
            throw new InvalidDataException("The Audible offline-title index exceeds its supported size.");
        await secrets.SetAsync(GetAccountTitlesStorageKey(accountId), json, cancellationToken)
            .ConfigureAwait(false);
    }

    private static string GetAccountTitlesStorageKey(string accountId)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(accountId.Trim()));
        return AccountTitlesStorageKeyPrefix + Convert.ToHexString(digest).ToLowerInvariant();
    }

    private static string GetManifestStorageKey(string accountId, string asin)
        => GetStorageKey(accountId, asin) + ManifestStorageKeySuffix;

    private static string GetManifestCompleteStorageKey(string accountId, string asin)
        => GetStorageKey(accountId, asin) + ManifestCompleteStorageKeySuffix;

    private static string GetProviderReferenceStorageKey(string accountId, string asin)
        => GetStorageKey(accountId, asin) + ProviderReferenceStorageKeySuffix;

    private static string GetPendingRenewalStorageKey(string accountId, string asin)
        => GetStorageKey(accountId, asin) + PendingRenewalStorageKeySuffix;

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

    private static void ValidateAccountId(string accountId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        if (accountId.Length > 512)
            throw new ArgumentOutOfRangeException(nameof(accountId));
    }

    private static void ValidateIdentity(string accountId, string asin)
    {
        ValidateAccountId(accountId);
        if (string.IsNullOrWhiteSpace(asin) || !AsinPattern.IsMatch(asin))
            throw new ArgumentException(
                "The Audible audiobook identifier is not a valid 10-character ASIN.",
                nameof(asin));
    }

    private sealed record StoredDashManifest(string ManifestUri, StoredStreamKey[] StreamKeys);
    private sealed record StoredStreamKey(int PeriodIndex, int GroupIndex, int TrackIndex);
    private sealed record StoredProviderLicenseReference(string Acr, long FileVersion);
}

public sealed record AudibleWidevineProviderLicenseReference(string Acr, long FileVersion);

public sealed record AudibleWidevineOfflineMediaReference(
    Uri ManifestUri,
    IReadOnlyList<AudibleWidevineStreamKey> StreamKeys);
