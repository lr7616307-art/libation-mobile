using System.Net;
using System.Net.Http.Headers;
using Libation.Mobile.Core;
using Xunit;

namespace Libation.Mobile.Core.Tests;

public sealed class CoverArtDownloadServiceTests
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];

    [Theory]
    [InlineData("https://m.media-amazon.com/images/cover.jpg", true)]
    [InlineData("http://m.media-amazon.com/cover.jpg", false)]
    [InlineData("https://example.com/cover.jpg", false)]
    [InlineData("https://m.media-amazon.com.example.com/cover.jpg", false)]
    [InlineData("https://user@m.media-amazon.com/cover.jpg", false)]
    public void IsTrustedUrl_OnlyAllowsAudibleHttpsHost(string url, bool expected)
        => Assert.Equal(expected, CoverArtDownloadService.IsTrustedUrl(url));

    [Fact]
    public async Task Download_CachesValidImageAndReusesIt()
    {
        using var directory = new TemporaryDirectory();
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(Png) { Headers = { ContentType = new MediaTypeHeaderValue("image/png") } }
        });
        using var client = new HttpClient(handler);
        var service = new CoverArtDownloadService(client, directory.Path);

        var path = await service.DownloadAsync("provider:one", "https://m.media-amazon.com/cover.jpg");
        var cached = await service.DownloadAsync("provider:one", "https://m.media-amazon.com/cover.jpg");

        Assert.Equal(path, cached);
        Assert.EndsWith(".png", path);
        Assert.Equal(Png, await File.ReadAllBytesAsync(path!));
        Assert.Equal(1, handler.RequestCount);
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
    }

    [Fact]
    public async Task Download_RejectsOversizedPayloadAndCleansTemporaryFile()
    {
        using var directory = new TemporaryDirectory();
        using var client = new HttpClient(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(Png) { Headers = { ContentType = new MediaTypeHeaderValue("image/png") } }
        }));
        var service = new CoverArtDownloadService(client, directory.Path, maximumBytes: 8);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.DownloadAsync("book", "https://m.media-amazon.com/cover"));
        Assert.Empty(Directory.GetFiles(directory.Path));
    }

    [Fact]
    public async Task Download_SkipsUntrustedUrlWithoutNetworkRequest()
    {
        using var directory = new TemporaryDirectory();
        var handler = new FakeHandler(_ => throw new InvalidOperationException("Unexpected request."));
        using var client = new HttpClient(handler);
        var service = new CoverArtDownloadService(client, directory.Path);

        Assert.Null(await service.DownloadAsync("book", "https://example.com/cover.png"));
        Assert.Equal(0, handler.RequestCount);
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            var result = response(request);
            result.RequestMessage = request;
            return Task.FromResult(result);
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(Path.GetTempPath(), "VorathCoverCacheTests", Guid.NewGuid().ToString("N"));
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, true); }
    }
}
