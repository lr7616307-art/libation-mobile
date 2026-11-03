namespace Libation.Mobile.Audible;

/// <summary>
/// Serializes asynchronous identity creation, persistence, and clearing.
/// A newer operation invalidates older work even while that work is awaiting
/// a remote response, so stale results are never persisted.
/// </summary>
public sealed class AudibleIdentityLifecycleCoordinator
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private long generation;

    /// <summary>
    /// Creates and persists an identity only if no newer identity operation or
    /// clear request has superseded this operation.
    /// </summary>
    public async Task<T> CreateAndSaveAsync<T>(
        Func<CancellationToken, Task<T>> createIdentityAsync,
        Func<T, CancellationToken, Task> saveIdentityAsync,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(createIdentityAsync);
        ArgumentNullException.ThrowIfNull(saveIdentityAsync);

        var operationGeneration = Interlocked.Increment(ref generation);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var identity = await createIdentityAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            if (operationGeneration != Volatile.Read(ref generation))
                throw new OperationCanceledException("A newer Audible identity operation superseded this result.");

            await saveIdentityAsync(identity, cancellationToken).ConfigureAwait(false);

            if (operationGeneration != Volatile.Read(ref generation))
                throw new OperationCanceledException("A newer Audible identity operation superseded this result.");

            return identity;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Invalidates pending identity creation immediately, then clears persisted
    /// identity after any currently-held lifecycle operation leaves the gate.
    /// </summary>
    public async Task ClearAsync(
        Func<CancellationToken, Task> clearIdentityAsync,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(clearIdentityAsync);
        Interlocked.Increment(ref generation);

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await clearIdentityAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }
}
