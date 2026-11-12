using System.Security.Cryptography;
using Libation.Mobile.Core;

namespace Libation.Mobile.Audible;

/// <summary>
/// Raised when encrypted Audible credentials exist but cannot be trusted or restored.
/// The user must sign in again; this is deliberately distinct from a missing identity.
/// </summary>
public sealed class AudibleIdentityRecoveryRequiredException : InvalidOperationException
{
    public AudibleIdentityRecoveryRequiredException(string message, Exception innerException)
        : base(message, innerException) { }
}

/// <summary>
/// Persists a complete Audible identity as one encrypted secret-store value.
/// The secret store is responsible for encryption at rest; this class never
/// writes the serialized snapshot to ordinary preferences or files.
/// </summary>
public sealed class AudibleIdentitySnapshotStore(ISecretStore secrets)
{
    public const string StorageKey = "audible_identity_v1";

    // One lifecycle boundary per encrypted identity store. Every authenticator and
    // API-session maintainer created for this store must share this coordinator.
    public AudibleIdentityLifecycleCoordinator LifecycleCoordinator { get; } = new();

    public async Task SaveAsync(
        AudibleIdentitySnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var serialized = AudibleIdentitySnapshotSerializer.Serialize(snapshot);
        await secrets.SetAsync(StorageKey, serialized, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns null only when no identity exists. If an encrypted record exists
    /// but its key, authentication tag, schema, or contents are invalid, throws
    /// AudibleIdentityRecoveryRequiredException so the UI can request sign-in again.
    /// Unavailable storage failures and cancellation propagate unchanged.
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
        catch (InvalidDataException ex)
        {
            throw new AudibleIdentityRecoveryRequiredException(
                "Saved Audible credentials are invalid or use an outdated device-registration profile. Sign in again to restore this device.", ex);
        }
        catch (CryptographicException ex)
        {
            throw new AudibleIdentityRecoveryRequiredException(
                "Saved Audible credentials cannot be decrypted. Sign in again to restore this device.", ex);
        }
    }

    public Task ClearAsync(CancellationToken cancellationToken = default) =>
        secrets.RemoveAsync(StorageKey, cancellationToken);
}
