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
    public async Task ProcessNextAsync_PublishesDownloadedFileBeforeMarkingCompletedAndCleansStagingCopy()
    {
        using var directory = new TemporaryDirectory();
        var queue = new JsonDownloadQueue(Path.Combine(directory.Path, "queue.json"));
        await queue.EnqueueAsync("book-publish");
        var staging = Path.Combine(directory.Path, "book-publish.m4b");
        var payload = new byte[] { 4, 5, 6, 7 };
        using var client = new HttpClient(new StubHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(payload)
        }));
        var publisher = new RecordingCompletionHandler();
        var worker = new DownloadQueueWorker(
            queue,
            new AudioFileDownloader(client),
            new StubSource(new Uri("https://provider.example/book-publish"), staging),
            publisher);

        Assert.True(await worker.ProcessNextAsync());

        Assert.Equal(payload, publisher.PublishedBytes);
        Assert.False(File.Exists(staging));
        Assert.Equal(DownloadStatus.Completed, Assert.Single(await queue.GetItemsAsync()).Status);
    }

    [Fact]
    public async Task ProcessNextAsync_ProtectedMediaIsSavedWithoutMarkingItPlayable()
    {
        using var directory = new TemporaryDirectory();
        var queue = new JsonDownloadQueue(Path.Combine(directory.Path, "queue.json"));
        await queue.EnqueueAsync("book-protected-media");
        var staging = Path.Combine(directory.Path, "protected.aaxc");
        var payload = new byte[] { 9, 8, 7, 6 };
        using var client = new HttpClient(new StubHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(payload)
        }));
        var protection = new AudioProtectionDescriptor(AudioProtectionKind.AudibleAdrm, "AAXC");
        var source = new ProtectedStubSource(
            new Uri("https://audible.example/offline-media"),
            staging,
            protection);
        var publisher = new RecordingProtectedCompletionHandler();
        var worker = new DownloadQueueWorker(
            queue,
            new AudioFileDownloader(client),
            source,
            publisher);

        Assert.True(await worker.ProcessNextAsync());

        var item = Assert.Single(await queue.GetItemsAsync());
        Assert.Equal(DownloadStatus.Protected, item.Status);
        Assert.Equal(payload, publisher.PublishedProtectedBytes);
        Assert.Equal(protection, publisher.PublishedProtection);
        Assert.False(publisher.NormalPublishCalled);
        Assert.False(File.Exists(staging));
    }

    [Fact]
    public async Task ProcessNextAsync_EmptyResponseFailsAndDoesNotPublishFile()
    {
        using var directory = new TemporaryDirectory();
        var destination = Path.Combine(directory.Path, "empty.m4b");
        var queue = new JsonDownloadQueue(Path.Combine(directory.Path, "queue.json"));
        await queue.EnqueueAsync("book-empty");
        using var client = new HttpClient(new StubHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([])
        }));
        var worker = new DownloadQueueWorker(
            queue, new AudioFileDownloader(client),
            new StubSource(new Uri("https://provider.example/book-empty"), destination));

        Assert.True(await worker.ProcessNextAsync());

        var item = Assert.Single(await queue.GetItemsAsync());
        Assert.Equal(DownloadStatus.Failed, item.Status);
        Assert.Contains("validation", item.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(destination));
        Assert.Empty(Directory.GetFiles(directory.Path, "*.part"));
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
    public async Task ProcessNextAsync_DoesNotPersistSensitiveProviderExceptionText()
    {
        using var directory = new TemporaryDirectory();
        var queue = new JsonDownloadQueue(Path.Combine(directory.Path, "queue.json"));
        await queue.EnqueueAsync("book-private");
        using var client = new HttpClient(new StubHandler(() => new HttpResponseMessage(HttpStatusCode.OK)));
        var worker = new DownloadQueueWorker(queue, new AudioFileDownloader(client),
            new SensitiveFailureSource());

        Assert.True(await worker.ProcessNextAsync());

        var item = Assert.Single(await queue.GetItemsAsync());
        Assert.Equal(DownloadStatus.Failed, item.Status);
        Assert.DoesNotContain("signed.example", item.ErrorMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-token", item.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("authorize", item.ErrorMessage, StringComparison.OrdinalIgnoreCase);
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
            Content = new ByteArrayContent([7, 8, 9])
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
    public async Task RecoverInterruptedDownloadsAsync_RequeuesAudibleAdrmProviderJobs()
    {
        using var directory = new TemporaryDirectory();
        var queue = new JsonDownloadQueue(Path.Combine(directory.Path, "queue.json"));
        await queue.EnqueueProviderAsync("provider:B012345678", "Test title");
        await queue.UpdateStatusAsync("provider:B012345678", DownloadStatus.Downloading);
        using var client = new HttpClient(new StubHandler(() => new HttpResponseMessage(HttpStatusCode.OK)));
        var worker = new DownloadQueueWorker(queue, new AudioFileDownloader(client),
            new StubSource(new Uri("https://provider.example/book"), Path.Combine(directory.Path, "book.aaxc")));

        var recovered = await worker.RecoverInterruptedDownloadsAsync();

        Assert.Equal(1, recovered);
        Assert.Equal(DownloadStatus.Queued, Assert.Single(await queue.GetItemsAsync()).Status);
    }

    [Fact]
    public async Task ProcessNextAsync_DownloadsAudibleAdrmProviderJobAndKeepsItProtected()
    {
        using var directory = new TemporaryDirectory();
        var queue = new JsonDownloadQueue(Path.Combine(directory.Path, "queue.json"));
        await queue.EnqueueProviderAsync("provider:B012345678", "Test title");
        var staging = Path.Combine(directory.Path, "book.aaxc");
        var payload = new byte[] { 8, 6, 7, 5 };
        using var client = new HttpClient(new StubHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(payload)
        }));
        var protection = new AudioProtectionDescriptor(AudioProtectionKind.AudibleAdrm, "AAXC");
        var source = new ProtectedStubSource(
            new Uri("https://audible.example/offline-media"), staging, protection);
        var publisher = new RecordingProtectedCompletionHandler();
        var worker = new DownloadQueueWorker(queue, new AudioFileDownloader(client), source, publisher);

        Assert.True(await worker.ProcessNextAsync());

        var item = Assert.Single(await queue.GetItemsAsync());
        Assert.Equal(DownloadStatus.Protected, item.Status);
        Assert.Equal(payload, publisher.PublishedProtectedBytes);
        Assert.Equal(protection, publisher.PublishedProtection);
        Assert.False(publisher.NormalPublishCalled);
    }

    private sealed class ProtectedStubSource(
        Uri uri,
        string path,
        AudioProtectionDescriptor protection) : IAuthorizedAudioDownloadSource
    {
        public Task<AuthorizedAudioDownload> ResolveAsync(
            string audiobookId, CancellationToken cancellationToken = default)
            => Task.FromResult(new AuthorizedAudioDownload(uri, path) { Protection = protection });
    }

    private sealed class RecordingProtectedCompletionHandler :
        IAudioDownloadCompletionHandler, IProtectedAudioDownloadCompletionHandler
    {
        public byte[] PublishedProtectedBytes { get; private set; } = [];
        public AudioProtectionDescriptor? PublishedProtection { get; private set; }
        public bool NormalPublishCalled { get; private set; }

        public Task PublishAsync(
            DownloadQueueItem item,
            string stagingFilePath,
            CancellationToken cancellationToken = default)
        {
            NormalPublishCalled = true;
            return Task.CompletedTask;
        }

        public async Task PublishProtectedAsync(
            DownloadQueueItem item,
            string stagingFilePath,
            AudioProtectionDescriptor protection,
            CancellationToken cancellationToken = default)
        {
            PublishedProtectedBytes = await File.ReadAllBytesAsync(stagingFilePath, cancellationToken);
            PublishedProtection = protection;
        }
    }

    private sealed class RecordingCompletionHandler : IAudioDownloadCompletionHandler
    {
        public byte[] PublishedBytes { get; private set; } = [];

        public async Task PublishAsync(
            DownloadQueueItem item,
            string stagingFilePath,
            CancellationToken cancellationToken = default)
        {
            PublishedBytes = await File.ReadAllBytesAsync(stagingFilePath, cancellationToken);
        }
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

    private sealed class SensitiveFailureSource : IAuthorizedAudioDownloadSource
    {
        public Task<AuthorizedAudioDownload> ResolveAsync(
            string audiobookId, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException(
                "Request failed for https://signed.example/book?token=secret-token");
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