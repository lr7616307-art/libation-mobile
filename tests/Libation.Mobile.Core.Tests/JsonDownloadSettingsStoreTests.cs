using System.Text.Json;
using Libation.Mobile.Core;
using Xunit;

namespace Libation.Mobile.Core.Tests;

public sealed class JsonDownloadSettingsStoreTests
{
    [Fact]
    public async Task SaveAndGet_PreservesFolderBookmarkAndOrganization()
    {
        using var directory = new TemporaryDirectory();
        var store = new JsonDownloadSettingsStore(Path.Combine(directory.Path, "download-settings.json"));
        var settings = new DownloadSettings(
            "bookmark:android-storage-tree",
            "Audiobooks",
            DownloadFolderOrganization.AuthorSeriesBook,
            AudibleDownloadQuality.Normal);

        await store.SaveAsync(settings);
        var loaded = await store.GetAsync();

        Assert.Equal("bookmark:android-storage-tree", loaded.DirectoryBookmark);
        Assert.Equal("Audiobooks", loaded.DirectoryName);
        Assert.Equal(DownloadFolderOrganization.AuthorSeriesBook, loaded.FolderOrganization);
        Assert.Equal(AudibleDownloadQuality.Normal, loaded.AudibleQuality);
        Assert.Equal("Downloads/Author/Series/Book Title/Book Title.m4b", loaded.FolderOrganizationExample);
    }

    [Fact]
    public async Task GetAsync_MissingFileReturnsSafeDefaults()
    {
        using var directory = new TemporaryDirectory();
        var store = new JsonDownloadSettingsStore(Path.Combine(directory.Path, "missing.json"));

        var loaded = await store.GetAsync();

        Assert.False(loaded.HasSelectedDirectory);
        Assert.Equal(DownloadFolderOrganization.AuthorBook, loaded.FolderOrganization);
        Assert.Equal(AudibleDownloadQuality.High, loaded.AudibleQuality);
        Assert.Equal("Author / Book", loaded.FolderOrganizationLabel);
    }

    [Fact]
    public async Task SaveAsync_NormalizesBlankFolderAndInvalidOrganization()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "download-settings.json");
        var store = new JsonDownloadSettingsStore(path);

        await store.SaveAsync(new DownloadSettings("  ", "  ", (DownloadFolderOrganization)999, (AudibleDownloadQuality)999));
        var loaded = await store.GetAsync();

        Assert.Null(loaded.DirectoryBookmark);
        Assert.Null(loaded.DirectoryName);
        Assert.False(loaded.HasSelectedDirectory);
        Assert.Equal(DownloadFolderOrganization.AuthorBook, loaded.FolderOrganization);
        Assert.Equal(AudibleDownloadQuality.High, loaded.AudibleQuality);
    }

    [Fact]
    public async Task GetAsync_DamagedJsonReturnsSafeDefaults()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "download-settings.json");
        Directory.CreateDirectory(directory.Path);
        await File.WriteAllTextAsync(path, "{ invalid json");
        var store = new JsonDownloadSettingsStore(path);

        var loaded = await store.GetAsync();

        Assert.False(loaded.HasSelectedDirectory);
        Assert.Equal(DownloadFolderOrganization.AuthorBook, loaded.FolderOrganization);
    }

    [Fact]
    public async Task SaveAsync_UsesAtomicReplacementAndLeavesNoTemporaryFiles()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "download-settings.json");
        var store = new JsonDownloadSettingsStore(path);

        await store.SaveAsync(new DownloadSettings("bookmark:first", "First"));
        await store.SaveAsync(new DownloadSettings("bookmark:second", "Second", DownloadFolderOrganization.Flat));

        Assert.Equal("bookmark:second", (await store.GetAsync()).DirectoryBookmark);
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "VorathDownloadSettingsTests", Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
