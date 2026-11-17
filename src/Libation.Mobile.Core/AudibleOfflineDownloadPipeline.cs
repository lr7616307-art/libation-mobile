namespace Libation.Mobile.Core;

/// <summary>
/// Non-secret stage information for the Audible offline-download pipeline.
/// Descriptions are safe for the foreground notification and queue UI.
/// </summary>
public sealed record AudibleOfflineDownloadStage(string Stage, string Message);

/// <summary>
/// Byte progress emitted by the protected-media downloader. The bytes remain
/// provider-encrypted; this progress record never contains a URL or DRM payload.
/// </summary>
public sealed record WidevineOfflineDownloadProgress(
    long BytesDownloaded,
    long? ContentLength,
    double? Percentage);

/// <summary>
/// Platform backend for provider-authorized offline audio. Implementations keep
/// licence messages opaque and delegate protection to the platform DRM system.
/// </summary>
public interface IAudibleOfflineDownloadBackend
{
    Task<bool> HasOfflineLicenseAsync(string asin, CancellationToken cancellationToken = default);

    Task AcquireOfflineLicenseAsync(
        string asin,
        IProgress<AudibleOfflineDownloadStage>? stageProgress,
        CancellationToken cancellationToken = default);

    Task<bool> HasOfflineMediaAsync(string asin, CancellationToken cancellationToken = default);

    Task DownloadOfflineMediaWithProgressAsync(
        string asin,
        IProgress<WidevineOfflineDownloadProgress>? progress,
        IProgress<AudibleOfflineDownloadStage>? stageProgress,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Coordinates the complete offline download lifecycle in explicit stages:
/// reconcile existing state, obtain the provider grant, transfer encrypted media,
/// and verify both the offline grant and all cached media before marking success.
/// The actual licence exchange and media transfer are supplied by a platform backend.
/// </summary>
public sealed class AudibleOfflineDownloadPipeline
{
    public async Task ExecuteAsync(
        string asin,
        IAudibleOfflineDownloadBackend backend,
        IProgress<AudibleOfflineDownloadStage>? stageProgress = null,
        IProgress<WidevineOfflineDownloadProgress>? transferProgress = null,
        CancellationToken cancellationToken = default,
        Func<Exception, bool>? isRetryableTransferFailure = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(asin);
        ArgumentNullException.ThrowIfNull(backend);
        cancellationToken.ThrowIfCancellationRequested();

        Report(stageProgress, "reconciling", "Checking the saved Audible licence and existing audio cache…");

        var hasLicense = await backend.HasOfflineLicenseAsync(asin, cancellationToken)
            .ConfigureAwait(false);
        var hasMedia = await backend.HasOfflineMediaAsync(asin, cancellationToken)
            .ConfigureAwait(false);

        if (!hasLicense)
        {
            Report(stageProgress, "authorizing", "Requesting an Audible-authorized offline licence…");
            await backend.AcquireOfflineLicenseAsync(asin, stageProgress, cancellationToken)
                .ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (!hasMedia)
        {
            Report(stageProgress, "transferring", "Downloading encrypted audiobook audio…");
            var shouldRetry = isRetryableTransferFailure ?? OfflineDownloadRetryPolicy.IsRetryable;
            var retryNumber = 0;
            while (true)
            {
                try
                {
                    await backend.DownloadOfflineMediaWithProgressAsync(
                        asin, transferProgress, stageProgress, cancellationToken).ConfigureAwait(false);
                    break;
                }
                catch (Exception exception) when (
                    !cancellationToken.IsCancellationRequested &&
                    retryNumber < OfflineDownloadRetryPolicy.MaximumAutomaticRetries &&
                    shouldRetry(exception))
                {
                    retryNumber++;
                    var delay = OfflineDownloadRetryPolicy.GetDelay(retryNumber);
                    Report(
                        stageProgress,
                        "retrying",
                        $"Connection interrupted. Preserved encrypted segments will be reused; retry {retryNumber}/{OfflineDownloadRetryPolicy.MaximumAutomaticRetries} in {delay.TotalSeconds:0}s…");
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        Report(stageProgress, "verifying", "Verifying the offline licence and complete audiobook…");

        var verifiedLicense = await backend.HasOfflineLicenseAsync(asin, cancellationToken)
            .ConfigureAwait(false);
        var verifiedMedia = await backend.HasOfflineMediaAsync(asin, cancellationToken)
            .ConfigureAwait(false);

        if (!verifiedLicense || !verifiedMedia)
            throw new InvalidDataException(
                "The offline download did not pass both licence and media verification. The incomplete item remains available to retry.");

        Report(stageProgress, "completed", "Offline audiobook verified and ready to play in VØRATH.");
    }

    private static void Report(
        IProgress<AudibleOfflineDownloadStage>? progress,
        string stage,
        string message)
    {
        try { progress?.Report(new AudibleOfflineDownloadStage(stage, message)); }
        catch (Exception) { /* A notification/UI observer must not fail the download. */ }
    }
}
