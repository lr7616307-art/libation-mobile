namespace Libation.Mobile.Core;

/// <summary>
/// Owns the lifecycle of a complete queue run independently of any UI view.
/// A platform foreground service can hold this controller while the UI observes progress.
/// </summary>
public sealed class DownloadQueueExecutionController
{
    private readonly DownloadQueueWorker _worker;
    private readonly object _sync = new();
    private CancellationTokenSource? _activeRun;

    public DownloadQueueExecutionController(DownloadQueueWorker worker)
        => _worker = worker ?? throw new ArgumentNullException(nameof(worker));

    public bool IsRunning
    {
        get { lock (_sync) return _activeRun is not null; }
    }

    /// <summary>
    /// Starts one sequential queue run. Concurrent starts are rejected rather than
    /// silently creating competing run lifecycles.
    /// </summary>
    public Task<int> RunAsync(
        IProgress<DownloadQueueProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        CancellationTokenSource runCancellation;
        lock (_sync)
        {
            if (_activeRun is not null)
                throw new InvalidOperationException("A download queue run is already active.");

            runCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _activeRun = runCancellation;
        }

        return RunCoreAsync(runCancellation, progress);
    }

    /// <summary>Requests cancellation of the active run, if one exists.</summary>
    public void Cancel()
    {
        CancellationTokenSource? active;
        lock (_sync) active = _activeRun;
        active?.Cancel();
    }

    private async Task<int> RunCoreAsync(
        CancellationTokenSource runCancellation,
        IProgress<DownloadQueueProgress>? progress)
    {
        try
        {
            return await _worker.ProcessAllAsync(progress, runCancellation.Token).ConfigureAwait(false);
        }
        finally
        {
            lock (_sync)
            {
                if (ReferenceEquals(_activeRun, runCancellation))
                    _activeRun = null;
            }
            runCancellation.Dispose();
        }
    }
}
