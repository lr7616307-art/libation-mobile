using Libation.Mobile.Core;

namespace Libation.Mobile.UI.Services;

/// <summary>
/// Sends Audible catalogue IDs to the authenticated Audible licence source and
/// keeps ordinary direct-URL downloads on their existing provider-neutral path.
/// </summary>
public sealed class RoutedAudioDownloadSource : IAuthorizedAudioDownloadSource
{
    private readonly IAuthorizedAudioDownloadSource _ordinarySource;
    private readonly Func<CancellationToken, Task<IAuthorizedAudioDownloadSource?>> _audibleSourceFactory;

    public RoutedAudioDownloadSource(
        IAuthorizedAudioDownloadSource ordinarySource,
        Func<CancellationToken, Task<IAuthorizedAudioDownloadSource?>> audibleSourceFactory)
    {
        _ordinarySource = ordinarySource ?? throw new ArgumentNullException(nameof(ordinarySource));
        _audibleSourceFactory = audibleSourceFactory ?? throw new ArgumentNullException(nameof(audibleSourceFactory));
    }

    public Task<AuthorizedAudioDownload> ResolveAsync(
        string audiobookId,
        CancellationToken cancellationToken = default)
    {
        if (!audiobookId.StartsWith("provider:", StringComparison.Ordinal))
            return _ordinarySource.ResolveAsync(audiobookId, cancellationToken);

        return ResolveAudibleAsync(audiobookId["provider:".Length..], cancellationToken);
    }

    public Task<AuthorizedAudioDownload> ResolveAsync(
        DownloadQueueItem item,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!item.AudiobookId.StartsWith("provider:", StringComparison.Ordinal))
            return _ordinarySource.ResolveAsync(item, cancellationToken);

        return ResolveAudibleAsync(item.AudiobookId["provider:".Length..], cancellationToken);
    }

    private async Task<AuthorizedAudioDownload> ResolveAudibleAsync(
        string asin,
        CancellationToken cancellationToken)
    {
        var source = await _audibleSourceFactory(cancellationToken).ConfigureAwait(false);
        if (source is null)
            throw new InvalidOperationException(
                "No saved Audible account is available. Sign in to Audible, sync your library, and retry.");
        return await source.ResolveAsync(asin, cancellationToken).ConfigureAwait(false);
    }
}
