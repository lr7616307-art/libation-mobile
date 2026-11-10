using Libation.Mobile.Core;
using Xunit;

namespace Libation.Mobile.Core.Tests;

public sealed class AudiobookFileImportServiceTests
{
    [Fact]
    public async Task ImportAsync_UsesFilenameFallbackAndKeepsAudioWhenMetadataIsMissing()
    {
        using var directory = new TemporaryDirectory();
        var audioDirectory = Path.Combine(directory.Path, "audio");
        var coverDirectory = Path.Combine(directory.Path, "covers");
        var service = new AudiobookFileImportService(
            new LocalAudioImportService(audioDirectory),
            coverDirectory);
        var payload = System.Text.Encoding.UTF8.GetBytes("ordinary lecture recording without audiobook tags");

        await using var source = new MemoryStream(payload);
        var result = await service.ImportAsync("lecture-id", "Motivational lecture.mp3", source);

        Assert.Equal("Motivational lecture", result.Title);
        Assert.Equal(string.Empty, result.Album);
        Assert.True(File.Exists(result.AudioFilePath));
        Assert.Equal(payload, await File.ReadAllBytesAsync(result.AudioFilePath));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "VORATHAudioImportTests", Guid.NewGuid().ToString("N"));

        public TemporaryDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, true);
        }
    }
}
