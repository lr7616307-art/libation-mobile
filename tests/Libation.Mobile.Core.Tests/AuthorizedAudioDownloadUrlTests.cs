using Libation.Mobile.Core;
using Xunit;

namespace Libation.Mobile.Core.Tests;

public sealed class AuthorizedAudioDownloadUrlTests
{
    [Theory]
    [InlineData("https://cdn.example/books/audio.mp3")]
    [InlineData("https://cdn.example/books/audio.m4b")]
    [InlineData("https://cdn.example/books/audio.flac")]
    public void Parse_AcceptsDirectHttpsAudioUrls(string value)
    {
        var uri = AuthorizedAudioDownloadUrl.Parse(value);
        Assert.Equal(value, uri.AbsoluteUri);
    }

    [Theory]
    [InlineData("http://cdn.example/audio.m4b")]
    [InlineData("file:///storage/emulated/0/audio.m4b")]
    [InlineData("https://user:password@cdn.example/audio.m4b")]
    [InlineData("https://cdn.example/audio.m4b?token=private")]
    [InlineData("https://cdn.example/audio.m4b#fragment")]
    [InlineData("https://cdn.example/audio.pdf")]
    [InlineData("not a URL")]
    public void Parse_RejectsUnsupportedOrSensitiveUrls(string value)
    {
        Assert.Throws<ArgumentException>(() => AuthorizedAudioDownloadUrl.Parse(value));
    }

    [Fact]
    public async Task QueuedSource_UsesQueueUrlAndKeepsPlayableExtensionOnStagingPath()
    {
        using var directory = new TemporaryDirectory();
        var source = new QueuedHttpsAudioDownloadSource(directory.Path);
        var item = new DownloadQueueItem(
            "book-1", DownloadStatus.Queued, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            SourceUrl: "https://cdn.example/books/audio.m4b",
            DisplayTitle: "Example book");

        var target = await source.ResolveAsync(item);

        Assert.Equal(new Uri("https://cdn.example/books/audio.m4b"), target.DownloadUri);
        Assert.EndsWith(".m4b", target.DestinationPath, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(Path.GetFullPath(directory.Path), target.DestinationPath, StringComparison.Ordinal);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "LibationMobileAuthorizedUrlTests", Guid.NewGuid().ToString("N"));
        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
