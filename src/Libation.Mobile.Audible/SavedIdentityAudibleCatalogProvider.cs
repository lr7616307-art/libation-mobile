using Libation.Mobile.Core;

namespace Libation.Mobile.Audible;

/// <summary>
/// Creates a catalogue provider from the identity already saved on this device.
/// It never initiates login or creates a device registration.
/// </summary>
public sealed class SavedIdentityAudibleCatalogProvider : ILibraryCatalogProvider
{
    private readonly AudibleIdentitySnapshotStore _snapshotStore;

    public SavedIdentityAudibleCatalogProvider(AudibleIdentitySnapshotStore snapshotStore)
    {
        _snapshotStore = snapshotStore ?? throw new ArgumentNullException(nameof(snapshotStore));
    }

    public async Task<IReadOnlyList<RemoteAudiobook>> GetLibraryAsync(
        CancellationToken cancellationToken = default)
    {
        var provider = await AudibleLibraryCatalogProvider.FromSavedIdentityAsync(
            _snapshotStore, cancellationToken).ConfigureAwait(false);

        if (provider is null)
            throw new InvalidOperationException(
                "No saved Audible identity was found. Sign in to Audible before syncing your library.");

        return await provider.GetLibraryAsync(cancellationToken).ConfigureAwait(false);
    }
}
