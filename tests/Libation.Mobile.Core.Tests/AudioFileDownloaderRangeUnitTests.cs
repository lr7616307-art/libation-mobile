using System.Net;
using System.Net.Http.Headers;
using Libation.Mobile.Core;
using Xunit;

namespace Libation.Mobile.Core.Tests;

public sealed class AudioFileDownloaderRangeUnitTests
{
    [Fact]
    public async Task DownloadAsync_RejectsPartialResponseWithNonByteRangeUnit()
    {
        using var directory = new TemporaryDirectory();
        var destination = Path.Combine(directory.Path, "book.m4b");
        var partial = destination + ".part";
        Directory.CreateDirectory(directory.Path);
        await File.WriteAllBytesAsync(partial, [1, 2]);
        await File.WriteAllTextAsync(partial + ".validator", "etag:\"v1\"");

        using var client = new HttpClient(new StubHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent([3, 4, 5])
            };
            Assert.True(ContentRangeHeaderValue.TryParse("items 2-4/5", out var range));
            response.Content.Headers.ContentRange = range;
            response.Headers.ETag = new EntityTagHeaderValue("\"v1\"");
            return response;
        }));

        var downloader = new AudioFileDownloader(client);

        await Assert.ThrowsAsync<HttpRequestException>(() => downloader.DownloadAsync(
            new Uri("https://provider.example/audio"), destination));

        Assert.False(File.Exists(partial));
        Assert.False(File.Exists(partial + ".validator"));
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public async Task DownloadAsync_DoesNotPromotePartialFromNonByteUnsatisfiedRange()
    {
        using var directory = new TemporaryDirectory();
        var destination = Path.Combine(directory.Path, "book.m4b");
        var partial = destination + ".part";
        var payload = new byte[] { 1, 2, 3, 4, 5 };
        Directory.CreateDirectory(directory.Path);
        await File.WriteAllBytesAsync(partial, payload);
        await File.WriteAllTextAsync(partial + ".validator", "etag:\"v1\"");

        using var client = new HttpClient(new StubHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable);
            Assert.True(ContentRangeHeaderValue.TryParse($"items */{payload.Length}", out var range));
            response.Content.Headers.ContentRange = range;
            response.Headers.ETag = new EntityTagHeaderValue("\"v1\"");
            return response;
        }));

        var downloader = new AudioFileDownloader(client);

        await Assert.ThrowsAsync<HttpRequestException>(() => downloader.DownloadAsync(
            new Uri("https://provider.example/audio"), destination));

        Assert.False(File.Exists(partial));
        Assert.False(File.Exists(partial + ".validator"));
        Assert.False(File.Exists(destination));
    }


    [Fact]
    public async Task DownloadAsync_DiscardsPartialWhenResumeValidatorChanges()
    {
        using var directory = new TemporaryDirectory();
        var destination = Path.Combine(directory.Path, "book.m4b");
        var partial = destination + ".part";
        Directory.CreateDirectory(directory.Path);
        await File.WriteAllBytesAsync(partial, [1, 2]);
        await File.WriteAllTextAsync(partial + ".validator", "etag:\"v1\"");

        using var client = new HttpClient(new StubHandler((request, _) =>
        {
            Assert.Equal(2, request.Headers.Range?.Ranges.Single().From);
            Assert.Equal("\"v1\"", request.Headers.IfRange?.EntityTag?.Tag);
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent([9, 9, 9])
            };
            Assert.True(ContentRangeHeaderValue.TryParse("bytes 2-4/5", out var range));
            response.Content.Headers.ContentRange = range;
            response.Headers.ETag = new EntityTagHeaderValue("\"v2\"");
            return response;
        }));

        var downloader = new AudioFileDownloader(client);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => downloader.DownloadAsync(
            new Uri("https://provider.example/audio"), destination));

        Assert.Contains("stale partial download was discarded", exception.Message);
        Assert.False(File.Exists(partial));
        Assert.False(File.Exists(partial + ".validator"));
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public async Task DownloadAsync_DiscardsPartialWhen416DoesNotMatchSavedValidator()
    {
        using var directory = new TemporaryDirectory();
        var destination = Path.Combine(directory.Path, "book.m4b");
        var partial = destination + ".part";
        Directory.CreateDirectory(directory.Path);
        await File.WriteAllBytesAsync(partial, [1, 2, 3]);
        await File.WriteAllTextAsync(partial + ".validator", "etag:\"v1\"");

        using var client = new HttpClient(new StubHandler((request, _) =>
        {
            Assert.Equal(3, request.Headers.Range?.Ranges.Single().From);
            var response = new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable);
            Assert.True(ContentRangeHeaderValue.TryParse("bytes */2", out var range));
            response.Content.Headers.ContentRange = range;
            response.Headers.ETag = new EntityTagHeaderValue("\"v2\"");
            return response;
        }));

        var downloader = new AudioFileDownloader(client);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => downloader.DownloadAsync(
            new Uri("https://provider.example/audio"), destination));

        Assert.Contains("stale partial download was discarded", exception.Message);
        Assert.False(File.Exists(partial));
        Assert.False(File.Exists(partial + ".validator"));
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public async Task DownloadAsync_DiscardsResumeStateWhenDeclaredTotalExceedsSizeLimit()
    {
        using var directory = new TemporaryDirectory();
        var destination = Path.Combine(directory.Path, "book.m4b");
        var partial = destination + ".part";
        Directory.CreateDirectory(directory.Path);
        await File.WriteAllBytesAsync(partial, [1, 2]);
        await File.WriteAllTextAsync(partial + ".validator", "etag:\"v1\"");

        using var client = new HttpClient(new StubHandler((request, _) =>
        {
            Assert.Equal(2, request.Headers.Range?.Ranges.Single().From);
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent([3, 4, 5])
            };
            Assert.True(ContentRangeHeaderValue.TryParse("bytes 2-4/10", out var range));
            response.Content.Headers.ContentRange = range;
            response.Headers.ETag = new EntityTagHeaderValue("\"v1\"");
            return response;
        }));

        var downloader = new AudioFileDownloader(client, maximumBytes: 5);

        await Assert.ThrowsAsync<InvalidDataException>(() => downloader.DownloadAsync(
            new Uri("https://provider.example/audio"), destination));

        Assert.False(File.Exists(partial));
        Assert.False(File.Exists(partial + ".validator"));
        Assert.False(File.Exists(destination));
    }

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> response)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(response(request, cancellationToken));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "LibationMobileRangeTests", Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
