using Libation.Mobile.Core;
using Xunit;

namespace Libation.Mobile.Core.Tests;

public sealed class DownloadQueueItemPresentationTests
{
    [Fact]
    public void ProgressPresentation_ShowsPercentageAndFormattedByteCounts()
    {
        var item = CreateItem() with { BytesReceived = 25, TotalBytes = 100 };

        Assert.True(item.HasProgress);
        Assert.True(item.HasKnownProgress);
        Assert.Equal(25d, item.ProgressPercentage);
        Assert.Equal("25 B of 100 B · 25%", item.ProgressLabel);
    }

    [Fact]
    public void ProgressPresentation_ShowsPartialBytesWhenTotalSizeIsUnknown()
    {
        var item = CreateItem() with { BytesReceived = 1536 };

        Assert.True(item.HasProgress);
        Assert.False(item.HasKnownProgress);
        Assert.Null(item.ProgressPercentage);
        Assert.Equal("1.5 KB downloaded · total size unknown", item.ProgressLabel);
    }

    [Fact]
    public void ProgressPresentation_IsHiddenWhenNoBytesHaveBeenReceived()
    {
        var item = CreateItem();

        Assert.False(item.HasProgress);
        Assert.False(item.HasKnownProgress);
        Assert.Equal(string.Empty, item.ProgressLabel);
    }

    private static DownloadQueueItem CreateItem()
        => new("book-1", DownloadStatus.Queued, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
}
