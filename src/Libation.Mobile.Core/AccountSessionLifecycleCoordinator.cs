namespace Libation.Mobile.Core;

/// <summary>
/// Serializes account sign-in completion, refresh, and disconnect. New sign-in
/// and disconnect requests invalidate older asynchronous work before it commits.
/// </summary>
public sealed class AccountSessionLifecycleCoordinator
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private long generation;

    public async Task<T> CreateAndSaveAsync<T>(
        Func<CancellationToken, Task<T>> createAsync,
        Func<T, CancellationToken, Task> saveAsync,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(createAsync);
        ArgumentNullException.ThrowIfNull(saveAsync);
        var operationGeneration = Interlocked.Increment(ref generation);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await createAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            EnsureCurrent(operationGeneration);
            await saveAsync(result, cancellationToken).ConfigureAwait(false);
            EnsureCurrent(operationGeneration);
            return result;
        }
        finally { gate.Release(); }
    }

    public async Task<T> ExecuteCurrentAsync<T>(
        Func<CancellationToken, Task<T>> operationAsync,
        Func<T, CancellationToken, Task> commitAsync,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operationAsync);
        ArgumentNullException.ThrowIfNull(commitAsync);
        var operationGeneration = Volatile.Read(ref generation);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureCurrent(operationGeneration);
            var result = await operationAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            EnsureCurrent(operationGeneration);
            await commitAsync(result, cancellationToken).ConfigureAwait(false);
            return result;
        }
        finally { gate.Release(); }
    }

    public async Task ClearAsync(
        Func<CancellationToken, Task> clearAsync,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(clearAsync);
        Interlocked.Increment(ref generation);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await clearAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    private void EnsureCurrent(long operationGeneration)
    {
        if (operationGeneration != Volatile.Read(ref generation))
            throw new OperationCanceledException("A newer account-session operation superseded this result.");
    }
}
