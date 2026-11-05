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
    string? ErrorMessage = null)
{
    [JsonIgnore]
    public string DisplayTitle { get; init; } = AudiobookId;

    [JsonIgnore]
    public string DisplayAuthor { get; init; } = string.Empty;

    [JsonIgnore]
    public bool HasDisplayAuthor => !string.IsNullOrWhiteSpace(DisplayAuthor);

    [JsonIgnore]
    public bool CanRetry => Status == DownloadStatus.Failed;

    [JsonIgnore]
    public bool HasErrorMessage => !string.IsNullOrWhiteSpace(ErrorMessage);

    [JsonIgnore]
    public bool CanRemove => Status != DownloadStatus.Downloading;
}
