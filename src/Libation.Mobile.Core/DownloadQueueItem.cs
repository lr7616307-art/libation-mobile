namespace Libation.Mobile.Core;

public enum DownloadStatus
{
    Queued,
    Downloading,
    Completed,
    Failed
}

public sealed record DownloadQueueItem(
    string AudiobookId,
    DownloadStatus Status,
    DateTimeOffset AddedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    string? ErrorMessage = null);
