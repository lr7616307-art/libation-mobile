using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Libation.Mobile.Core;
using Xunit;

namespace Libation.Mobile.Core.Tests;

public sealed class ResumableAudioFileDownloaderTests
{
    [Fact]
    public async Task DownloadAsync_ResumesFromSavedRangeAndAppendsOnlyRemainingBytes()
    {
        using var directory = new TemporaryDirectory();
        var partialPath = Path.Combine(directory.Path, "book.aaxc.part");
        await File.WriteAllTextAsync(partialPath, "abcde");
        await File.WriteAllTextAsync(
            partialPath + ".resume.json",
            """{"fingerprint":"voucher-fingerprint","eTag":"\"audible-v1\"","totalLength":10}""");

        var handler = new RecordingHandler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal(5, request.Headers.Range?.Ranges.Single().From);
            Assert.Null(request.Headers.Range?.Ranges.Single().To);
            Assert.Equal("\"audible-v1\"", request.Headers.IfRange?.EntityTag?.Tag);

            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes("fghij"))
            };
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(5, 9, 10);
            response.Headers.ETag = new EntityTagHeaderValue("\"audible-v1\"");
            return response;
        });

        using var client = new HttpClient(handler);
        var downloader = new ResumableAudioFileDownloader(client);
        var result = await downloader.DownloadAsync(
            new Uri("https://cds.audible.com/content/book"),
            partialPath,
            "voucher-fingerprint");

        Assert.Equal(10, result.BytesReceived);
        Assert.Equal(10, result.TotalBytes);
        Assert.Equal("abcdefghij", await File.ReadAllTextAsync(partialPath));
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task DownloadAsync_RestartsWhenVoucherFingerprintChangesInsteadOfMixingCiphertext()
    {
        using var directory = new TemporaryDirectory();
        var partialPath = Path.Combine(directory.Path, "book.aaxc.part");
        await File.WriteAllTextAsync(partialPath, "old-encrypted-prefix");
        await File.WriteAllTextAsync(
            partialPath + ".resume.json",
            """{"fingerprint":"old-voucher","eTag":"\"old\"","totalLength":64}""");

        var handler = new RecordingHandler(request =>
        {
            Assert.Null(request.Headers.Range);
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes("fresh-complete-file"))
            };
            response.Headers.ETag = new EntityTagHeaderValue("\"new\"");
            return response;
        });

        using var client = new HttpClient(handler);
        var result = await new ResumableAudioFileDownloader(client).DownloadAsync(
            new Uri("https://cds.audible.com/content/book"),
            partialPath,
            "new-voucher");

        Assert.Equal("fresh-complete-file", await File.ReadAllTextAsync(partialPath));
        Assert.Equal(19, result.BytesReceived);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task DownloadAsync_KeepsPartialFileAndResumeMetadataWhenCancelled()
    {
        using var directory = new TemporaryDirectory();
        var partialPath = Path.Combine(directory.Path, "book.aaxc.part");
        var handler = new RecordingHandler(async (request, cancellationToken) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new SlowHttpContent(cancellationToken)
            };
            return response;
        });

        using var client = new HttpClient(handler);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new ResumableAudioFileDownloader(client).DownloadAsync(
                new Uri("https://cds.audible.com/content/book"),
                partialPath,
                "voucher-fingerprint",
                cancellationToken: cts.Token));

        Assert.True(File.Exists(partialPath));
        Assert.True(File.Exists(partialPath + ".resume.json"));
    }

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        public RecordingHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> asyncFactory)
            : this(request => throw new InvalidOperationException("Async factory was not used." ))
        {
            AsyncFactory = asyncFactory;
        }

        private Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? AsyncFactory { get; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            if (AsyncFactory is not null)
                return AsyncFactory(request, cancellationToken);
            return Task.FromResult(responseFactory(request));
        }
    }

    private sealed class SlowHttpContent(CancellationToken cancellationToken) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            await stream.WriteAsync(Encoding.UTF8.GetBytes("first chunk"), cancellationToken);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        protected override async Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context,
            CancellationToken cancellationToken)
        {
            await stream.WriteAsync(Encoding.UTF8.GetBytes("first chunk"), cancellationToken);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = -1;
            return false;
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "vorath-resume-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
