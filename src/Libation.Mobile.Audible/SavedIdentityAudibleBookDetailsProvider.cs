using AudibleApi;
using AudibleApi.Common;
using Libation.Mobile.Core;

namespace Libation.Mobile.Audible;

/// <summary>
/// Reads Audible chapter markers and nominal audio size for one title using the
/// content-metadata endpoint. It does not request a licence or retrieve audio.
/// </summary>
public sealed class SavedIdentityAudibleBookDetailsProvider : IAudiobookDetailsProvider
{
    private readonly AudibleIdentitySnapshotStore _snapshotStore;

    public SavedIdentityAudibleBookDetailsProvider(AudibleIdentitySnapshotStore snapshotStore)
    {
        _snapshotStore = snapshotStore ?? throw new ArgumentNullException(nameof(snapshotStore));
    }

    public async Task<ProviderAudiobookDetails?> GetDetailsAsync(
        string providerId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        cancellationToken.ThrowIfCancellationRequested();

        var api = await new AudibleApiSessionFactory(_snapshotStore)
            .CreateFromSavedIdentityAsync(cancellationToken)
            .ConfigureAwait(false);
        if (api is null)
            throw new InvalidOperationException("No saved Audible identity is available.");

        // This GET endpoint returns chapter_info and content_reference only.
        // No download licence is needed for chapter markers or content size.
        var metadata = await api.GetContentMetadataAsync(providerId, ChapterTitlesType.Tree)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (metadata is null)
            return null;

        var chapters = new List<AudiobookChapter>();
        if (metadata.ChapterInfo?.Chapters is { } roots)
        {
            foreach (var chapter in roots)
                AppendChapter(chapter, chapters);
        }

        return new ProviderAudiobookDetails(
            AudiobookChapterService.Normalize(chapters),
            Math.Max(0, metadata.ContentReference?.ContentSizeInBytes ?? 0));
    }

    private static void AppendChapter(Chapter chapter, ICollection<AudiobookChapter> destination)
    {
        if (chapter.Chapters is { Length: > 0 } children)
        {
            foreach (var child in children)
                AppendChapter(child, destination);
            return;
        }

        if (string.IsNullOrWhiteSpace(chapter.Title))
            return;

        var startMilliseconds = chapter.StartOffsetMs > 0
            ? chapter.StartOffsetMs
            : Math.Max(0, chapter.StartOffsetSec) * 1000;
        destination.Add(new AudiobookChapter(chapter.Title.Trim(), startMilliseconds));
    }
}
