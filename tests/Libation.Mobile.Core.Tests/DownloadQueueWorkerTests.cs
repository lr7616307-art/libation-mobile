using System.Net;
using Libation.Mobile.Core;
using Xunit;

namespace Libation.Mobile.Core.Tests;

public sealed class DownloadQueueWorkerTests
{
    [Fact]
    public async Task ProcessNextAsync_DownloadsAndMarksItemCompleted()
    {
        using var directory = new TemporaryDirectory();
        var queue = new JsonDownloadQueue(Path.Combine(directory.Path, "queue.json"));
        await queue.EnqueueAsync("book-1");
        var payload = new byte[] { 7, 8, 9 };
        using var client = new HttpClient(new StubHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(payload)
        }));
        var worker = new DownloadQueueWorker(
            queue, new AudioFileDownloader(client),
            new StubSource(new Uri("https://provider.example/book"), Path.Combine(directory.Path, "book.m4b")));

        Assert.True(await worker.ProcessNextAsync());

        var item = Assert.Single(await queue.GetItemsAsync());
        Assert.Equal(DownloadStatus.Completed, item.Status);
        Assert.Equal(payload, await File.ReadAllBytesAsync(Path.Combine(directory.Path, "book.m4b")));
    }

    [Fact]
    public async Task RecoverInterruptedDownloadsAsync_RequeuesItemsLeftDownloading()
    {
        using var directory = new TemporaryDirectory();
        var queue = new JsonDownloadQueue(Path.Combine(directory.Path, "queue.json"));
        await queue.EnqueueAsync("book-interrupted");
        await queue.UpdateStatusAsync("book-interrupted", DownloadStatus.Downloading);
        using var client = new HttpClient(new StubHandler(() => new HttpResponseMessage(HttpStatusCode.OK)));
        var worker = new DownloadQueueWorker(queue, new AudioFileDownloader(client),
            new StubSource(new Uri("https://provider.example/book"), Path.Combine(directory.Path, "book.m4b")));

        var recovered = await worker.RecoverInterruptedDownloadsAsync();

        Assert.Equal(1, recovered);
        Assert.Equal(DownloadStatus.Queued, Assert.Single(await queue.GetItemsAsync()).Status);
    }

    [Fact]
    public async Task ProcessNextAsync_NoQueuedItemsReturnsFalse()
    {
        using var directory = new TemporaryDirectory();
        var queue = new JsonDownloadQueue(Path.Combine(directory.Path, "queue.json"));
        using var client = new HttpClient(new StubHandler(() => new HttpResponseMessage(HttpStatusCode.OK)));
        var worker = new DownloadQueueWorker(queue, new AudioFileDownloader(client),
            new StubSource(new Uri("https://provider.example/book"), Path.Combine(directory.Path, "book.m4b")));

        Assert.False(await worker.ProcessNextAsync());
    }

    [Fact]
    public async Task ProcessNextAsync_ProviderFailureMarksItemFailed()
    {
        using var directory = new TemporaryDirectory();
        var queue = new JsonDownloadQueue(Path.Combine(directory.Path, "queue.json"));
        await queue.EnqueueAsync("book-1");
        using var client = new HttpClient(new StubHandler(() => new HttpResponseMessage(HttpStatusCode.OK)));
        var worker = new DownloadQueueWorker(queue, new AudioFileDownloader(client),
            new ThrowingSource());

        Assert.True(await worker.ProcessNextAsync());

        var item = Assert.Single(await queue.GetItemsAsync());
        Assert.Equal(DownloadStatus.Failed, item.Status);
        Assert.True(item.CanRetry);
        Assert.False(string.IsNullOrWhiteSpace(item.ErrorMessage));
    }


    [Fact]
    public async Task ProcessNextAsync_CancellationReturnsItemToQueued()
    {
        using var directory = new TemporaryDirectory();
        var queue = new JsonDownloadQueue(Path.Combine(directory.Path, "queue.json"));
        await queue.EnqueueAsync("book-cancelled");
        using var client = new HttpClient(new StubHandler(() => new HttpResponseMessage(HttpStatusCode.OK)));
        using var cancellation = new CancellationTokenSource();
        var worker = new DownloadQueueWorker(queue, new AudioFileDownloader(client),
            new CancellingSource(cancellation));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => worker.ProcessNextAsync(cancellationToken: cancellation.Token));

        var item = Assert.Single(await queue.GetItemsAsync());
        Assert.Equal(DownloadStatus.Queued, item.Status);
        Assert.Null(item.ErrorMessage);
    }

    private sealed class CancellingSource(CancellationTokenSource cancellation) : IAuthorizedAudioDownloadSource
    {
        public Task<AuthorizedAudioDownload> ResolveAsync(
            string audiobookId, CancellationToken cancellationToken = default)
        {
            cancellation.Cancel();
            return Task.FromCanceled<AuthorizedAudioDownload>(cancellationToken);
        }
    }

    private sealed class StubSource(Uri uri, string path) : IAuthorizedAudioDownloadSource
    {
        public Task<AuthorizedAudioDownload> ResolveAsync(
            string audiobookId, CancellationToken cancellationToken = default)
            => Task.FromResult(new AuthorizedAudioDownload(uri, path));
    }

    private sealed class ThrowingSource : IAuthorizedAudioDownloadSource
    {
        public Task<AuthorizedAudioDownload> ResolveAsync(
            string audiobookId, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("No authorized download URL is available.");
    }

    private sealed class StubHandler(Func<HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(response());
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "LibationMobileWorkerTests", Guid.NewGuid().ToString("N"));
        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
