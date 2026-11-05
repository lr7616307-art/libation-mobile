using Libation.Mobile.Core;
using Xunit;

namespace Libation.Mobile.Core.Tests;

public sealed class AudiobookChapterServiceTests
{
    [Fact]
    public void Normalize_SortsTrimsAndRemovesInvalidOrDuplicateMarkers()
    {
        AudiobookChapter[] input =
        [
            new("  Chapter Two  ", 20_000),
            new("", 5_000),
            new("Negative", -1),
            new("Duplicate", 20_000),
            new("Chapter One", 0),
            new("Beyond duration", 100_000)
        ];

        var result = AudiobookChapterService.Normalize(input, durationMilliseconds: 90_000);

        Assert.Collection(result,
            chapter => { Assert.Equal("Chapter One", chapter.Title); Assert.Equal(0, chapter.StartPositionMilliseconds); },
            chapter => { Assert.Equal("Chapter Two", chapter.Title); Assert.Equal(20_000, chapter.StartPositionMilliseconds); });
    }

    [Fact]
    public void FindCurrentChapterIndex_ReturnsChapterContainingPosition()
    {
        AudiobookChapter[] chapters =
        [
            new("Intro", 0),
            new("Part One", 30_000),
            new("Part Two", 90_000)
        ];

        Assert.Equal(-1, AudiobookChapterService.FindCurrentChapterIndex(chapters, -1));
        Assert.Equal(0, AudiobookChapterService.FindCurrentChapterIndex(chapters, 0));
        Assert.Equal(0, AudiobookChapterService.FindCurrentChapterIndex(chapters, 29_999));
        Assert.Equal(1, AudiobookChapterService.FindCurrentChapterIndex(chapters, 30_000));
        Assert.Equal(2, AudiobookChapterService.FindCurrentChapterIndex(chapters, 120_000));
    }

    [Fact]
    public void GetNextChapterPosition_ReturnsNextMarkerOrNull()
    {
        AudiobookChapter[] chapters =
        [
            new("Intro", 0),
            new("Part One", 30_000),
            new("Part Two", 90_000)
        ];

        Assert.Equal(30_000, AudiobookChapterService.GetNextChapterPosition(chapters, 1_000));
        Assert.Equal(90_000, AudiobookChapterService.GetNextChapterPosition(chapters, 30_000));
        Assert.Null(AudiobookChapterService.GetNextChapterPosition(chapters, 90_000));
    }
}
