namespace Libation.Mobile.Core;

/// <summary>
/// Resolves a provider-issued, authorized HTTPS media URL and destination for a purchased title.
/// Implementations must not bypass access controls or decrypt protected media.
/// </summary>
public interface IAuthorizedAudioDownloadSource
{
    Task<AuthorizedAudioDownload> ResolveAsync(
        string audiobookId, CancellationToken cancellationToken = default);
}

public sealed record AuthorizedAudioDownload(Uri DownloadUri, string DestinationPath);

public sealed record DownloadQueueProgress(
    string AudiobookId,
    long BytesReceived,
    long? TotalBytes,
    double? Percentage);

/// <summary>
/// Executes one queued download using a provider-supplied authorized URL.
/// </summary>
public sealed class DownloadQueueWorker
{
    private readonly IDownloadQueue _queue;
    private readonly AudioFileDownloader _downloader;
    private readonly IAuthorizedAudioDownloadSource _source;
    private readonly SemaphoreSlim _runGate = new(1, 1);

    public DownloadQueueWorker(
        IDownloadQueue queue,
        AudioFileDownloader downloader,
        IAuthorizedAudioDownloadSource source)
    {
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _downloader = downloader ?? throw new ArgumentNullException(nameof(downloader));
        _source = source ?? throw new ArgumentNullException(nameof(source));
    }

    /// <summary>
    /// Processes the oldest queued item. Returns false when no work is queued.
    /// Cancellation returns the item to Queued so it can safely be resumed later.
    /// </summary>
    public async Task<int> RecoverInterruptedDownloadsAsync(CancellationToken cancellationToken = default)
    {
        var items = await _queue.GetItemsAsync(cancellationToken);
        var recovered = 0;
        foreach (var item in items.Where(x => x.Status == DownloadStatus.Downloading))
        {
            await _queue.UpdateStatusAsync(item.AudiobookId, DownloadStatus.Queued, cancellationToken: cancellationToken);
            recovered++;
        }
        return recovered;
    }

    public async Task<bool> ProcessNextAsync(
        IProgress<DownloadQueueProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await _runGate.WaitAsync(cancellationToken);
        try
        {
        var item = (await _queue.GetItemsAsync(cancellationToken))
            .Where(x => x.Status == DownloadStatus.Queued)
            .OrderBy(x => x.AddedAtUtc)
            .FirstOrDefault();

        if (item is null)
            return false;

        await _queue.UpdateStatusAsync(
            item.AudiobookId, DownloadStatus.Downloading,
            cancellationToken: cancellationToken);

        try
        {
            var target = await _source.ResolveAsync(item.AudiobookId, cancellationToken);
            if (target is null)
                throw new InvalidOperationException("The audio provider returned no download target.");

            var result = await _downloader.DownloadAsync(
                target.DownloadUri,
                target.DestinationPath,
                new Progress<AudioDownloadProgress>(value =>
                    progress?.Report(new DownloadQueueProgress(
                        item.AudiobookId, value.BytesReceived, value.TotalBytes, value.Percentage))),
                cancellationToken);

            await _queue.UpdateStatusAsync(
                item.AudiobookId, DownloadStatus.Completed,
                cancellationToken: cancellationToken);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await _queue.UpdateStatusAsync(
                item.AudiobookId, DownloadStatus.Queued,
                cancellationToken: CancellationToken.None);
            throw;
        }
        catch (Exception exception)
        {
            await _queue.UpdateStatusAsync(
                item.AudiobookId, DownloadStatus.Failed,
                exception.Message,
                cancellationToken: CancellationToken.None);
            return true;
        }
        }
        finally
        {
            _runGate.Release();
        }
    }
}
