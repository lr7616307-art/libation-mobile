namespace Libation.Mobile.Core;

/// <summary>
/// Deterministically selects the next Audible provider job for the single
/// Android foreground Widevine service. Local HTTP downloads stay on their
/// separate worker and failed/paused/completed items are never auto-started.
/// </summary>
public static class AudibleProviderQueueScheduler
{
    public static DownloadQueueItem? SelectNext(
        IReadOnlyCollection<DownloadQueueItem> items,
        string? excludedAudiobookId = null)
    {
        ArgumentNullException.ThrowIfNull(items);

        return items
            .Where(item =>
                item.Status == DownloadStatus.Queued &&
                item.AudiobookId.StartsWith("provider:", StringComparison.Ordinal) &&
                !string.Equals(item.AudiobookId, excludedAudiobookId, StringComparison.Ordinal))
            .OrderBy(item => item.AddedAtUtc)
            .ThenBy(item => item.AudiobookId, StringComparer.Ordinal)
            .FirstOrDefault();
    }
}
