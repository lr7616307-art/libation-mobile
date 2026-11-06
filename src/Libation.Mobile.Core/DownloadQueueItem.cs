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
    public long BytesReceived { get; init; }
    public long? TotalBytes { get; init; }

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

    [JsonIgnore]
    public double? ProgressPercentage => TotalBytes is > 0
        ? Math.Clamp(BytesReceived * 100d / TotalBytes.Value, 0d, 100d)
        : null;

    [JsonIgnore]
    public bool HasProgress => BytesReceived > 0 || TotalBytes is > 0;

    [JsonIgnore]
    public bool HasKnownProgress => ProgressPercentage.HasValue;

    [JsonIgnore]
    public string ProgressLabel => TotalBytes is > 0
        ? $"{FormatBytes(BytesReceived)} of {FormatBytes(TotalBytes.Value)} · {ProgressPercentage:0}%"
        : BytesReceived > 0
            ? $"{FormatBytes(BytesReceived)} downloaded · total size unknown"
            : string.Empty;

    private static string FormatBytes(long bytes)
    {
        var value = Math.Max(0, bytes);
        if (value < 1024) return $"{value} B";
        if (value < 1024 * 1024) return $"{value / 1024d:0.0} KB";
        if (value < 1024L * 1024 * 1024) return $"{value / (1024d * 1024):0.0} MB";
        return $"{value / (1024d * 1024 * 1024):0.00} GB";
    }
}
