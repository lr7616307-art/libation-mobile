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
    public async Task DownloadAsync_MidstreamReadFailurePreservesDestinationAndCleansPartialFile()
    {
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(directory.Path);
        var destination = Path.Combine(directory.Path, "book.m4b");
        await File.WriteAllTextAsync(destination, "existing");
        using var client = new HttpClient(new StubHandler((_, _) =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new FaultingReadStream([1, 2, 3, 4, 5]))
            }));
        var downloader = new AudioFileDownloader(client);

        await Assert.ThrowsAsync<IOException>(() => downloader.DownloadAsync(
            new Uri("https://provider.example/audio"), destination));

        Assert.Equal("existing", await File.ReadAllTextAsync(destination));
        Assert.Equal(new byte[] { 1, 2, 3 },
            await File.ReadAllBytesAsync(destination + ".part"));
    }

    [Fact]
    public async Task DownloadAsync_ResumesExistingPartWithRangeAndRequestHeaders()
    {
        using var directory = new TemporaryDirectory();
        var destination = Path.Combine(directory.Path, "book.aaxc");
        await File.WriteAllBytesAsync(destination + ".part", [1, 2]);
        var handler = new StubHandler((request, _) =>
        {
            Assert.Equal(2L, request.Headers.Range?.Ranges.Single().From);
            Assert.Contains(request.Headers.GetValues("User-Agent"),
                value => value.Contains("Audible/671", StringComparison.Ordinal));
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent([3, 4])
            };
            response.Content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(2, 3, 4);
            return response;
        });
        using var client = new HttpClient(handler);
        var downloader = new AudioFileDownloader(client);

        var result = await downloader.DownloadAsync(
            new Uri("https://cdn.example/book"),
            destination,
            cancellationToken: CancellationToken.None,
            requestHeaders: new Dictionary<string, string>
            {
                ["User-Agent"] = "Audible/671 CFNetwork/1240.0.4 Darwin/20.6.0"
            });

        Assert.Equal(4, result.BytesReceived);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, await File.ReadAllBytesAsync(destination));
        Assert.False(File.Exists(destination + ".part"));
    }

    [Fact]
    public async Task DownloadAsync_RestartsWhenServerIgnoresRange()
    {
        using var directory = new TemporaryDirectory();
        var destination = Path.Combine(directory.Path, "book.aax");
        await File.WriteAllBytesAsync(destination + ".part", [1, 2]);
        using var client = new HttpClient(new StubHandler((request, _) =>
        {
            Assert.Equal(2L, request.Headers.Range?.Ranges.Single().From);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([7, 8, 9])
            };
        }));
        var downloader = new AudioFileDownloader(client);

        var result = await downloader.DownloadAsync(
            new Uri("https://cdn.example/book"), destination);

        Assert.Equal(3, result.BytesReceived);
        Assert.Equal(new byte[] { 7, 8, 9 }, await File.ReadAllBytesAsync(destination));
        Assert.False(File.Exists(destination + ".part"));
    }

    [Fact]
    public async Task DownloadAsync_PromotesCompletePartialFileAfterRangeNotSatisfiable()
    {
        using var directory = new TemporaryDirectory();
        var destination = Path.Combine(directory.Path, "book.aaxc");
        await File.WriteAllBytesAsync(destination + ".part", [5, 4, 3]);
        using var client = new HttpClient(new StubHandler((request, _) =>
        {
            Assert.Equal(3L, request.Headers.Range?.Ranges.Single().From);
            var response = new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable);
            response.Content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(3);
            return response;
        }));
        var downloader = new AudioFileDownloader(client);

        var result = await downloader.DownloadAsync(
            new Uri("https://cdn.example/book"), destination);

        Assert.Equal(3, result.BytesReceived);
        Assert.Equal(new byte[] { 5, 4, 3 }, await File.ReadAllBytesAsync(destination));
        Assert.False(File.Exists(destination + ".part"));
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

    private sealed class FaultingReadStream(byte[] payload) : MemoryStream(payload)
    {
        private bool _returnedData;

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_returnedData) throw new IOException("Simulated connection interruption.");
            _returnedData = true;
            return base.ReadAsync(buffer[..Math.Min(3, buffer.Length)], cancellationToken);
        }
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
