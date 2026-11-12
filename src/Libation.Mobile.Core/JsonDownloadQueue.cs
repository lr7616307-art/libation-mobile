using System.Text.Json;

namespace Libation.Mobile.Core;

/// <summary>
/// Persists queue metadata locally. This class schedules work only; it does not fetch or decrypt audio.
/// </summary>
public sealed class JsonDownloadQueue : IDownloadQueue
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _filePath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonDownloadQueue(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = Path.GetFullPath(filePath);
    }

    public async Task<IReadOnlyList<DownloadQueueItem>> GetItemsAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await ReadItemsAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task EnqueueAsync(string audiobookId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(audiobookId);
        await MutateAsync(items =>
        {
            var existingIndex = items.FindIndex(item =>
                string.Equals(item.AudiobookId, audiobookId, StringComparison.Ordinal));
            var now = DateTimeOffset.UtcNow;

            if (existingIndex < 0)
            {
                items.Add(new DownloadQueueItem(audiobookId, DownloadStatus.Queued, now, now));
                return;
            }

            var existing = items[existingIndex];
            if (existing.Status == DownloadStatus.Failed)
                items[existingIndex] = existing with
                {
                    Status = DownloadStatus.Queued,
                    UpdatedAtUtc = now,
                    ErrorMessage = null
                };
        }, cancellationToken);
    }

    public async Task EnqueueAuthorizedAsync(
        string audiobookId,
        string sourceUrl,
        string displayTitle,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(audiobookId);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayTitle);
        var uri = AuthorizedAudioDownloadUrl.Parse(sourceUrl);

        await MutateAsync(items =>
        {
            var existingIndex = items.FindIndex(item =>
                string.Equals(item.AudiobookId, audiobookId, StringComparison.Ordinal));
            var now = DateTimeOffset.UtcNow;

            if (existingIndex < 0)
            {
                items.Add(new DownloadQueueItem(
                    audiobookId, DownloadStatus.Queued, now, now,
                    SourceUrl: uri.AbsoluteUri, DisplayTitle: displayTitle.Trim()));
                return;
            }

            var existing = items[existingIndex];
            if (existing.Status == DownloadStatus.Downloading ||
                existing.Status == DownloadStatus.Completed)
                return;

            items[existingIndex] = existing with
            {
                Status = DownloadStatus.Queued,
                UpdatedAtUtc = now,
                ErrorMessage = null,
                SourceUrl = uri.AbsoluteUri,
                DisplayTitle = displayTitle.Trim()
            };
        }, cancellationToken);
    }

    public async Task UpdateStatusAsync(
        string audiobookId,
        DownloadStatus status,
        string? errorMessage = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(audiobookId);
        if (!Enum.IsDefined(status))
            throw new ArgumentOutOfRangeException(nameof(status));

        await MutateAsync(items =>
        {
            var index = items.FindIndex(item =>
                string.Equals(item.AudiobookId, audiobookId, StringComparison.Ordinal));
            if (index < 0)
                throw new KeyNotFoundException($"No download queue item exists for '{audiobookId}'.");

            var current = items[index];
            if (!IsAllowedTransition(current.Status, status))
                throw new InvalidOperationException(
                    $"Cannot change download status from {current.Status} to {status}.");

            var normalizedError = status == DownloadStatus.Failed
                ? NormalizeError(errorMessage)
                : null;
            if (status == DownloadStatus.Failed && normalizedError is null)
                throw new ArgumentException("A failed item must include a short error message.", nameof(errorMessage));

            items[index] = current with
            {
                Status = status,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                ErrorMessage = normalizedError
            };
        }, cancellationToken);
    }

    public async Task<bool> RemoveAsync(string audiobookId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(audiobookId);
        var removed = false;
        await MutateAsync(items =>
        {
            var index = items.FindIndex(item =>
                string.Equals(item.AudiobookId, audiobookId, StringComparison.Ordinal));
            if (index < 0)
                return;
            if (items[index].Status == DownloadStatus.Downloading)
                return;
            items.RemoveAt(index);
            removed = true;
        }, cancellationToken);
        return removed;
    }

    private async Task MutateAsync(Action<List<DownloadQueueItem>> mutation, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var items = (await ReadItemsAsync(cancellationToken)).ToList();
            mutation(items);
            await WriteItemsAsync(items, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IReadOnlyList<DownloadQueueItem>> ReadItemsAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_filePath))
            return [];

        await using var stream = new FileStream(
            _filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
        var items = await JsonSerializer.DeserializeAsync<List<DownloadQueueItem>>(
            stream, JsonOptions, cancellationToken);
        return items ?? [];
    }

    private async Task WriteItemsAsync(
        IReadOnlyList<DownloadQueueItem> items,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_filePath)
            ?? throw new InvalidOperationException("Queue file path has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = _filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";

        try
        {
            await using (var stream = new FileStream(
                temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true))
            {
                await JsonSerializer.SerializeAsync(stream, items, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, _filePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static bool IsAllowedTransition(DownloadStatus current, DownloadStatus next)
        => current switch
        {
            DownloadStatus.Queued => next is DownloadStatus.Downloading or DownloadStatus.Failed,
            DownloadStatus.Downloading => next is DownloadStatus.Completed or DownloadStatus.Failed or DownloadStatus.Queued,
            DownloadStatus.Failed => next == DownloadStatus.Queued,
            DownloadStatus.Completed => false,
            _ => false
        };

    private static string? NormalizeError(string? errorMessage)
    {
        if (string.IsNullOrWhiteSpace(errorMessage))
            return null;
        var value = errorMessage.Trim();
        return value.Length <= 500 ? value : value[..500];
    }
}
