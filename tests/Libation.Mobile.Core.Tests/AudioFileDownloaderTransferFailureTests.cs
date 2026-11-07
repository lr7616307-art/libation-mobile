using System.Net;
using Libation.Mobile.Core;
using Xunit;

namespace Libation.Mobile.Core.Tests;

public sealed class AudioFileDownloaderTransferFailureTests
{
    [Fact]
    public async Task DownloadAsync_DurablyRetainsBytesWhenResponseStreamFails()
    {
        using var directory = new TemporaryDirectory();
        var destination = Path.Combine(directory.Path, "book.m4b");
        using var client = new HttpClient(new SingleResponseHandler(() =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new FailingAfterPrefixContent([1, 2, 3])
            }));
        var downloader = new AudioFileDownloader(client);

        await Assert.ThrowsAsync<IOException>(() => downloader.DownloadAsync(
            new Uri("https://provider.example/audio"), destination));

        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(destination + ".part"));
        Assert.False(File.Exists(destination));
    }

    private sealed class SingleResponseHandler(Func<HttpResponseMessage> responseFactory)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responseFactory());
    }

    private sealed class FailingAfterPrefixContent(byte[] prefix) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => throw new NotSupportedException();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override Task<Stream> CreateContentReadStreamAsync()
            => Task.FromResult<Stream>(new FailingAfterPrefixStream(prefix));
    }

    private sealed class FailingAfterPrefixStream(byte[] prefix) : Stream
    {
        private bool _sentPrefix;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_sentPrefix)
                throw new IOException("Simulated interrupted response stream.");

            prefix.CopyTo(buffer);
            _sentPrefix = true;
            return ValueTask.FromResult(prefix.Length);
        }

        public override int Read(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "LibationMobileTransferFailureTests", Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
