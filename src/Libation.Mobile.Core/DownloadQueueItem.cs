using System.Text.Json.Serialization;

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
        _ => "Unknown"
    };
}
