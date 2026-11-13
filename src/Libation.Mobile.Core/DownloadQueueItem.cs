using System.Text.Json.Serialization;

namespace Libation.Mobile.Core;

public enum DownloadStatus
{
    Queued,
    Downloading,
    Completed,
    Failed,
    // The provider transfer completed, but the file remains protected and is not playable in VØRATH.
    Protected,
    // An Audible offline transfer explicitly paused by the user.
    Paused
}

public sealed record DownloadQueueItem(
    string AudiobookId,
    DownloadStatus Status,
    DateTimeOffset AddedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    string? ErrorMessage = null,
    string? SourceUrl = null,
    string? DisplayTitle = null,
    long BytesReceived = 0,
    long? TotalBytes = null)
{
    [JsonIgnore]
    public bool CanRetry => Status == DownloadStatus.Failed;

    [JsonIgnore]
    public bool HasErrorMessage => !string.IsNullOrWhiteSpace(ErrorMessage);

    [JsonIgnore]
    public bool CanRemove => Status != DownloadStatus.Downloading;

    [JsonIgnore]
    public bool HasProgress => BytesReceived > 0;

    [JsonIgnore]
    public double ProgressPercentage => TotalBytes is > 0
        ? Math.Clamp(BytesReceived * 100d / TotalBytes.Value, 0d, 100d)
        : 0d;

    [JsonIgnore]
    public string ProgressLabel => TotalBytes is > 0
        ? $"{ProgressPercentage:0}% · {FormatBytes(BytesReceived)} of {FormatBytes(TotalBytes.Value)}"
        : $"{FormatBytes(BytesReceived)} downloaded";

    [JsonIgnore]
    public bool CanToggleAudibleDownload =>
        AudiobookId.StartsWith("provider:", StringComparison.Ordinal) &&
        Status is DownloadStatus.Queued or DownloadStatus.Downloading or DownloadStatus.Paused;

    [JsonIgnore]
    public string DownloadActionLabel =>
        Status == DownloadStatus.Downloading ? "Pause" : "Resume";

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024L * 1024) return $"{bytes / 1024d:0.0} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024d * 1024):0.0} MB";
        return $"{bytes / (1024d * 1024 * 1024):0.00} GB";
    }

    [JsonIgnore]
    public string StatusLabel => Status switch
    {
        DownloadStatus.Queued => "Queued",
        DownloadStatus.Downloading => "Downloading",
        DownloadStatus.Completed => "Completed",
        DownloadStatus.Failed => "Failed",
        DownloadStatus.Protected => "Downloaded (protected)",
        DownloadStatus.Paused => "Paused",
        _ => "Unknown"
    };
}
