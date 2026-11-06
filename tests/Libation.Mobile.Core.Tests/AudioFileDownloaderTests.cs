using System.Net;
using Libation.Mobile.Core;
using Xunit;

namespace Libation.Mobile.Core.Tests;

public sealed class AudioFileDownloaderTests
{
    [Fact]
    public async Task DownloadAsync_WritesResponseAndReportsProgress()
    {
        using var directory = new TemporaryDirectory();
        var payload = new byte[] { 1, 2, 3, 4, 5 };
        using var client = new HttpClient(new StubHandler((_, _) =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload)
            }));
        var downloader = new AudioFileDownloader(client);
        var progress = new RecordingProgress<AudioDownloadProgress>();
        var destination = Path.Combine(directory.Path, "book.m4b");

        var result = await downloader.DownloadAsync(
            new Uri("https://provider.example/audio"), destination, progress);

        Assert.Equal(payload, await File.ReadAllBytesAsync(destination));
        Assert.Equal(payload.Length, result.BytesReceived);
        Assert.Equal(destination, result.FilePath);
        Assert.NotEmpty(progress.Values);
        Assert.Equal(100d, progress.Values[^1].Percentage);
    }


    [Fact]
    public async Task DownloadAsync_ResumesExistingPartialFileWithRangeRequest()
    {
        using var directory = new TemporaryDirectory();
        var destination = Path.Combine(directory.Path, "book.m4b");
        var partial = destination + ".part";
        Directory.CreateDirectory(directory.Path);
        await File.WriteAllBytesAsync(partial, new byte[] { 1, 2 });
        using var client = new HttpClient(new StubHandler((request, _) =>
        {
            Assert.Equal(2, request.Headers.Range?.Ranges.Single().From);
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(new byte[] { 3, 4, 5 })
            };
            response.Content.Headers.ContentRange =
                new System.Net.Http.Headers.ContentRangeHeaderValue(2, 4, 5);
            return response;
        }));
        var downloader = new AudioFileDownloader(client);

        var result = await downloader.DownloadAsync(
            new Uri("https://provider.example/audio"), destination);

        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, await File.ReadAllBytesAsync(destination));
        Assert.Equal(5, result.BytesReceived);
        Assert.False(File.Exists(partial));
    }

    [Fact]
    public async Task DownloadAsync_RestartsWhenServerIgnoresRangeRequest()
    {
        using var directory = new TemporaryDirectory();
        var destination = Path.Combine(directory.Path, "book.m4b");
        Directory.CreateDirectory(directory.Path);
        await File.WriteAllBytesAsync(destination + ".part", new byte[] { 9, 9 });
        using var client = new HttpClient(new StubHandler((request, _) =>
        {
            Assert.NotNull(request.Headers.Range);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[] { 1, 2, 3 })
            };
        }));
        var downloader = new AudioFileDownloader(client);

        await downloader.DownloadAsync(new Uri("https://provider.example/audio"), destination);

        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(destination));
    }

    [Fact]
    public async Task DownloadAsync_RejectsEmptySuccessfulResponseAndPreservesExistingDestination()
    {
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(directory.Path);
        var destination = Path.Combine(directory.Path, "book.m4b");
        var partial = destination + ".part";
        await File.WriteAllTextAsync(destination, "existing");
        using var client = new HttpClient(new StubHandler((_, _) =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([])
            }));
        var downloader = new AudioFileDownloader(client);

        await Assert.ThrowsAsync<InvalidDataException>(() => downloader.DownloadAsync(
            new Uri("https://provider.example/audio"), destination));

        Assert.Equal("existing", await File.ReadAllTextAsync(destination));
        Assert.True(File.Exists(partial));
        Assert.Empty(await File.ReadAllBytesAsync(partial));
    }

    [Fact]
    public async Task DownloadAsync_CancellationKeepsPartialAndPreservesExistingDestination()
    {
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(directory.Path);
        var destination = Path.Combine(directory.Path, "book.m4b");
        var partial = destination + ".part";
        await File.WriteAllTextAsync(destination, "existing");
        var payload = Enumerable.Range(0, 128 * 1024).Select(i => (byte)(i % 251)).ToArray();
        using var cancellation = new CancellationTokenSource();
        using var client = new HttpClient(new StubHandler((_, _) =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload)
            }));
        var downloader = new AudioFileDownloader(client);
        var progress = new CancelAfterFirstProgress(cancellation);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => downloader.DownloadAsync(
            new Uri("https://provider.example/audio"),
            destination,
            progress,
            cancellation.Token));

        Assert.Equal("existing", await File.ReadAllTextAsync(destination));
        Assert.True(File.Exists(partial));
        var savedPartial = await File.ReadAllBytesAsync(partial);
        Assert.NotEmpty(savedPartial);
        Assert.True(savedPartial.Length < payload.Length);
        Assert.Equal(payload[..savedPartial.Length], savedPartial);
    }

    [Fact]
    public async Task DownloadAsync_RejectsTruncatedResponseAndKeepsPartialForRetry()
    {
        using var directory = new TemporaryDirectory();
        var destination = Path.Combine(directory.Path, "book.m4b");
        var partial = destination + ".part";
        var payload = new byte[] { 1, 2, 3 };
        using var client = new HttpClient(new StubHandler((_, _) =>
        {
            var content = new ByteArrayContent(payload);
            content.Headers.ContentLength = 5;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }));
        var downloader = new AudioFileDownloader(client);

        await Assert.ThrowsAsync<InvalidDataException>(() => downloader.DownloadAsync(
            new Uri("https://provider.example/audio"), destination));

        Assert.False(File.Exists(destination));
        Assert.Equal(payload, await File.ReadAllBytesAsync(partial));
    }

    [Fact]
    public async Task DownloadAsync_RejectsResumeResponseStartingAtWrongOffset()
    {
        using var directory = new TemporaryDirectory();
        var destination = Path.Combine(directory.Path, "book.m4b");
        var partial = destination + ".part";
        Directory.CreateDirectory(directory.Path);
        await File.WriteAllBytesAsync(partial, new byte[] { 1, 2 });
        using var client = new HttpClient(new StubHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(new byte[] { 3, 4, 5 })
            };
            response.Content.Headers.ContentRange =
                new System.Net.Http.Headers.ContentRangeHeaderValue(1, 3, 5);
            return response;
        }));
        var downloader = new AudioFileDownloader(client);

        await Assert.ThrowsAsync<HttpRequestException>(() => downloader.DownloadAsync(
            new Uri("https://provider.example/audio"), destination));

        Assert.Equal(new byte[] { 1, 2 }, await File.ReadAllBytesAsync(partial));
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public async Task DownloadAsync_RejectsNonHttpsUrl()
    {
        using var client = new HttpClient(new StubHandler((_, _) =>
            new HttpResponseMessage(HttpStatusCode.OK)));
        var downloader = new AudioFileDownloader(client);
        using var directory = new TemporaryDirectory();

        await Assert.ThrowsAsync<ArgumentException>(() => downloader.DownloadAsync(
            new Uri("http://provider.example/audio"),
            Path.Combine(directory.Path, "book.m4b")));
    }

    [Fact]
    public async Task DownloadAsync_RejectsRedirectToNonHttpsAndPreservesDestination()
    {
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(directory.Path);
        var destination = Path.Combine(directory.Path, "book.m4b");
        await File.WriteAllTextAsync(destination, "existing");
        using var client = new HttpClient(new StubHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[] { 1, 2, 3 }),
                RequestMessage = new HttpRequestMessage(
                    HttpMethod.Get, new Uri("http://provider.example/insecure-audio"))
            };
            return response;
        }));
        var downloader = new AudioFileDownloader(client);

        await Assert.ThrowsAsync<HttpRequestException>(() => downloader.DownloadAsync(
            new Uri("https://provider.example/audio"), destination));

        Assert.Equal("existing", await File.ReadAllTextAsync(destination));
        Assert.Empty(Directory.GetFiles(directory.Path, "*.part"));
    }

    [Fact]
    public async Task DownloadAsync_StopsWhenStreamExceedsConfiguredSizeLimit()
    {
        using var directory = new TemporaryDirectory();
        var destination = Path.Combine(directory.Path, "book.m4b");
        Directory.CreateDirectory(directory.Path);
        await File.WriteAllTextAsync(destination, "existing");
        using var client = new HttpClient(new StubHandler((_, _) =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[] { 1, 2, 3, 4, 5 })
            }));
        var downloader = new AudioFileDownloader(client, maximumBytes: 4);

        await Assert.ThrowsAsync<InvalidDataException>(() => downloader.DownloadAsync(
            new Uri("https://provider.example/audio"), destination));

        Assert.Equal("existing", await File.ReadAllTextAsync(destination));
        Assert.Empty(Directory.GetFiles(directory.Path, "*.part"));
    }

    [Fact]
    public async Task DownloadAsync_DoesNotReplaceDestinationWhenRequestFails()
    {
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(directory.Path);
        var destination = Path.Combine(directory.Path, "book.m4b");
        await File.WriteAllTextAsync(destination, "existing");
        using var client = new HttpClient(new StubHandler((_, _) =>
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        var downloader = new AudioFileDownloader(client);

        await Assert.ThrowsAsync<HttpRequestException>(() => downloader.DownloadAsync(
            new Uri("https://provider.example/audio"), destination));

        Assert.Equal("existing", await File.ReadAllTextAsync(destination));
        Assert.Empty(Directory.GetFiles(directory.Path, "*.part"));
    }

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> response)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(response(request, cancellationToken));
    }

    private sealed class RecordingProgress<T> : IProgress<T>
    {
        public List<T> Values { get; } = [];
        public void Report(T value) => Values.Add(value);
    }

    private sealed class CancelAfterFirstProgress(CancellationTokenSource cancellation)
        : IProgress<AudioDownloadProgress>
    {
        private bool _cancelled;

        public void Report(AudioDownloadProgress value)
        {
            if (_cancelled) return;
            _cancelled = true;
            cancellation.Cancel();
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "LibationMobileDownloadTests", Guid.NewGuid().ToString("N"));
        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
