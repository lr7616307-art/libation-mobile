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
    public async Task ProcessNextAsync_PersistsFinalDownloadProgressOnQueueItem()
    {
        using var directory = new TemporaryDirectory();
        var queuePath = Path.Combine(directory.Path, "queue.json");
        var queue = new JsonDownloadQueue(queuePath);
        await queue.EnqueueAsync("book-progress-worker");
        var payload = Enumerable.Range(0, 1_250_000).Select(index => (byte)(index % 251)).ToArray();
        using var client = new HttpClient(new StubHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(payload)
        }));
        var destination = Path.Combine(directory.Path, "book-progress.m4b");
        var worker = new DownloadQueueWorker(
            queue, new AudioFileDownloader(client),
            new StubSource(new Uri("https://provider.example/book-progress"), destination));

        Assert.True(await worker.ProcessNextAsync());

        // Reopen the JSON queue to prove the worker persisted its final byte counts.
        var item = Assert.Single(await new JsonDownloadQueue(queuePath).GetItemsAsync());
        Assert.Equal(DownloadStatus.Completed, item.Status);
        Assert.Equal(payload.LongLength, item.BytesReceived);
        Assert.Equal(payload.LongLength, item.TotalBytes);
        Assert.Equal(100d, item.ProgressPercentage);
    }

    [Fact]
    public async Task ProcessNextAsync_AttachesCompletedAudioToLocalCatalogue()
    {
        using var directory = new TemporaryDirectory();
        var queue = new JsonDownloadQueue(Path.Combine(directory.Path, "queue.json"));
        var repository = new JsonLibraryRepository(Path.Combine(directory.Path, "library.json"));
        var book = new Audiobook("book-attach", "Title", "Author", "", "", "Audible");
        await repository.ReplaceAllAsync([book]);
        await queue.EnqueueAsync(book.Id);
        var destination = Path.Combine(directory.Path, "book.m4b");
        using var client = new HttpClient(new StubHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[] { 1, 2, 3 })
        }));
        var worker = new DownloadQueueWorker(
            queue, new AudioFileDownloader(client),
            new StubSource(new Uri("https://provider.example/book"), destination),
            libraryRepository: repository);

        Assert.True(await worker.ProcessNextAsync());

        var savedBook = Assert.Single(await repository.GetBooksAsync());
        Assert.Equal(destination, savedBook.AudioFilePath);
        Assert.True(File.Exists(savedBook.AudioFilePath));
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


    [Fact]
    public async Task ProcessNextAsync_ConcurrentCallsNeverRunTwoDownloadsAtOnce()
    {
        using var directory = new TemporaryDirectory();
        var queue = new JsonDownloadQueue(Path.Combine(directory.Path, "queue.json"));
        await queue.EnqueueAsync("book-1");
        await queue.EnqueueAsync("book-2");
        using var client = new HttpClient(new StubHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            // The concurrency test exercises queue serialization, not empty-response handling.
            Content = new ByteArrayContent(new byte[] { 1 })
        }));
        var source = new ConcurrencyTrackingSource();
        var worker = new DownloadQueueWorker(queue, new AudioFileDownloader(client), source);

        var results = await Task.WhenAll(
            worker.ProcessNextAsync(),
            worker.ProcessNextAsync());

        Assert.All(results, Assert.True);
        Assert.Equal(2, source.CallCount);
        Assert.Equal(1, source.MaximumConcurrentCalls);
        Assert.All(await queue.GetItemsAsync(), item => Assert.Equal(DownloadStatus.Completed, item.Status));
    }


    [Fact]
    public async Task ProcessNextAsync_InvokesOptionalPostDownloadProcessorAfterTransfer()
    {
        using var directory = new TemporaryDirectory();
        var queue = new JsonDownloadQueue(Path.Combine(directory.Path, "queue.json"));
        await queue.EnqueueAsync("book-processed");
        var payload = new byte[] { 4, 5, 6 };
        using var client = new HttpClient(new StubHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(payload)
        }));
        var sourcePath = Path.Combine(directory.Path, "book.m4b");
        var processor = new RecordingProcessor();

        var worker = new DownloadQueueWorker(
            queue, new AudioFileDownloader(client),
            new StubSource(new Uri("https://provider.example/book"), sourcePath),
            processor);

        Assert.True(await worker.ProcessNextAsync());

        Assert.Equal(sourcePath, processor.InputPath);
        Assert.Equal(payload, await File.ReadAllBytesAsync(sourcePath));
        Assert.Equal(DownloadStatus.Completed, Assert.Single(await queue.GetItemsAsync()).Status);
    }

    [Fact]
    public async Task ProcessNextAsync_PostProcessorFailureMarksItemFailedAndKeepsDownloadedFile()
    {
        using var directory = new TemporaryDirectory();
        var queue = new JsonDownloadQueue(Path.Combine(directory.Path, "queue.json"));
        await queue.EnqueueAsync("book-processing-failed");
        var payload = new byte[] { 9, 8, 7 };
        using var client = new HttpClient(new StubHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(payload)
        }));
        var sourcePath = Path.Combine(directory.Path, "book.m4b");

        var worker = new DownloadQueueWorker(
            queue, new AudioFileDownloader(client),
            new StubSource(new Uri("https://provider.example/book"), sourcePath),
            new ThrowingProcessor());

        Assert.True(await worker.ProcessNextAsync());

        var item = Assert.Single(await queue.GetItemsAsync());
        Assert.Equal(DownloadStatus.Failed, item.Status);
        Assert.Equal(payload, await File.ReadAllBytesAsync(sourcePath));
    }

    private sealed class RecordingProcessor : IAudioPostDownloadProcessor
    {
        public string? InputPath { get; private set; }

        public Task<string> ProcessAsync(string downloadedFilePath, CancellationToken cancellationToken = default)
        {
            InputPath = downloadedFilePath;
            return Task.FromResult(downloadedFilePath);
        }
    }

    private sealed class ThrowingProcessor : IAudioPostDownloadProcessor
    {
        public Task<string> ProcessAsync(string downloadedFilePath, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Audio processing failed.");
    }

    private sealed class ConcurrencyTrackingSource : IAuthorizedAudioDownloadSource
    {
        private int _activeCalls;
        private int _maximumConcurrentCalls;
        private int _callCount;

        public int MaximumConcurrentCalls => _maximumConcurrentCalls;
        public int CallCount => _callCount;

        public async Task<AuthorizedAudioDownload> ResolveAsync(
            string audiobookId, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _callCount);
            var active = Interlocked.Increment(ref _activeCalls);
            UpdateMaximum(active);
            try
            {
                await Task.Delay(40, cancellationToken);
                return new AuthorizedAudioDownload(
                    new Uri("https://provider.example/" + audiobookId),
                    Path.Combine(Path.GetTempPath(), "LibationMobileWorkerTests", audiobookId + ".m4b"));
            }
            finally
            {
                Interlocked.Decrement(ref _activeCalls);
            }
        }

        private void UpdateMaximum(int active)
        {
            int observed;
            do
            {
                observed = _maximumConcurrentCalls;
                if (active <= observed) return;
            }
            while (Interlocked.CompareExchange(ref _maximumConcurrentCalls, active, observed) != observed);
        }
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
