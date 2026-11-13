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
    public async Task EnqueueAuthorizedDownload_PersistsDirectUrlAndDisplayTitle()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "queue.json");
        var queue = new JsonDownloadQueue(path);

        await queue.EnqueueAuthorizedAsync(
            "book-1", "https://cdn.example/audio/book.m4b", "Example audiobook");

        var item = Assert.Single(await new JsonDownloadQueue(path).GetItemsAsync());
        Assert.Equal("book-1", item.AudiobookId);
        Assert.Equal("https://cdn.example/audio/book.m4b", item.SourceUrl);
        Assert.Equal("Example audiobook", item.DisplayTitle);
        Assert.Equal(DownloadStatus.Queued, item.Status);
        Assert.Equal("Queued", item.StatusLabel);
    }

    [Fact]
    public async Task EnqueueAuthorizedDownload_RejectsUrlsWithQueryTokens()
    {
        using var directory = new TemporaryDirectory();
        var queue = new JsonDownloadQueue(Path.Combine(directory.Path, "queue.json"));

        await Assert.ThrowsAsync<ArgumentException>(() => queue.EnqueueAuthorizedAsync(
            "book-1", "https://cdn.example/book.m4b?token=private", "Example audiobook"));

        Assert.Empty(await queue.GetItemsAsync());
    }

    [Fact]
    public async Task Retry_PreservesAuthorizedSourceUrlAndDisplayTitle()
    {
        using var directory = new TemporaryDirectory();
        var queue = new JsonDownloadQueue(Path.Combine(directory.Path, "queue.json"));
        await queue.EnqueueAuthorizedAsync(
            "book-1", "https://cdn.example/audio/book.m4b", "Example audiobook");
        await queue.UpdateStatusAsync("book-1", DownloadStatus.Failed, "Network unavailable");

        await queue.EnqueueAsync("book-1");

        var item = Assert.Single(await queue.GetItemsAsync());
        Assert.Equal(DownloadStatus.Queued, item.Status);
        Assert.Equal("https://cdn.example/audio/book.m4b", item.SourceUrl);
        Assert.Equal("Example audiobook", item.DisplayTitle);
        Assert.Null(item.ErrorMessage);
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
    public async Task Remove_DoesNotRemoveAnActivelyDownloadingItem()
    {
        using var directory = new TemporaryDirectory();
        var queue = new JsonDownloadQueue(Path.Combine(directory.Path, "queue.json"));
        await queue.EnqueueAsync("book-active");
        await queue.UpdateStatusAsync("book-active", DownloadStatus.Downloading);

        Assert.False(await queue.RemoveAsync("book-active"));
        var item = Assert.Single(await queue.GetItemsAsync());
        Assert.Equal(DownloadStatus.Downloading, item.Status);
    }

    [Fact]
    public async Task UpdateStatus_UnknownBookThrows()
    {
        using var directory = new TemporaryDirectory();
        var queue = new JsonDownloadQueue(Path.Combine(directory.Path, "queue.json"));

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => queue.UpdateStatusAsync("missing", DownloadStatus.Downloading));
    }


    [Fact]
    public async Task UpdateProgress_PersistsMonotonicProgressAcrossQueueInstances()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "queue.json");
        var queue = new JsonDownloadQueue(path);
        await queue.EnqueueAsync("provider:B012345678");
        await queue.UpdateStatusAsync("provider:B012345678", DownloadStatus.Downloading);
        await queue.UpdateProgressAsync("provider:B012345678", 7_000, 10_000);

        // A late callback from an earlier segment must not move the visible progress backwards.
        await queue.UpdateProgressAsync("provider:B012345678", 2_000, 10_000);

        var item = Assert.Single(await new JsonDownloadQueue(path).GetItemsAsync());
        Assert.Equal(7_000, item.BytesReceived);
        Assert.Equal(10_000, item.TotalBytes);
        Assert.Equal(70d, item.ProgressPercentage);
        Assert.True(item.HasProgress);
        Assert.Equal("70% · 6.8 KB of 9.8 KB", item.ProgressLabel);
    }

    [Fact]
    public async Task PauseAndResumeTransitionsAreDurable()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "queue.json");
        var queue = new JsonDownloadQueue(path);
        await queue.EnqueueAsync("provider:B012345678");
        await queue.UpdateStatusAsync("provider:B012345678", DownloadStatus.Downloading);
        await queue.UpdateProgressAsync("provider:B012345678", 5_000, 10_000);
        await queue.UpdateStatusAsync("provider:B012345678", DownloadStatus.Paused);

        var paused = Assert.Single(await new JsonDownloadQueue(path).GetItemsAsync());
        Assert.Equal(DownloadStatus.Paused, paused.Status);
        Assert.Equal(5_000, paused.BytesReceived);
        Assert.True(paused.CanToggleAudibleDownload);
        Assert.Equal("Resume", paused.DownloadActionLabel);
        Assert.True(paused.CanRemove);

        await queue.UpdateStatusAsync("provider:B012345678", DownloadStatus.Downloading);
        var resumed = Assert.Single(await queue.GetItemsAsync());
        Assert.Equal(DownloadStatus.Downloading, resumed.Status);
        Assert.Equal(5_000, resumed.BytesReceived);
        Assert.Equal("Pause", resumed.DownloadActionLabel);
        Assert.False(resumed.CanRemove);
    }

    [Fact]
    public async Task ProgressUpdatesAreIgnoredAfterTheItemIsPaused()
    {
        using var directory = new TemporaryDirectory();
        var queue = new JsonDownloadQueue(Path.Combine(directory.Path, "queue.json"));
        await queue.EnqueueAsync("provider:B012345678");
        await queue.UpdateStatusAsync("provider:B012345678", DownloadStatus.Downloading);
        await queue.UpdateProgressAsync("provider:B012345678", 5_000, 10_000);
        await queue.UpdateStatusAsync("provider:B012345678", DownloadStatus.Paused);

        await queue.UpdateProgressAsync("provider:B012345678", 8_000, 10_000);

        var item = Assert.Single(await queue.GetItemsAsync());
        Assert.Equal(5_000, item.BytesReceived);
        Assert.Equal(DownloadStatus.Paused, item.Status);
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
