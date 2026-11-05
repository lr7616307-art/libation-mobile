using Libation.Mobile.Core;
using Xunit;

namespace Libation.Mobile.Core.Tests;

public sealed class AudiobookResumePositionTests
{
    [Fact]
    public void ResumePositionLabel_IsEmptyWhenPlaybackHasNotStarted()
    {
        var book = new Audiobook("book-1", "Title", "Author", "", "", "Manual");

        Assert.False(book.HasSavedPlaybackPosition);
        Assert.Equal(string.Empty, book.ResumePositionLabel);
    }

    [Theory]
    [InlineData(1_000L, "Resume at 00:00:01")]
    [InlineData(3_723_000L, "Resume at 01:02:03")]
    [InlineData(90_061_000L, "Resume at 25:01:01")]
    public void ResumePositionLabel_FormatsTotalHoursWithoutWrapping(long milliseconds, string expected)
    {
        var book = new Audiobook("book-1", "Title", "Author", "", "", "Manual")
        {
            PlaybackPositionMilliseconds = milliseconds
        };

        Assert.True(book.HasSavedPlaybackPosition);
        Assert.Equal(expected, book.ResumePositionLabel);
    }
}
