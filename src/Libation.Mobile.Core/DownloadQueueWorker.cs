using System.Threading.Channels;

namespace Libation.Mobile.Core;

/// <summary>
/// Resolves a provider-issued, authorized HTTPS media URL and destination for a purchased title.
/// Implementations must not bypass access controls or decrypt protected media.
/// </summary>
public interface IAuthorizedAudioDownloadSource
{
    Task<AuthorizedAudioDownload> ResolveAsync(
        string audiobookId, CancellationToken cancellationToken = default);
}

public sealed record AuthorizedAudioDownload(Uri DownloadUri, string DestinationPath)
{
    /// <summary>
    /// Protection metadata returned with the provider-issued licence. Defaults to
    /// unprotected for existing non-DRM providers and test sources.
    /// </summary>
    public AudioProtectionDescriptor Protection { get; init; } = AudioProtectionDescriptor.Unprotected;
}

public sealed record DownloadQueueProgress(
    string AudiobookId,
    long BytesReceived,
    long? TotalBytes,
    double? Percentage);

/// <summary>
/// Optional, on-demand stage for audio transformations that are independently
/// authorized and supported by the application. The main downloader remains
/// independent of any particular processing implementation.
/// </summary>
public interface IAudioPostDownloadProcessor
{
    /// <summary>
    /// Returns true only when this implementation has an authorized processing
    /// path for the supplied protection mode. Protected downloads are rejected
    /// before transfer unless this returns true.
    /// </summary>
    bool CanProcess(AudioProtectionDescriptor protection);

    /// <summary>
    /// Processes a completed download and returns the path of the resulting audio file.
    /// Implementations must leave the input file intact if processing fails.
    /// </summary>
    Task<string> ProcessAsync(
        string downloadedFilePath,
        AudioProtectionDescriptor protection,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Executes one queued download using a provider-supplied authorized URL.
/// </summary>
public sealed class DownloadQueueWorker
{
    private readonly IDownloadQueue _queue;
    private readonly AudioFileDownloader _downloader;
    private readonly IAuthorizedAudioDownloadSource _source;
    private readonly IAudioPostDownloadProcessor? _postDownloadProcessor;
    private readonly ILibraryRepository? _libraryRepository;
    private readonly SemaphoreSlim _runGate = new(1, 1);

    public DownloadQueueWorker(
        IDownloadQueue queue,
        AudioFileDownloader downloader,
        IAuthorizedAudioDownloadSource source,
        IAudioPostDownloadProcessor? postDownloadProcessor = null,
        ILibraryRepository? libraryRepository = null)
    {
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _downloader = downloader ?? throw new ArgumentNullException(nameof(downloader));
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _postDownloadProcessor = postDownloadProcessor;
        _libraryRepository = libraryRepository;
    }

    /// <summary>
    /// Processes queued items one at a time until the queue is empty.
    /// Failed items are counted as attempted and do not prevent later items from running.
    /// Cancellation stops the batch and leaves unstarted items queued.
    /// </summary>
    public async Task<int> ProcessAllAsync(
        IProgress<DownloadQueueProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        // A process restart can leave an item marked Downloading even though
        // no worker is alive. Requeue those items before selecting new work;
        // the downloader will validate and resume any retained partial file.
        await RecoverInterruptedDownloadsAsync(cancellationToken).ConfigureAwait(false);

        var processed = 0;
        while (await ProcessNextAsync(progress, cancellationToken))
            processed++;
        return processed;
    }

    public async Task<int> RecoverInterruptedDownloadsAsync(CancellationToken cancellationToken = default)
    {
        // Use the queue's single atomic recovery operation so multiple queue
        // objects cannot independently claim the same interrupted item.
        return await _queue.RecoverInterruptedDownloadsAsync(cancellationToken).ConfigureAwait(false);
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private async Task PersistProgressAsync(
        string audiobookId,
        ChannelReader<(long BytesReceived, long? TotalBytes)> reader)
    {
        await foreach (var value in reader.ReadAllAsync().ConfigureAwait(false))
        {
            await _queue.UpdateProgressAsync(
                audiobookId, value.BytesReceived, value.TotalBytes,
                CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Processes the oldest queued item. Returns false when no work is queued.
    /// Cancellation returns the item to Queued so it can safely be resumed later.
    /// </summary>
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

            try
            {
                var target = await _source.ResolveAsync(item.AudiobookId, cancellationToken);
                if (target is null)
                    throw new InvalidOperationException("The audio provider returned no download target.");

                // Do not spend bandwidth or storage on protected bytes that this
                // app cannot process. A compatible, authorized processor must be
                // present before the first network request is made.
                if (target.Protection.RequiresProtectedMediaPipeline &&
                    (_postDownloadProcessor is null || !_postDownloadProcessor.CanProcess(target.Protection)))
                    throw new NotSupportedException(
                        $"The audiobook uses {target.Protection.Kind} protection, but no compatible authorized media pipeline is configured.");

                // Persist progress asynchronously. Blocking on UpdateProgressAsync from
                // the downloader's synchronous IProgress callback can deadlock when a
                // platform synchronization context is captured by the queue store.
                var progressChannel = Channel.CreateUnbounded<(long BytesReceived, long? TotalBytes)>(
                    new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
                var progressPersistence = PersistProgressAsync(item.AudiobookId, progressChannel.Reader);
                long lastPersistedBytes = 0;
                AudioDownloadProgress? latestProgress = null;
                var downloadProgress = new InlineProgress<AudioDownloadProgress>(value =>
                {
                    latestProgress = value;
                    // Queue writes are coalesced to roughly one per MiB, not one per
                    // network chunk. The consumer performs the writes without blocking
                    // the download stream's progress callback.
                    if (value.BytesReceived - lastPersistedBytes >= 1024 * 1024 ||
                        (value.TotalBytes is long total && value.BytesReceived == total))
                    {
                        progressChannel.Writer.TryWrite((value.BytesReceived, value.TotalBytes));
                        lastPersistedBytes = value.BytesReceived;
                    }

                    progress?.Report(new DownloadQueueProgress(
                        item.AudiobookId, value.BytesReceived, value.TotalBytes, value.Percentage));
                });

                AudioDownloadResult result;
                try
                {
                    try
                    {
                        result = await _downloader.DownloadAsync(
                            target.DownloadUri,
                            target.DestinationPath,
                            downloadProgress,
                            cancellationToken);
                    }
                    catch (HttpRequestException exception) when (
                        exception.StatusCode is System.Net.HttpStatusCode.Unauthorized or
                            System.Net.HttpStatusCode.Forbidden)
                    {
                        // Provider download URLs can expire independently of the
                        // purchased title. Request one fresh authorized licence and
                        // retry once; never loop indefinitely on rejected credentials.
                        cancellationToken.ThrowIfCancellationRequested();
                        var refreshedTarget = await _source.ResolveAsync(
                            item.AudiobookId, cancellationToken).ConfigureAwait(false);
                        if (refreshedTarget is null)
                            throw new InvalidOperationException(
                                "The audio provider returned no refreshed download target.");

                        if (!string.Equals(
                            Path.GetFullPath(refreshedTarget.DestinationPath),
                            Path.GetFullPath(target.DestinationPath),
                            StringComparison.Ordinal))
                            throw new InvalidDataException(
                                "The refreshed download target changed the audiobook destination.");

                        // A refreshed licence is authoritative and may carry
                        // different protection metadata from the expired URL's licence.
                        // Revalidate before retrying so protected bytes are never fetched
                        // without a compatible authorized processor.
                        if (refreshedTarget.Protection.RequiresProtectedMediaPipeline &&
                            (_postDownloadProcessor is null || !_postDownloadProcessor.CanProcess(refreshedTarget.Protection)))
                            throw new NotSupportedException(
                                $"The refreshed audiobook licence uses {refreshedTarget.Protection.Kind} protection, but no compatible authorized media pipeline is configured.");

                        target = refreshedTarget;
                        result = await _downloader.DownloadAsync(
                            target.DownloadUri,
                            target.DestinationPath,
                            downloadProgress,
                            cancellationToken);
                    }
                    latestProgress = new AudioDownloadProgress(result.BytesReceived, result.BytesReceived);
                }
                finally
                {
                    // Flush the last observed byte count on success or cancellation so
                    // the persisted checkpoint stays close to the retained .part file.
                    if (latestProgress is not null)
                        progressChannel.Writer.TryWrite((latestProgress.BytesReceived, latestProgress.TotalBytes));
                    progressChannel.Writer.TryComplete();
                    await progressPersistence.ConfigureAwait(false);
                }

                // This hook is deliberately optional for unprotected media and runs
                // only after a complete download. Its output becomes the playable file.
                var playablePath = result.FilePath;
                if (_postDownloadProcessor is not null &&
                    _postDownloadProcessor.CanProcess(target.Protection))
                {
                    playablePath = await _postDownloadProcessor.ProcessAsync(
                        result.FilePath, target.Protection, cancellationToken);
                    if (string.IsNullOrWhiteSpace(playablePath) ||
                        !File.Exists(playablePath) ||
                        new FileInfo(playablePath).Length == 0)
                        throw new InvalidDataException(
                            "The audio processing stage did not produce a valid output file.");
                }

                if (_libraryRepository is not null)
                {
                    var books = await _libraryRepository.GetBooksAsync(cancellationToken);
                    var book = books.FirstOrDefault(x =>
                        string.Equals(x.Id, item.AudiobookId, StringComparison.Ordinal));
                    if (book is not null)
                        await _libraryRepository.UpsertAsync(
                            book with { AudioFilePath = playablePath }, cancellationToken);
                }

                await _queue.UpdateStatusAsync(
                    item.AudiobookId, DownloadStatus.Completed,
                    cancellationToken: cancellationToken);
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
                await _queue.UpdateStatusAsync(
                    item.AudiobookId, DownloadStatus.Failed,
                    exception.Message,
                    cancellationToken: CancellationToken.None);
                return true;
            }
        }
        finally
        {
            _runGate.Release();
        }
    }
}
