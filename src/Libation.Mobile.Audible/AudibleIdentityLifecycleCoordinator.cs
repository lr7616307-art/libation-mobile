namespace Libation.Mobile.Audible;

/// <summary>
/// Serializes asynchronous identity creation, persistence, and clearing.
/// A newer operation invalidates older work even while that work is awaiting
/// a remote response or committing its result.
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
            EnsureCurrent(operationGeneration);

            await saveIdentityAsync(identity, cancellationToken).ConfigureAwait(false);
            EnsureCurrent(operationGeneration);

            return identity;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Runs a refresh or other update against the current lifecycle generation.
    /// A newer login/replacement or clear invalidates the operation before it
    /// can commit, or causes it to report cancellation if superseded during commit.
    /// </summary>
    /// <summary>
    /// Current identity generation. A maintainer captures this when its identity is
    /// restored; later login or clear operations permanently invalidate that maintainer.
    /// </summary>
    public long CurrentGeneration => Volatile.Read(ref generation);

    public Task<T> ExecuteCurrentAsync<T>(
        Func<CancellationToken, Task<T>> operationAsync,
        Func<T, CancellationToken, Task> commitAsync,
        CancellationToken cancellationToken = default)
        => ExecuteCurrentAsync(CurrentGeneration, operationAsync, commitAsync, cancellationToken);

    /// <summary>
    /// Executes work only for the identity generation supplied by the caller.
    /// This prevents an old API session from becoming valid again merely because
    /// it starts another operation after a newer login or disconnect has completed.
    /// </summary>
    public async Task<T> ExecuteCurrentAsync<T>(
        long expectedGeneration,
        Func<CancellationToken, Task<T>> operationAsync,
        Func<T, CancellationToken, Task> commitAsync,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operationAsync);
        ArgumentNullException.ThrowIfNull(commitAsync);

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureCurrent(expectedGeneration);

            var result = await operationAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            EnsureCurrent(expectedGeneration);

            await commitAsync(result, cancellationToken).ConfigureAwait(false);
            EnsureCurrent(expectedGeneration);

            return result;
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

    private void EnsureCurrent(long operationGeneration)
    {
        if (operationGeneration != Volatile.Read(ref generation))
            throw new OperationCanceledException("A newer Audible identity operation superseded this result.");
    }
}
