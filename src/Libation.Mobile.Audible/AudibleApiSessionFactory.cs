using AudibleApi;
using AudibleApi.Authorization;
using Dinah.Core;

namespace Libation.Mobile.Audible;

/// <summary>
/// Restores the app's saved Audible identity and creates an authenticated API client.
/// Callers receive an API client backed by the app's token maintainer; credentials and
/// license material remain inside the Audible adapter and encrypted identity store.
/// </summary>
public sealed class AudibleApiSessionFactory
{
    private readonly AudibleIdentitySnapshotStore snapshotStore;
    private readonly ISystemDateTime systemDateTime;
    private readonly IAudibleTokenRefresher tokenRefresher;

    public AudibleApiSessionFactory(
        AudibleIdentitySnapshotStore snapshotStore,
        ISystemDateTime? systemDateTime = null,
        IAudibleTokenRefresher? tokenRefresher = null)
    {
        this.snapshotStore = snapshotStore ?? throw new ArgumentNullException(nameof(snapshotStore));
        this.systemDateTime = systemDateTime ?? new SystemDateTime();
        this.tokenRefresher = tokenRefresher
            ?? new AudibleTokenRefreshClient(new HttpClient(), this.systemDateTime);
    }

    /// <summary>
    /// Creates a client from the identity already saved on this installation.
    /// Returns null when the user has not signed in. This method never launches login
    /// or registers a new device.
    /// </summary>
    public async Task<Api?> CreateFromSavedIdentityAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Capture before reading the snapshot. A login or disconnect racing with
        // restore makes this API session stale instead of binding it to a new account.
        var identityGeneration = snapshotStore.LifecycleCoordinator.CurrentGeneration;
        var snapshot = await snapshotStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (snapshot is null)
            return null;

        cancellationToken.ThrowIfCancellationRequested();

        var identity = AudibleIdentitySnapshotMapper.Restore(snapshot);
        var maintainer = new AudibleIdentityMaintainer(
            identity,
            tokenRefresher,
            systemDateTime,
            snapshotStore,
            identityGeneration: identityGeneration);

        return new Api(maintainer);
    }
}
