using Libation.Mobile.Core;
using Xunit;

namespace Libation.Mobile.Core.Tests;

public sealed class JsonDownloadQueueTests
{
    [Fact]
    public async Task Enqueue_PersistsItemAndDoesNotDuplicateIt()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "queue.json");
        var queue = new JsonDownloadQueue(path);

        await queue.EnqueueAsync("book-1");
        await queue.EnqueueAsync("book-1");

        var item = Assert.Single(await new JsonDownloadQueue(path).GetItemsAsync());
        Assert.Equal("book-1", item.AudiobookId);
        Assert.Equal(DownloadStatus.Queued, item.Status);
        Assert.False(item.CanRetry);
        Assert.False(item.HasErrorMessage);
        Assert.Null(item.ErrorMessage);
        Assert.Equal(item.AddedAtUtc, item.UpdatedAtUtc);
    }

    [Fact]
    public async Task StatusTransitions_ArePersisted()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "queue.json");
        var queue = new JsonDownloadQueue(path);
        await queue.EnqueueAsync("book-1");

        await queue.UpdateStatusAsync("book-1", DownloadStatus.Downloading);
        await queue.UpdateStatusAsync("book-1", DownloadStatus.Completed);

        var item = Assert.Single(await new JsonDownloadQueue(path).GetItemsAsync());
        Assert.Equal(DownloadStatus.Completed, item.Status);
        Assert.False(item.CanRetry);
        Assert.Null(item.ErrorMessage);
        Assert.True(item.UpdatedAtUtc >= item.AddedAtUtc);
    }

    [Fact]
    public async Task FailedItem_CanBeRetriedByEnqueueingAgain()
    {
        using var directory = new TemporaryDirectory();
        var queue = new JsonDownloadQueue(Path.Combine(directory.Path, "queue.json"));
        await queue.EnqueueAsync("book-1");
        await queue.UpdateStatusAsync("book-1", DownloadStatus.Failed, "Network unavailable");

        var failedItem = Assert.Single(await queue.GetItemsAsync());
        Assert.True(failedItem.CanRetry);
        Assert.True(failedItem.HasErrorMessage);
        Assert.Equal("Network unavailable", failedItem.ErrorMessage);

        await queue.EnqueueAsync("book-1");

        var retriedItem = Assert.Single(await queue.GetItemsAsync());
        Assert.Equal(DownloadStatus.Queued, retriedItem.Status);
        Assert.False(retriedItem.CanRetry);
        Assert.False(retriedItem.HasErrorMessage);
        Assert.Null(retriedItem.ErrorMessage);
    }

    [Fact]
    public async Task FailedStatus_RequiresAnErrorMessage()
    {
        using var directory = new TemporaryDirectory();
        var queue = new JsonDownloadQueue(Path.Combine(directory.Path, "queue.json"));
        await queue.EnqueueAsync("book-1");

        await Assert.ThrowsAsync<ArgumentException>(
            () => queue.UpdateStatusAsync("book-1", DownloadStatus.Failed));
        Assert.Equal(DownloadStatus.Queued, Assert.Single(await queue.GetItemsAsync()).Status);
    }

    [Fact]
    public async Task InvalidStatusTransition_IsRejected()
    {
        using var directory = new TemporaryDirectory();
        var queue = new JsonDownloadQueue(Path.Combine(directory.Path, "queue.json"));
        await queue.EnqueueAsync("book-1");
        await queue.UpdateStatusAsync("book-1", DownloadStatus.Downloading);
        await queue.UpdateStatusAsync("book-1", DownloadStatus.Completed);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => queue.UpdateStatusAsync("book-1", DownloadStatus.Queued));
        Assert.Equal(DownloadStatus.Completed, Assert.Single(await queue.GetItemsAsync()).Status);
    }

    [Fact]
    public async Task Remove_DeletesAnItemAndReportsWhetherItExisted()
    {
        using var directory = new TemporaryDirectory();
        var queue = new JsonDownloadQueue(Path.Combine(directory.Path, "queue.json"));
        await queue.EnqueueAsync("book-1");

        Assert.True(await queue.RemoveAsync("book-1"));
        Assert.False(await queue.RemoveAsync("book-1"));
        Assert.Empty(await queue.GetItemsAsync());
    }

    [Fact]
    public async Task UpdateStatus_UnknownBookThrows()
    {
        using var directory = new TemporaryDirectory();
        var queue = new JsonDownloadQueue(Path.Combine(directory.Path, "queue.json"));

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => queue.UpdateStatusAsync("missing", DownloadStatus.Downloading));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "LibationMobileQueueTests", Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
