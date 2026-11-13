namespace Libation.Mobile.Core;

/// <summary>
/// Resolves a provider-issued, authorized HTTPS media URL and a staging destination.
/// Implementations must not bypass access controls or decrypt protected media.
/// </summary>
public interface IAuthorizedAudioDownloadSource
{
    Task<AuthorizedAudioDownload> ResolveAsync(
        string audiobookId, CancellationToken cancellationToken = default);

    Task<AuthorizedAudioDownload> ResolveAsync(
        DownloadQueueItem item, CancellationToken cancellationToken = default)
        => ResolveAsync(item.AudiobookId, cancellationToken);
}

public interface IAudioDownloadCompletionHandler
{
    /// <summary>
    /// Publishes a fully downloaded, unprotected audio file to the user's selected
    /// destination and attaches a playable local copy to the VØRATH catalogue.
    /// </summary>
    Task PublishAsync(
        DownloadQueueItem item,
        string stagingFilePath,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Publishes provider-protected media without decrypting it or attaching it to the
/// local player. Implementations must retain the provider's protection.
/// </summary>
public interface IProtectedAudioDownloadCompletionHandler
{
    Task PublishProtectedAsync(
        DownloadQueueItem item,
        string stagingFilePath,
        AudioProtectionDescriptor protection,
        CancellationToken cancellationToken = default);
}

public sealed record AuthorizedAudioDownload(Uri DownloadUri, string DestinationPath)
{
    public AudioProtectionDescriptor Protection { get; init; } = AudioProtectionDescriptor.Unprotected;
}

public sealed record DownloadQueueProgress(
    string AudiobookId,
    long BytesReceived,
    long? TotalBytes,
    double? Percentage);

/// <summary>
/// Executes one queued download using a provider-supplied authorized URL.
/// Only one item is processed at a time to limit bandwidth and memory use.
/// </summary>
public sealed class DownloadQueueWorker
{
    private readonly IDownloadQueue _queue;
    private readonly AudioFileDownloader _downloader;
    private readonly IAuthorizedAudioDownloadSource _source;
    private readonly IAudioDownloadCompletionHandler? _completionHandler;
    private readonly SemaphoreSlim _runGate = new(1, 1);

    public DownloadQueueWorker(
        IDownloadQueue queue,
        AudioFileDownloader downloader,
        IAuthorizedAudioDownloadSource source,
        IAudioDownloadCompletionHandler? completionHandler = null)
    {
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _downloader = downloader ?? throw new ArgumentNullException(nameof(downloader));
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _completionHandler = completionHandler;
    }

    /// <summary>
    /// Requeues items left in Downloading state after process death. The app does not
    /// automatically restart them on mobile data; the user starts the pending queue.
    /// </summary>
    public async Task<int> RecoverInterruptedDownloadsAsync(CancellationToken cancellationToken = default)
    {
        var items = await _queue.GetItemsAsync(cancellationToken);
        var recovered = 0;
        foreach (var item in items.Where(x =>
            x.Status == DownloadStatus.Downloading &&
            !x.AudiobookId.StartsWith("provider:", StringComparison.Ordinal)))
        {
            // Audible Widevine downloads belong to Android's foreground service.
            // Requeueing them here would send them through the ordinary HTTP worker
            // and could discard the provider-specific recovery path.
            await _queue.UpdateStatusAsync(
                item.AudiobookId, DownloadStatus.Queued, cancellationToken: cancellationToken);
            recovered++;
        }
        return recovered;
    }

    public async Task<bool> ProcessNextAsync(
        IProgress<DownloadQueueProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await _runGate.WaitAsync(cancellationToken);
        try
        {
            var item = (await _queue.GetItemsAsync(cancellationToken))
                .Where(x => x.Status == DownloadStatus.Queued)
                .OrderBy(x => x.AddedAtUtc)
                .FirstOrDefault();

            if (item is null)
                return false;

            await _queue.UpdateStatusAsync(
                item.AudiobookId, DownloadStatus.Downloading,
                cancellationToken: cancellationToken);

            AuthorizedAudioDownload? target = null;
            try
            {
                target = await _source.ResolveAsync(item, cancellationToken);
                if (target is null)
                    throw new InvalidOperationException("The audio provider returned no download target.");

                var result = await _downloader.DownloadAsync(
                    target.DownloadUri,
                    target.DestinationPath,
                    new Progress<AudioDownloadProgress>(value =>
                        progress?.Report(new DownloadQueueProgress(
                            item.AudiobookId, value.BytesReceived, value.TotalBytes, value.Percentage))),
                    cancellationToken);

                if (target.Protection.RequiresProtectedMediaPipeline)
                {
                    if (_completionHandler is not IProtectedAudioDownloadCompletionHandler protectedPublisher)
                        throw new NotSupportedException(
                            "Audible granted protected media, but this build has no protected-file publisher.");

                    await protectedPublisher.PublishProtectedAsync(
                        item, result.FilePath, target.Protection, cancellationToken);
                    await _queue.UpdateStatusAsync(
                        item.AudiobookId, DownloadStatus.Protected,
                        cancellationToken: cancellationToken);
                }
                else
                {
                    if (_completionHandler is not null)
                        await _completionHandler.PublishAsync(item, result.FilePath, cancellationToken);

                    await _queue.UpdateStatusAsync(
                        item.AudiobookId, DownloadStatus.Completed,
                        cancellationToken: cancellationToken);
                }
                return true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await _queue.UpdateStatusAsync(
                    item.AudiobookId, DownloadStatus.Queued,
                    cancellationToken: CancellationToken.None);
                throw;
            }
            catch (Exception exception)
            {
                // Provider/network exceptions can include signed media URLs or query tokens.
                // Persist only a stable, user-safe category; never store raw exception text.
                await _queue.UpdateStatusAsync(
                    item.AudiobookId, DownloadStatus.Failed,
                    GetSafeFailureMessage(exception),
                    cancellationToken: CancellationToken.None);
                return true;
            }
            finally
            {
                // The app's completion handler copies into SAF and app-private storage.
                // Do not retain staging copies after publication or a failed attempt.
                if (_completionHandler is not null && target is not null)
                    TryDeleteStagingFile(target.DestinationPath);
            }
        }
        finally
        {
            _runGate.Release();
        }
    }

    private static void TryDeleteStagingFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string GetSafeFailureMessage(Exception exception) => exception switch
    {
        HttpRequestException => "The download request failed. Check your connection and retry.",
        InvalidDataException => "The downloaded audio failed validation.",
        UnauthorizedAccessException => "The app cannot write to the selected download location.",
        InvalidOperationException => "The provider could not authorize this download.",
        ArgumentException => exception.Message,
        _ => "The download could not be completed. Please retry."
    };
}
