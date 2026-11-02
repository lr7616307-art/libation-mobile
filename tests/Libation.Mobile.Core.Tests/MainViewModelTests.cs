using Libation.Mobile.Core;
using Libation.Mobile.UI.ViewModels;
using Xunit;

namespace Libation.Mobile.Core.Tests;

public sealed class MainViewModelTests
{
    [Fact]
    public async Task SyncLibraryAsync_WithoutProvider_ReportsNotConnected()
    {
        using var directory = new TemporaryDirectory();
        var vm = CreateViewModel(directory.Path);
        await vm.InitializeAsync();
        await vm.SyncLibraryAsync();
        Assert.Contains("Provider not connected", vm.SyncStatus);
        Assert.False(vm.CanSync);
        Assert.False(vm.IsSyncing);
    }

    [Fact]
    public async Task SyncLibraryAsync_WithProvider_UpdatesCatalogueAndTimestamp()
    {
        using var directory = new TemporaryDirectory();
        var repository = new JsonLibraryRepository(Path.Combine(directory.Path, "library.json"));
        var provider = new FakeCatalogProvider([new RemoteAudiobook("remote-1", "Remote title", "Remote author", "Narrator", "5h")]);
        var vm = new MainViewModel(repository, new JsonDownloadQueue(Path.Combine(directory.Path, "queue.json")), new LibrarySyncService(provider, repository));
        await vm.InitializeAsync();
        await vm.SyncLibraryAsync();
        Assert.Contains(vm.Books, x => x.Id == "provider:remote-1");
        Assert.NotNull(vm.LastSuccessfulSync);
        Assert.Contains("1 audiobook metadata entry updated", vm.SyncStatus);
        Assert.False(vm.IsSyncing);
    }

    [Fact]
    public async Task SyncLibraryAsync_WhenProviderFails_ReportsSafeFailure()
    {
        using var directory = new TemporaryDirectory();
        var repository = new JsonLibraryRepository(Path.Combine(directory.Path, "library.json"));
        var vm = new MainViewModel(repository, new JsonDownloadQueue(Path.Combine(directory.Path, "queue.json")), new LibrarySyncService(new FakeCatalogProvider(error: new IOException("secret detail")), repository));
        await vm.InitializeAsync();
        var before = vm.Books.Select(x => x.Id).ToArray();
        await vm.SyncLibraryAsync();
        Assert.Equal(before, vm.Books.Select(x => x.Id).ToArray());
        Assert.Contains("Sync failed", vm.SyncStatus);
        Assert.DoesNotContain("secret detail", vm.SyncStatus);
        Assert.Null(vm.LastSuccessfulSync);
    }

    [Fact]
    public async Task AddBookAsync_ValidEntryPersistsAndClearsEditor()
    {
        using var directory = new TemporaryDirectory();
        var vm = CreateViewModel(directory.Path);
        await vm.InitializeAsync();
        vm.NewTitle = "New audiobook";
        vm.NewAuthor = "New author";
        vm.NewNarrator = "New narrator";
        vm.NewLength = "6h";

        await vm.AddBookAsync();

        var added = Assert.Single((await new JsonLibraryRepository(Path.Combine(directory.Path, "library.json")).GetBooksAsync())
            .Where(x => x.Title == "New audiobook"));
        Assert.Equal("New author", added.Author);
        Assert.Equal("New narrator", added.Narrator);
        Assert.Equal("6h", added.Length);
        Assert.Empty(vm.NewTitle);
        Assert.Empty(vm.NewAuthor);
        Assert.False(vm.HasError);
    }

    [Fact]
    public async Task AddBookAsync_RequiresTitleAndAuthor()
    {
        using var directory = new TemporaryDirectory();
        var vm = CreateViewModel(directory.Path);
        await vm.InitializeAsync();
        vm.NewTitle = "Title only";

        await vm.AddBookAsync();

        Assert.Contains("title and author", vm.ErrorMessage);
        Assert.Equal(3, vm.Books.Count);
    }

    [Fact]
    public async Task Edit_UpdatesEntryAndPreservesItsId()
    {
        using var directory = new TemporaryDirectory();
        var vm = CreateViewModel(directory.Path);
        await vm.InitializeAsync();
        vm.NewTitle = "Original";
        vm.NewAuthor = "Author";
        await vm.AddBookAsync();
        var original = Assert.Single(vm.Books.Where(x => x.Title == "Original"));

        vm.BeginEdit(original.Id);
        Assert.True(vm.IsEditing);
        vm.NewTitle = "Revised";
        vm.NewAuthor = "Revised author";
        await vm.AddBookAsync();

        var saved = Assert.Single((await new JsonLibraryRepository(Path.Combine(directory.Path, "library.json")).GetBooksAsync())
            .Where(x => x.Title == "Revised"));
        Assert.Equal(original.Id, saved.Id);
        Assert.Equal("Revised author", saved.Author);
        Assert.False(vm.IsEditing);
    }

    [Fact]
    public async Task Delete_RemovesManualEntryButProtectsDemoEntries()
    {
        using var directory = new TemporaryDirectory();
        var vm = CreateViewModel(directory.Path);
        await vm.InitializeAsync();
        var demo = vm.Books.First(x => x.Status == "Demo");
        await vm.RemoveBookAsync(demo.Id);
        Assert.Contains(vm.Books, x => x.Id == demo.Id);

        vm.NewTitle = "To delete";
        vm.NewAuthor = "Author";
        await vm.AddBookAsync();
        var manual = Assert.Single(vm.Books.Where(x => x.Title == "To delete"));
        await vm.RemoveBookAsync(manual.Id);

        Assert.DoesNotContain(vm.Books, x => x.Id == manual.Id);
    }

    [Fact]
    public async Task Search_FiltersByTitleAuthorAndNarrator()
    {
        using var directory = new TemporaryDirectory();
        var vm = CreateViewModel(directory.Path);
        await vm.InitializeAsync();

        vm.SearchText = "Ray Porter";
        Assert.Contains(vm.Books, x => x.Title == "Project Hail Mary");
        Assert.Single(vm.Books);
        vm.SearchText = "not found";
        Assert.Empty(vm.Books);
    }

    [Fact]
    public async Task BackupImport_RequiresConfirmationBeforeReplacingCatalogue()
    {
        using var directory = new TemporaryDirectory();
        var vm = CreateViewModel(directory.Path);
        await vm.InitializeAsync();
        vm.NewTitle = "Current only";
        vm.NewAuthor = "Current author";
        await vm.AddBookAsync();

        await using var backup = new MemoryStream();
        await LibraryBackupService.ExportAsync(backup,
            [new Audiobook("restored-id", "Restored book", "Restored author", "", "", "Manually added")]);
        backup.Position = 0;
        await vm.PrepareImportAsync(backup);

        Assert.True(vm.HasPendingImport);
        Assert.Contains("1 entries", vm.PendingImportLabel);
        Assert.Contains(vm.Books, x => x.Title == "Current only");

        await vm.ConfirmImportAsync();

        Assert.False(vm.HasPendingImport);
        Assert.Single(vm.Books);
        Assert.Equal("Restored book", vm.Books[0].Title);
    }

    [Fact]
    public async Task BackupImport_CanBeCancelledWithoutChangingCatalogue()
    {
        using var directory = new TemporaryDirectory();
        var vm = CreateViewModel(directory.Path);
        await vm.InitializeAsync();
        var initialCount = vm.Books.Count;

        await using var backup = new MemoryStream();
        await LibraryBackupService.ExportAsync(backup,
            [new Audiobook("other", "Other book", "Author", "", "", "Manually added")]);
        backup.Position = 0;
        await vm.PrepareImportAsync(backup);
        vm.CancelImport();

        Assert.False(vm.HasPendingImport);
        Assert.Equal(initialCount, vm.Books.Count);
    }


    [Fact]
    public async Task CatalogueSort_TogglesAlphabeticalDirection()
    {
        using var directory = new TemporaryDirectory();
        var vm = CreateViewModel(directory.Path);
        await vm.InitializeAsync();
        Assert.Equal("Project Hail Mary", vm.Books[0].Title);
        vm.ToggleSort();
        Assert.Equal("The Three-Body Problem", vm.Books[0].Title);
        Assert.Equal("Sort: Z-A", vm.SortLabel);
    }

    [Fact]
    public async Task CatalogueFilter_CyclesAllManualAndDemo()
    {
        using var directory = new TemporaryDirectory();
        var vm = CreateViewModel(directory.Path);
        await vm.InitializeAsync();
        vm.NewTitle = "Manual entry";
        vm.NewAuthor = "Author";
        await vm.AddBookAsync();
        vm.CycleCatalogueFilter();
        Assert.Equal("Filter: Manual", vm.FilterLabel);
        Assert.Single(vm.Books);
        Assert.Equal("Manual entry", vm.Books[0].Title);
        vm.CycleCatalogueFilter();
        Assert.Equal("Filter: Demo", vm.FilterLabel);
        Assert.Equal(3, vm.Books.Count);
        Assert.All(vm.Books, book => Assert.Equal("Demo", book.Status));
        vm.CycleCatalogueFilter();
        Assert.Equal(4, vm.Books.Count);
    }

    private static MainViewModel CreateViewModel(string directory)
        => new(new JsonLibraryRepository(Path.Combine(directory, "library.json")),
            new JsonDownloadQueue(Path.Combine(directory, "queue.json")));

    private sealed class FakeCatalogProvider(IReadOnlyList<RemoteAudiobook>? books = null, Exception? error = null) : ILibraryCatalogProvider
    {
        public Task<IReadOnlyList<RemoteAudiobook>> GetLibraryAsync(CancellationToken cancellationToken = default)
            => error is null ? Task.FromResult(books ?? (IReadOnlyList<RemoteAudiobook>)[]) : Task.FromException<IReadOnlyList<RemoteAudiobook>>(error);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "LibationMobileViewModelTests", Guid.NewGuid().ToString("N"));
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, true); }
    }
}
