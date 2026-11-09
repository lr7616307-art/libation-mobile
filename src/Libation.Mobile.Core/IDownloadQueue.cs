namespace Libation.Mobile.Core;

public interface IDownloadQueue
{
    Task<IReadOnlyList<DownloadQueueItem>> GetItemsAsync(CancellationToken cancellationToken = default);
    Task EnqueueAsync(string audiobookId, CancellationToken cancellationToken = default);
    Task UpdateStatusAsync(
        string audiobookId,
        DownloadStatus status,
        string? errorMessage = null,
        CancellationToken cancellationToken = default);
    Task UpdateProgressAsync(
        string audiobookId,
        long bytesReceived,
        long? totalBytes,
        CancellationToken cancellationToken = default);
    Task<bool> RemoveAsync(string audiobookId, CancellationToken cancellationToken = default);
}
