using Libation.Mobile.Core;
using Xunit;

namespace Libation.Mobile.Core.Tests;

public sealed class JsonLibraryRepositoryTests
{
    [Fact]
    public async Task GetBooks_WhenFileDoesNotExist_ReturnsEmptyList()
    {
        using var directory = new TemporaryDirectory();
        var repository = new JsonLibraryRepository(Path.Combine(directory.Path, "library.json"));

        Assert.Empty(await repository.GetBooksAsync());
    }

    [Fact]
    public async Task ReplaceAllAndReload_RoundTripsBooks()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "data", "library.json");
        var repository = new JsonLibraryRepository(path);
        Audiobook[] expected =
        [
            new("book-1", "Title One", "Author One", "Narrator One", "4h", "Owned"),
            new("book-2", "Title Two", "Author Two", "Narrator Two", "8h", "Downloaded")
        ];

        await repository.ReplaceAllAsync(expected);
        var actual = await new JsonLibraryRepository(path).GetBooksAsync();

        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task ReplaceAllAndReload_PreservesChapterMarkers()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "library.json");
        var repository = new JsonLibraryRepository(path);
        var expected = new Audiobook("book-1", "Title", "Author", "Narrator", "4h", "Owned")
        {
            Chapters =
            [
                new("Prologue", 0),
                new("The Journey", 125_000),
                new("Afterword", 13_500_000)
            ]
        };

        await repository.UpsertAsync(expected);
        var actual = Assert.Single(await new JsonLibraryRepository(path).GetBooksAsync());

        Assert.Equal(expected.Chapters, actual.Chapters);
    }

    [Fact]
    public async Task GetBooks_WhenStoredCatalogueHasNoChapters_UsesEmptyChapterList()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "library.json");
        Directory.CreateDirectory(directory.Path);
        await File.WriteAllTextAsync(path,
            """[{"Id":"legacy","Title":"Old book","Author":"Author","Narrator":"","Length":"","Status":"Owned"}]""");

        var actual = Assert.Single(await new JsonLibraryRepository(path).GetBooksAsync());

        Assert.NotNull(actual.Chapters);
        Assert.Empty(actual.Chapters);
    }

    [Fact]
    public async Task Upsert_ReplacesMatchingIdWithoutDuplicates()
    {
        using var directory = new TemporaryDirectory();
        var repository = new JsonLibraryRepository(Path.Combine(directory.Path, "library.json"));
        await repository.UpsertAsync(new("book-1", "Old title", "Author", "Narrator", "1h", "Owned"));
        await repository.UpsertAsync(new("book-1", "New title", "Author", "Narrator", "2h", "Owned"));

        var books = await repository.GetBooksAsync();

        var book = Assert.Single(books);
        Assert.Equal("New title", book.Title);
    }

    [Fact]
    public async Task Remove_DeletesExistingBookAndReportsResult()
    {
        using var directory = new TemporaryDirectory();
        var repository = new JsonLibraryRepository(Path.Combine(directory.Path, "library.json"));
        await repository.UpsertAsync(new("book-1", "Title", "Author", "Narrator", "1h", "Owned"));

        Assert.True(await repository.RemoveAsync("book-1"));
        Assert.False(await repository.RemoveAsync("book-1"));
        Assert.Empty(await repository.GetBooksAsync());
    }

    [Fact]
    public async Task ReplaceAll_RejectsDuplicateIds()
    {
        using var directory = new TemporaryDirectory();
        var repository = new JsonLibraryRepository(Path.Combine(directory.Path, "library.json"));
        Audiobook[] books =
        [
            new("same", "One", "A", "N", "1h", "Owned"),
            new("same", "Two", "B", "M", "2h", "Owned")
        ];

        await Assert.ThrowsAsync<ArgumentException>(() => repository.ReplaceAllAsync(books));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "LibationMobileTests", Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
