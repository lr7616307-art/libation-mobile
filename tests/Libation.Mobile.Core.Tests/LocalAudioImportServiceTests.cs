using Libation.Mobile.Core;
using Xunit;

namespace Libation.Mobile.Core.Tests;

public sealed class LocalAudioImportServiceTests
{
    [Fact]
    public async Task Import_CopiesAudioToAppManagedPath_WithoutUsingUserFileName()
    {
        using var directory = new TemporaryDirectory();
        var service = new LocalAudioImportService(directory.Path);
        var payload = new byte[] { 1, 2, 3, 4, 5 };

        var path = await service.ImportAsync("../provider:123", "my book.M4B", new MemoryStream(payload));

        Assert.StartsWith(directory.Path, path, StringComparison.Ordinal);
        Assert.EndsWith(".m4b", path, StringComparison.Ordinal);
        Assert.Equal(payload, await File.ReadAllBytesAsync(path));
        Assert.DoesNotContain("my book", path, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
    }

    [Fact]
    public async Task Import_RejectsUnsupportedExtensionBeforeCreatingFile()
    {
        using var directory = new TemporaryDirectory();
        var service = new LocalAudioImportService(directory.Path);

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            service.ImportAsync("book-1", "protected.aax", new MemoryStream([1, 2, 3])));

        Assert.False(Directory.Exists(directory.Path));
    }

    [Fact]
    public async Task Import_RejectsEmptyFile()
    {
        using var directory = new TemporaryDirectory();
        var service = new LocalAudioImportService(directory.Path);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.ImportAsync("book-1", "empty.mp3", new MemoryStream()));

        Assert.Empty(Directory.GetFiles(directory.Path));
    }

    [Fact]
    public async Task Import_EnforcesMaximumSizeAndCleansTemporaryFile()
    {
        using var directory = new TemporaryDirectory();
        var service = new LocalAudioImportService(directory.Path, maximumFileBytes: 4);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.ImportAsync("book-1", "large.mp3", new MemoryStream([1, 2, 3, 4, 5])));

        Assert.Empty(Directory.GetFiles(directory.Path));
    }

    [Fact]
    public async Task Import_CancellationDoesNotLeavePartialFile()
    {
        using var directory = new TemporaryDirectory();
        var service = new LocalAudioImportService(directory.Path);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.ImportAsync("book-1", "book.mp3", new MemoryStream([1, 2, 3]), cancellation.Token));

        Assert.Empty(Directory.GetFiles(directory.Path));
    }

    [Theory]
    [InlineData("book.mp3")]
    [InlineData("book.m4a")]
    [InlineData("book.m4b")]
    [InlineData("book.aac")]
    [InlineData("book.flac")]
    [InlineData("book.ogg")]
    [InlineData("book.opus")]
    [InlineData("book.wav")]
    public void IsSupportedFileName_AcceptsCommonAudioExtensions(string name)
        => Assert.True(LocalAudioImportService.IsSupportedFileName(name));

    [Theory]
    [InlineData("book.aax")]
    [InlineData("book.pdf")]
    [InlineData("book.mp3.exe")]
    [InlineData("")]
    public void IsSupportedFileName_RejectsUnsupportedExtensions(string name)
        => Assert.False(LocalAudioImportService.IsSupportedFileName(name));

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "LibationMobileAudioImportTests", Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
