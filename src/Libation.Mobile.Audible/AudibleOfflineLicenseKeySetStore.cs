using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Libation.Mobile.Core;

namespace Libation.Mobile.Audible;

/// <summary>
/// Stores only the opaque offline key-set identifier returned by Android MediaDrm.
/// The secret-store implementation is responsible for encryption at rest. This
/// class never stores DRM challenges, licence responses, or content keys.
/// </summary>
public sealed class AudibleOfflineLicenseKeySetStore(ISecretStore secrets)
{
    private const string StorageKeyPrefix = "audible_widevine_keyset_v1_";
    private const int MaximumKeySetIdBytes = 1024;
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

        // Hash the account/title pair so preference keys do not reveal which
        // Audible title or account owns a local offline licence record.
        var storageKey = GetStorageKey(accountId, asin);
        var encoded = Convert.ToBase64String(keySetId.ToArray());
        await secrets.SetAsync(storageKey, encoded, cancellationToken).ConfigureAwait(false);
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

    public Task RemoveAsync(
        string accountId,
        string asin,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(accountId, asin);
        return secrets.RemoveAsync(GetStorageKey(accountId, asin), cancellationToken);
    }

    private static string GetStorageKey(string accountId, string asin)
    {
        var identity = Encoding.UTF8.GetBytes(
            accountId.Trim() + "\n" + asin.ToUpperInvariant());
        var digest = SHA256.HashData(identity);
        return StorageKeyPrefix + Convert.ToHexString(digest).ToLowerInvariant();
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
}
