using System.Text.Json;

namespace Libation.Mobile.Core;

public enum WidevineOfflineDownloadStatus
{
    Downloading,
    Paused,
    Completed,
    Failed
}

/// <summary>
/// Safe, non-secret recovery metadata for one provider-authorized Audible offline
/// download. Never contains manifest URLs, signed URLs, licence bytes, or keys.
/// </summary>
public sealed record WidevineOfflineDownloadState(
    string Asin,
    string Title,
    WidevineOfflineDownloadStatus Status,
    long BytesDownloaded,
    long? ContentLength,
    DateTimeOffset UpdatedAtUtc,
    string? ErrorMessage = null)
{
    public bool CanResume => Status is WidevineOfflineDownloadStatus.Downloading
        or WidevineOfflineDownloadStatus.Paused
        or WidevineOfflineDownloadStatus.Failed;
    public bool ShouldAutoRecover => Status == WidevineOfflineDownloadStatus.Downloading;
    public double? Percentage => ContentLength is > 0
        ? Math.Clamp(BytesDownloaded * 100d / ContentLength.Value, 0d, 100d)
        : null;
}

/// <summary>
/// Atomically persists only the identity and progress needed to recover an
/// Audible Widevine download after process death. Media and licence material
/// remain in their existing app-private Media3/MediaDrm stores.
/// </summary>
public sealed class JsonWidevineOfflineDownloadStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _filePath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonWidevineOfflineDownloadStateStore(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = Path.GetFullPath(filePath);
    }

    public async Task<WidevineOfflineDownloadState?> GetAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await ReadAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task MarkDownloadingAsync(
        string asin,
        string title,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(asin, title);
        return MutateAsync(current =>
        {
            var sameTitle = current is not null &&
                string.Equals(current.Asin, asin, StringComparison.OrdinalIgnoreCase);
            return new WidevineOfflineDownloadState(
                asin.ToUpperInvariant(),
                title.Trim(),
                WidevineOfflineDownloadStatus.Downloading,
                sameTitle ? current!.BytesDownloaded : 0,
                sameTitle ? current!.ContentLength : null,
                DateTimeOffset.UtcNow);
        }, cancellationToken);
    }

    public Task MarkProgressAsync(
        string asin,
        long bytesDownloaded,
        long? contentLength,
        CancellationToken cancellationToken = default)
    {
        ValidateAsin(asin);
        if (bytesDownloaded < 0 || contentLength is < 0)
            throw new ArgumentOutOfRangeException(nameof(bytesDownloaded));

        return MutateAsync(current =>
        {
            if (current is null ||
                !string.Equals(current.Asin, asin, StringComparison.OrdinalIgnoreCase) ||
                current.Status != WidevineOfflineDownloadStatus.Downloading)
                return current;

            var bytes = Math.Max(current.BytesDownloaded, bytesDownloaded);
            var total = contentLength is > 0
                ? current.ContentLength is > 0
                    ? Math.Max(current.ContentLength.Value, contentLength.Value)
                    : contentLength
                : current.ContentLength;
            if (total is > 0)
                bytes = Math.Min(bytes, total.Value);
            return current with
            {
                BytesDownloaded = bytes,
                ContentLength = total,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };
        }, cancellationToken);
    }

    public Task MarkPausedAsync(
        string asin,
        CancellationToken cancellationToken = default)
        => ChangeStatusAsync(asin, WidevineOfflineDownloadStatus.Paused, null, cancellationToken);

    public Task MarkCompletedAsync(
        string asin,
        CancellationToken cancellationToken = default)
        => ChangeStatusAsync(asin, WidevineOfflineDownloadStatus.Completed, null, cancellationToken);

    public Task MarkFailedAsync(
        string asin,
        string safeErrorMessage,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(safeErrorMessage))
            throw new ArgumentException("A safe error message is required.", nameof(safeErrorMessage));
        return ChangeStatusAsync(
            asin,
            WidevineOfflineDownloadStatus.Failed,
            NormalizeError(safeErrorMessage),
            cancellationToken);
    }

    private Task ChangeStatusAsync(
        string asin,
        WidevineOfflineDownloadStatus status,
        string? error,
        CancellationToken cancellationToken)
    {
        ValidateAsin(asin);
        return MutateAsync(current =>
        {
            if (current is null ||
                !string.Equals(current.Asin, asin, StringComparison.OrdinalIgnoreCase) ||
                current.Status != WidevineOfflineDownloadStatus.Downloading)
                return current;
            return current with
            {
                Status = status,
                ErrorMessage = error,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };
        }, cancellationToken);
    }

    private async Task MutateAsync(
        Func<WidevineOfflineDownloadState?, WidevineOfflineDownloadState?> mutation,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var next = mutation(await ReadAsync(cancellationToken));
            if (next is not null)
                await WriteAsync(next, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<WidevineOfflineDownloadState?> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_filePath))
            return null;

        await using var stream = new FileStream(
            _filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
        var state = await JsonSerializer.DeserializeAsync<WidevineOfflineDownloadState>(
            stream, JsonOptions, cancellationToken);
        if (state is null)
            throw new InvalidDataException("The protected download recovery record is empty.");
        ValidateIdentity(state.Asin, state.Title);
        if (!Enum.IsDefined(state.Status) || state.BytesDownloaded < 0 ||
            state.ContentLength is < 0 ||
            (state.ContentLength is > 0 && state.BytesDownloaded > state.ContentLength.Value))
            throw new InvalidDataException("The protected download recovery record is invalid.");
        return state;
    }

    private async Task WriteAsync(
        WidevineOfflineDownloadState state,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_filePath)
            ?? throw new InvalidOperationException("Recovery path has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = _filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(
                temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true))
            {
                await JsonSerializer.SerializeAsync(stream, state, JsonOptions, cancellationToken);
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

    private static void ValidateIdentity(string asin, string title)
    {
        ValidateAsin(asin);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        if (title.Trim().Length > 300)
            throw new ArgumentOutOfRangeException(nameof(title));
    }

    private static void ValidateAsin(string asin)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(asin);
        if (asin.Length != 10 || asin.Any(character => !char.IsAsciiLetterOrDigit(character)))
            throw new ArgumentException("The Audible audiobook identifier is invalid.", nameof(asin));
    }

    private static string NormalizeError(string value)
    {
        var normalized = value.Trim();
        return normalized.Length <= 300 ? normalized : normalized[..300];
    }
}
