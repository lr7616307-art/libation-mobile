namespace Libation.Mobile.Core;

/// <summary>Validates and navigates chapter markers for an audiobook.</summary>
public static class AudiobookChapterService
{
    /// <summary>
    /// Returns chapter markers in playback order, removing invalid entries and duplicate
    /// start times. A positive duration excludes markers at or beyond the end of the book.
    /// </summary>
    public static IReadOnlyList<AudiobookChapter> Normalize(
        IEnumerable<AudiobookChapter>? chapters,
        long durationMilliseconds = 0)
    {
        if (chapters is null)
            return Array.Empty<AudiobookChapter>();

        var normalized = chapters
            .Where(chapter => chapter is not null
                && !string.IsNullOrWhiteSpace(chapter.Title)
                && chapter.StartPositionMilliseconds >= 0
                && (durationMilliseconds <= 0 || chapter.StartPositionMilliseconds < durationMilliseconds))
            .Select(chapter => chapter with { Title = chapter.Title.Trim() })
            .OrderBy(chapter => chapter.StartPositionMilliseconds)
            .GroupBy(chapter => chapter.StartPositionMilliseconds)
            .Select(group => group.First())
            .ToArray();

        return Array.AsReadOnly(normalized);
    }

    /// <summary>Returns the index of the chapter containing the given position, or -1 before the first chapter.</summary>
    public static int FindCurrentChapterIndex(
        IReadOnlyList<AudiobookChapter>? chapters,
        long positionMilliseconds)
    {
        if (chapters is null || chapters.Count == 0 || positionMilliseconds < 0)
            return -1;

        var low = 0;
        var high = chapters.Count - 1;
        var result = -1;
        while (low <= high)
        {
            var middle = low + ((high - low) / 2);
            if (chapters[middle].StartPositionMilliseconds <= positionMilliseconds)
            {
                result = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return result;
    }

    /// <summary>Returns the next chapter start after the current position, or null at the end.</summary>
    public static long? GetNextChapterPosition(
        IReadOnlyList<AudiobookChapter>? chapters,
        long positionMilliseconds)
    {
        if (chapters is null)
            return null;

        foreach (var chapter in chapters)
        {
            if (chapter.StartPositionMilliseconds > positionMilliseconds)
                return chapter.StartPositionMilliseconds;
        }

        return null;
    }
}
