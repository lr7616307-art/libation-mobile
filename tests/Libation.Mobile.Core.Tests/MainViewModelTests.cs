using Libation.Mobile.Core;
using Libation.Mobile.UI.ViewModels;
using Xunit;

namespace Libation.Mobile.Core.Tests;

public sealed class MainViewModelTests
{
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

    private static MainViewModel CreateViewModel(string directory)
        => new(new JsonLibraryRepository(Path.Combine(directory, "library.json")),
            new JsonDownloadQueue(Path.Combine(directory, "queue.json")));

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "LibationMobileViewModelTests", Guid.NewGuid().ToString("N"));
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, true); }
    }
}
