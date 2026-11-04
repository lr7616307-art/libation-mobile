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
