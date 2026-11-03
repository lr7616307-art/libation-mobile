using System.Security.Cryptography;
using Libation.Mobile.Core;

namespace Libation.Mobile.Audible;

/// <summary>
/// Persists a complete Audible identity as one encrypted secret-store value.
/// The secret store is responsible for encryption at rest; this class never
/// writes the serialized snapshot to ordinary preferences or files.
/// </summary>
public sealed class AudibleIdentitySnapshotStore(ISecretStore secrets)
{
    public const string StorageKey = "audible_identity_v1";

    public async Task SaveAsync(
        AudibleIdentitySnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var serialized = AudibleIdentitySnapshotSerializer.Serialize(snapshot);
        await secrets.SetAsync(StorageKey, serialized, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns null when no identity exists or when its encrypted payload
    /// cannot be decoded, decrypted, or validated. Other storage failures are
    /// allowed to propagate so callers do not mistake an unavailable store for sign-out.
    /// </summary>
    public async Task<AudibleIdentitySnapshot?> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            var serialized = await secrets.GetAsync(StorageKey, cancellationToken).ConfigureAwait(false);
            if (serialized is null)
                return null;

            return AudibleIdentitySnapshotSerializer.Deserialize(serialized);
        }
        catch (InvalidDataException)
        {
            return null;
        }
        catch (CryptographicException)
        {
            // An authenticated-encryption failure means the saved identity is unusable.
            return null;
        }
        catch (FormatException)
        {
            // Invalid Base64 or another malformed storage encoding is corruption, not sign-out.
            return null;
        }
    }

    public Task ClearAsync(CancellationToken cancellationToken = default) =>
        secrets.RemoveAsync(StorageKey, cancellationToken);
}
