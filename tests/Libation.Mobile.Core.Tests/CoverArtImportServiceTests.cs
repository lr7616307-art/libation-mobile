using Libation.Mobile.Core;
using Xunit;

namespace Libation.Mobile.Core.Tests;

public sealed class CoverArtImportServiceTests
{
    [Theory]
    [InlineData("cover.jpg", new byte[] { 0xFF, 0xD8, 0xFF, 0x01 })]
    [InlineData("cover.png", new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })]
    [InlineData("cover.webp", new byte[] { 0x52, 0x49, 0x46, 0x46, 0, 0, 0, 0, 0x57, 0x45, 0x42, 0x50 })]
    public async Task Import_CopiesSupportedImageToPrivatePath(string name, byte[] payload)
    {
        using var directory = new TemporaryDirectory();
        var service = new CoverArtImportService(directory.Path);

        var path = await service.ImportAsync("provider/book:1", name, new MemoryStream(payload));

        Assert.StartsWith(directory.Path, path, StringComparison.Ordinal);
        Assert.Equal(payload, await File.ReadAllBytesAsync(path));
        Assert.DoesNotContain(name, path, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
    }

    [Fact]
    public async Task Import_RejectsUnsupportedExtension()
    {
        using var directory = new TemporaryDirectory();
        var service = new CoverArtImportService(directory.Path);

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            service.ImportAsync("book-1", "cover.gif", new MemoryStream([1, 2, 3])));

        Assert.False(Directory.Exists(directory.Path));
    }

    [Fact]
    public async Task Import_RejectsImageWithMismatchedSignatureAndCleansTemporaryFile()
    {
        using var directory = new TemporaryDirectory();
        var service = new CoverArtImportService(directory.Path);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.ImportAsync("book-1", "not-really.jpg", new MemoryStream([1, 2, 3, 4])));

        Assert.Empty(Directory.GetFiles(directory.Path));
    }

    [Fact]
    public async Task Import_EnforcesMaximumSize()
    {
        using var directory = new TemporaryDirectory();
        var service = new CoverArtImportService(directory.Path, maximumFileBytes: 8);
        var pngHeader = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1 };

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.ImportAsync("book-1", "cover.png", new MemoryStream(pngHeader)));

        Assert.Empty(Directory.GetFiles(directory.Path));
    }

    [Fact]
    public async Task Import_RejectsEmptyFile()
    {
        using var directory = new TemporaryDirectory();
        var service = new CoverArtImportService(directory.Path);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.ImportAsync("book-1", "cover.png", new MemoryStream()));

        Assert.Empty(Directory.GetFiles(directory.Path));
    }

    [Theory]
    [InlineData("cover.jpg")]
    [InlineData("cover.jpeg")]
    [InlineData("cover.png")]
    [InlineData("cover.webp")]
    public void IsSupportedFileName_AcceptsImageFormats(string name)
        => Assert.True(CoverArtImportService.IsSupportedFileName(name));

    [Theory]
    [InlineData("cover.gif")]
    [InlineData("cover.svg")]
    [InlineData("cover.png.exe")]
    [InlineData("")]
    public void IsSupportedFileName_RejectsUnsupportedFormats(string name)
        => Assert.False(CoverArtImportService.IsSupportedFileName(name));

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "LibationMobileCoverTests", Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
