using System.Text.Json.Serialization;

namespace Libation.Mobile.Core;

public enum DownloadStatus
{
    Queued,
    Downloading,
    Completed,
    Failed,
    // The provider transfer completed, but the file remains protected and is not playable in VØRATH.
    Protected
}

public sealed record DownloadQueueItem(
    string AudiobookId,
    DownloadStatus Status,
    DateTimeOffset AddedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    string? ErrorMessage = null,
    string? SourceUrl = null,
    string? DisplayTitle = null)
{
    [JsonIgnore]
    public bool CanRetry => Status == DownloadStatus.Failed;

    [JsonIgnore]
    public bool HasErrorMessage => !string.IsNullOrWhiteSpace(ErrorMessage);

    [JsonIgnore]
    public bool CanRemove => Status != DownloadStatus.Downloading;

    [JsonIgnore]
    public string StatusLabel => Status switch
    {
        DownloadStatus.Queued => "Queued",
        DownloadStatus.Downloading => "Downloading",
        DownloadStatus.Completed => "Completed",
        DownloadStatus.Failed => "Failed",
        DownloadStatus.Protected => "Downloaded (protected)",
        _ => "Unknown"
    };
}
