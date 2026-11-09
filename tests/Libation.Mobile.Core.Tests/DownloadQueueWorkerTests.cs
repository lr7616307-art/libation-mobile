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
    public async Task ProcessAllAsync_DrainsQueuedItemsSequentially()
    {
        using var directory = new TemporaryDirectory();
        var queue = new JsonDownloadQueue(Path.Combine(directory.Path, "queue.json"));
        await queue.EnqueueAsync("book-1");
        await queue.EnqueueAsync("book-2");
        using var client = new HttpClient(new StubHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[] { 1, 2, 3 })
        }));
        var source = new ConcurrencyTrackingSource();
        var worker = new DownloadQueueWorker(queue, new AudioFileDownloader(client), source);

        var processed = await worker.ProcessAllAsync();

        Assert.Equal(2, processed);
        Assert.Equal(2, source.CallCount);
        Assert.Equal(1, source.MaximumConcurrentCalls);
        Assert.All(await queue.GetItemsAsync(), item => Assert.Equal(DownloadStatus.Completed, item.Status));
    }

    [Fact]
    public async Task ProcessAllAsync_ContinuesAfterAnItemFails()
    {
        using var directory = new TemporaryDirectory();
        var queue = new JsonDownloadQueue(Path.Combine(directory.Path, "queue.json"));
        await queue.EnqueueAsync("book-fails");
        await queue.EnqueueAsync("book-succeeds");
        using var client = new HttpClient(new StubHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[] { 1, 2, 3 })
        }));
        var source = new FailFirstSource(Path.Combine(directory.Path, "book.m4b"));
        var worker = new DownloadQueueWorker(queue, new AudioFileDownloader(client), source);

        var processed = await worker.ProcessAllAsync();

        Assert.Equal(2, processed);
        var items = await queue.GetItemsAsync();
        Assert.Equal(DownloadStatus.Failed, Assert.Single(items.Where(x => x.AudiobookId == "book-fails")).Status);
        Assert.Equal(DownloadStatus.Completed, Assert.Single(items.Where(x => x.AudiobookId == "book-succeeds")).Status);
        Assert.True(File.Exists(Path.Combine(directory.Path, "book.m4b")));
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
    public async Task ProcessNextAsync_CancellationPersistsBytesWrittenToPartialFile()
    {
        using var directory = new TemporaryDirectory();
        var queuePath = Path.Combine(directory.Path, "queue.json");
        var queue = new JsonDownloadQueue(queuePath);
        await queue.EnqueueAsync("book-partial");
        using var cancellation = new CancellationTokenSource();
        using var client = new HttpClient(new StubHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new CancelAfterFirstReadStream(
                Enumerable.Range(0, 128 * 1024).Select(index => (byte)(index % 251)).ToArray(),
                cancellation))
        }));
        var destination = Path.Combine(directory.Path, "book-partial.m4b");
        var worker = new DownloadQueueWorker(
            queue, new AudioFileDownloader(client),
            new StubSource(new Uri("https://provider.example/book-partial"), destination));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => worker.ProcessNextAsync(cancellationToken: cancellation.Token));

        var saved = Assert.Single(await new JsonDownloadQueue(queuePath).GetItemsAsync());
        var partialPath = destination + ".part";
        Assert.Equal(DownloadStatus.Queued, saved.Status);
        Assert.True(File.Exists(partialPath));
        Assert.True(saved.BytesReceived > 0);
        Assert.Equal(new FileInfo(partialPath).Length, saved.BytesReceived);
    }

    [Fact]
    public async Task ProcessNextAsync_RestartResumesPartialDownloadAndCompletesFile()
    {
        using var directory = new TemporaryDirectory();
        var queuePath = Path.Combine(directory.Path, "queue.json");
        var queue = new JsonDownloadQueue(queuePath);
        await queue.EnqueueAsync("book-restart-resume");
        var destination = Path.Combine(directory.Path, "book-restart-resume.m4b");
        var payload = Enumerable.Range(0, 128 * 1024).Select(index => (byte)(index % 251)).ToArray();
        using var cancellation = new CancellationTokenSource();
        using (var interruptedClient = new HttpClient(new StubHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new CancelAfterFirstReadStream(payload, cancellation))
        })))
        {
            var interruptedWorker = new DownloadQueueWorker(
                queue, new AudioFileDownloader(interruptedClient),
                new StubSource(new Uri("https://provider.example/book-restart-resume"), destination));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => interruptedWorker.ProcessNextAsync(cancellationToken: cancellation.Token));
        }

        var savedAfterInterruption = Assert.Single(await new JsonDownloadQueue(queuePath).GetItemsAsync());
        var partialPath = destination + ".part";
        var offset = new FileInfo(partialPath).Length;
        Assert.Equal(DownloadStatus.Queued, savedAfterInterruption.Status);
        Assert.True(offset > 0);
        Assert.Equal(offset, savedAfterInterruption.BytesReceived);
        Assert.True(offset < payload.Length);

        var rangeWasRequested = false;
        using var resumedClient = new HttpClient(new RequestAwareStubHandler(request =>
        {
            rangeWasRequested = true;
            Assert.Equal(offset, request.Headers.Range?.Ranges.Single().From);
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(payload[(int)offset..])
            };
            response.Content.Headers.ContentRange =
                new System.Net.Http.Headers.ContentRangeHeaderValue(offset, payload.Length - 1, payload.Length);
            return response;
        }));
        var resumedWorker = new DownloadQueueWorker(
            new JsonDownloadQueue(queuePath), new AudioFileDownloader(resumedClient),
            new StubSource(new Uri("https://provider.example/book-restart-resume"), destination));

        Assert.True(await resumedWorker.ProcessNextAsync());

        Assert.True(rangeWasRequested);
        Assert.Equal(payload, await File.ReadAllBytesAsync(destination));
        Assert.False(File.Exists(partialPath));
        var completed = Assert.Single(await new JsonDownloadQueue(queuePath).GetItemsAsync());
        Assert.Equal(DownloadStatus.Completed, completed.Status);
        Assert.Equal(payload.LongLength, completed.BytesReceived);
        Assert.Equal(payload.LongLength, completed.TotalBytes);
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
    public async Task ProcessAllAsync_CancellationKeepsCurrentAndUnstartedItemsQueued()
    {
        using var directory = new TemporaryDirectory();
        var queue = new JsonDownloadQueue(Path.Combine(directory.Path, "queue.json"));
        await queue.EnqueueAsync("book-current");
        await queue.EnqueueAsync("book-pending");
        using var client = new HttpClient(new StubHandler(() => new HttpResponseMessage(HttpStatusCode.OK)));
        using var cancellation = new CancellationTokenSource();
        var worker = new DownloadQueueWorker(
            queue, new AudioFileDownloader(client), new CancellingSource(cancellation));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => worker.ProcessAllAsync(cancellationToken: cancellation.Token));

        var items = await queue.GetItemsAsync();
        Assert.All(items, item => Assert.Equal(DownloadStatus.Queued, item.Status));
    }


    [Fact]
    public async Task ExecutionController_CancelStopsRunAndReleasesControllerForNextRun()
    {
        using var directory = new TemporaryDirectory();
        var queue = new JsonDownloadQueue(Path.Combine(directory.Path, "queue.json"));
        await queue.EnqueueAsync("book-controller");
        using var client = new HttpClient(new StubHandler(() => new HttpResponseMessage(HttpStatusCode.OK)));
        var source = new BlockingSource();
        var worker = new DownloadQueueWorker(queue, new AudioFileDownloader(client), source);
        var controller = new DownloadQueueExecutionController(worker);

        var run = controller.RunAsync();
        await source.Started;
        Assert.True(controller.IsRunning);
        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.RunAsync());

        controller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        Assert.False(controller.IsRunning);
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


    private sealed class BlockingSource : IAuthorizedAudioDownloadSource
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Started => _started.Task;

        public async Task<AuthorizedAudioDownload> ResolveAsync(
            string audiobookId, CancellationToken cancellationToken = default)
        {
            _started.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("The blocked source should only finish through cancellation.");
        }
    }

    private sealed class FailFirstSource(string destinationPath) : IAuthorizedAudioDownloadSource
    {
        public Task<AuthorizedAudioDownload> ResolveAsync(
            string audiobookId, CancellationToken cancellationToken = default)
        {
            if (audiobookId == "book-fails")
                throw new InvalidOperationException("Provider temporarily unavailable.");
            return Task.FromResult(new AuthorizedAudioDownload(
                new Uri("https://provider.example/" + audiobookId), destinationPath));
        }
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

    private sealed class CancelAfterFirstReadStream(byte[] payload, CancellationTokenSource cancellation) : Stream
    {
        private int _position;
        private bool _firstReadCompleted;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => payload.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = Math.Min(count, payload.Length - _position);
            Array.Copy(payload, _position, buffer, offset, read);
            _position += read;
            return read;
        }
        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (_firstReadCompleted)
            {
                cancellation.Cancel();
                return ValueTask.FromCanceled<int>(cancellationToken);
            }

            var read = Math.Min(buffer.Length, payload.Length - _position);
            payload.AsMemory(_position, read).CopyTo(buffer);
            _position += read;
            _firstReadCompleted = true;
            return ValueTask.FromResult(read);
        }
    }

    private sealed class RequestAwareStubHandler(Func<HttpRequestMessage, HttpResponseMessage> response)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(response(request));
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpResponseMessage>? _response;
        private readonly Func<HttpRequestMessage, HttpResponseMessage>? _requestResponse;

        public StubHandler(Func<HttpResponseMessage> response) => _response = response;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> response)
            => _requestResponse = response;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_requestResponse is not null
                ? _requestResponse(request)
                : _response!());
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
