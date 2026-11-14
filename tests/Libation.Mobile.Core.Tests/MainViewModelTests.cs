using Libation.Mobile.Core;
using Libation.Mobile.UI.ViewModels;
using Xunit;

namespace Libation.Mobile.Core.Tests;

public sealed class MainViewModelTests
{
    [Fact]
    public void AddAccountSheet_UsesViewModelStateAndRaisesNotifications()
    {
        using var directory = new TemporaryDirectory();
        var vm = CreateViewModel(directory.Path);
        var notifications = new List<string?>();
        vm.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);

        vm.OpenAddAccountSheet();
        Assert.True(vm.IsAddAccountSheetVisible);
        Assert.Contains(nameof(vm.IsAddAccountSheetVisible), notifications);

        vm.CloseAddAccountSheet();
        Assert.False(vm.IsAddAccountSheetVisible);
        Assert.Equal(2, notifications.Count(name => name == nameof(vm.IsAddAccountSheetVisible)));
    }

    [Fact]
    public void SystemBack_ClosesAccountSheetBeforeLeavingAccountTab()
    {
        using var directory = new TemporaryDirectory();
        var vm = CreateViewModel(directory.Path);
        vm.SelectTab(2);
        vm.OpenAddAccountSheet();

        Assert.True(vm.TryNavigateBack());
        Assert.False(vm.IsAddAccountSheetVisible);
        Assert.True(vm.IsAudibleAccountTab);
    }

    [Fact]
    public async Task OpeningBookDetails_ClearsStaleDownloadFailureFromPreviousTitle()
    {
        using var directory = new TemporaryDirectory();
        var repository = new JsonLibraryRepository(Path.Combine(directory.Path, "library.json"));
        await repository.UpsertAsync(new Audiobook(
            "provider:1234567890",
            "Example audiobook",
            "Example author",
            "Example narrator",
            "1h",
            "Not started"));
        var vm = new MainViewModel(
            repository,
            new JsonDownloadQueue(Path.Combine(directory.Path, "queue.json")));
        await vm.InitializeAsync();
        vm.ReportAudioStatus("The download request failed. Check your connection and retry.");

        vm.OpenBookDetails("provider:1234567890");

        Assert.Equal(string.Empty, vm.AudioImportStatus);
        Assert.Equal("Availability not reported by Audible", vm.DetailAudibleAvailabilityLabel);
    }

    [Fact]
    public async Task RefreshAudiobookState_UpdatesOpenDetailAfterProtectedFileIsPublished()
    {
        using var directory = new TemporaryDirectory();
        var repository = new JsonLibraryRepository(Path.Combine(directory.Path, "library.json"));
        var book = new Audiobook(
            "provider:1234567890",
            "Example audiobook",
            "Example author",
            "Example narrator",
            "1h",
            "Not started");
        await repository.UpsertAsync(book);
        var vm = new MainViewModel(
            repository,
            new JsonDownloadQueue(Path.Combine(directory.Path, "queue.json")));
        await vm.InitializeAsync();
        vm.OpenBookDetails(book.Id);

        await repository.UpsertAsync(book with
        {
            ProtectedAudioFileName = "Example audiobook.aax",
            ProtectedAudioFolderName = "Downloads",
            ProtectedAudioProtection = "AudibleAdrm",
            FileSizeBytes = 12_345
        });

        var refreshed = await vm.RefreshAudiobookStateAsync(book.Id);

        Assert.Equal("Example audiobook.aax", refreshed?.ProtectedAudioFileName);
        Assert.Equal("Example audiobook.aax", vm.DetailBook?.ProtectedAudioFileName);
        Assert.Equal("PROTECTED BACKUP ONLY", vm.DetailBook?.AudibleAvailabilityShortLabel);
        Assert.Contains("not an ordinary MP3/M4B", vm.DetailProtectedDownloadLabel,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Widevine DASH", vm.DetailProtectedDownloadLabel,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AudibleDownloadQuality_CyclesAndPersistsGlobalPreference()
    {
        using var directory = new TemporaryDirectory();
        var settingsStore = new JsonDownloadSettingsStore(Path.Combine(directory.Path, "download-settings.json"));
        var vm = new MainViewModel(
            new JsonLibraryRepository(Path.Combine(directory.Path, "library.json")),
            new JsonDownloadQueue(Path.Combine(directory.Path, "queue.json")),
            downloadSettingsStore: settingsStore);

        await vm.InitializeAsync();
        Assert.Equal("High quality", vm.AudibleDownloadQualityLabel);

        await vm.CycleAudibleDownloadQualityAsync();

        Assert.Equal(AudibleDownloadQuality.Normal, (await settingsStore.GetAsync()).AudibleQuality);
        Assert.Equal("Normal quality", vm.AudibleDownloadQualityLabel);

        await vm.CycleAudibleDownloadQualityAsync();

        Assert.Equal(AudibleDownloadQuality.High, (await settingsStore.GetAsync()).AudibleQuality);
        Assert.Equal("High quality", vm.AudibleDownloadQualityLabel);
    }

    [Fact]
    public void ProbeWidevine_UsesPlatformDiagnosticAndRaisesNotification()
    {
        using var directory = new TemporaryDirectory();
        var vm = new MainViewModel(
            new JsonLibraryRepository(Path.Combine(directory.Path, "library.json")),
            new JsonDownloadQueue(Path.Combine(directory.Path, "queue.json")),
            widevineDiagnosticFactory: () => "Widevine supported. Security level: L3.");

        var notifications = 0;
        vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(vm.WidevineDiagnostic)) notifications++;
        };

        Assert.True(vm.CanProbeWidevine);
        Assert.Equal("Widevine check is available only in the Android app.", vm.WidevineDiagnostic);

        vm.ProbeWidevine();

        Assert.Equal("Widevine supported. Security level: L3.", vm.WidevineDiagnostic);
        Assert.Equal(1, notifications);
    }

    [Fact]
    public void ProbeWidevine_WhenPlatformProbeThrows_ShowsSafeDiagnostic()
    {
        using var directory = new TemporaryDirectory();
        var vm = new MainViewModel(
            new JsonLibraryRepository(Path.Combine(directory.Path, "library.json")),
            new JsonDownloadQueue(Path.Combine(directory.Path, "queue.json")),
            widevineDiagnosticFactory: () => throw new InvalidOperationException("private device detail"));

        vm.ProbeWidevine();

        Assert.Equal("Widevine check failed (InvalidOperationException).", vm.WidevineDiagnostic);
        Assert.DoesNotContain("private device detail", vm.WidevineDiagnostic);
    }

    [Fact]
    public void ProbeWidevine_WithoutAndroidFactory_RemainsUnavailable()
    {
        using var directory = new TemporaryDirectory();
        var vm = CreateViewModel(directory.Path);

        Assert.False(vm.CanProbeWidevine);
        vm.ProbeWidevine();

        Assert.Equal("Widevine check is available only in the Android app.", vm.WidevineDiagnostic);
    }

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
    public async Task AudibleLibraryEntries_OpenReadOnlyDetailsAndExposeAvailability()
    {
        using var directory = new TemporaryDirectory();
        var repository = new JsonLibraryRepository(Path.Combine(directory.Path, "library.json"));
        var remote = new RemoteAudiobook("remote-asin", "Audible title", "Author", "Narrator", "5h")
        {
            Description = "Audible product summary",
            IsPlayableOnAudible = true,
            IsDownloadedOnAudible = false
        };
        var vm = new MainViewModel(
            repository,
            new JsonDownloadQueue(Path.Combine(directory.Path, "queue.json")),
            new LibrarySyncService(new FakeCatalogProvider([remote]), repository));

        await vm.InitializeAsync();
        await vm.SyncLibraryAsync();

        var book = Assert.Single(vm.AudibleBooks);
        Assert.False(book.CanQueueDownload);
        Assert.False(book.CanDelete);
        Assert.Equal("AVAILABLE", book.AudibleAvailabilityShortLabel);

        vm.OpenBookDetails(book.Id);

        Assert.True(vm.IsBookDetailsVisible);
        Assert.True(vm.IsDetailProviderBook);
        Assert.False(vm.CanEditDetailBook);
        Assert.Equal("Available to download in VØRATH", vm.DetailAudibleAvailabilityLabel);
        Assert.Equal("Audible product summary", vm.DetailBookDescription);
    }

    [Fact]
    public async Task AudibleDetails_HideLibraryAndBackReturnsToTheFilteredResults()
    {
        using var directory = new TemporaryDirectory();
        var repository = new JsonLibraryRepository(Path.Combine(directory.Path, "library.json"));
        var detailProvider = new FakeAudiobookDetailsProvider(new ProviderAudiobookDetails(
            [new AudiobookChapter("Introduction", 0), new AudiobookChapter("Chapter 1", 120_000)],
            42_000_000));
        var vm = new MainViewModel(
            repository,
            new JsonDownloadQueue(Path.Combine(directory.Path, "queue.json")),
            new LibrarySyncService(new FakeCatalogProvider([
                new RemoteAudiobook("ikigai-1", "Ikigai for Young Readers", "Hector Garcia", "Narrator", "3h 48m"),
                new RemoteAudiobook("other-1", "Another book", "Someone Else", "Narrator", "5h")
            ]), repository),
            audiobookDetailsProvider: detailProvider);

        await vm.InitializeAsync();
        await vm.SyncLibraryAsync();
        vm.SelectTab(1);
        vm.AudibleSearchText = "Ikigai";
        var book = Assert.Single(vm.AudibleBooks);

        vm.OpenBookDetails(book.Id);
        Assert.False(vm.IsAudibleLibraryTab);
        await vm.LoadProviderBookDetailsAsync(book.Id);

        Assert.True(vm.IsBookDetailsVisible);
        Assert.Equal("2 chapters", vm.DetailChapterLabel);
        Assert.Equal("40.1 MB", vm.DetailFileSizeLabel);
        Assert.Equal(2, vm.DetailBook!.Chapters.Count);
        Assert.Equal("Ikigai for Young Readers", vm.DetailBook.Title);

        Assert.True(vm.TryNavigateBack());
        Assert.False(vm.IsBookDetailsVisible);
        Assert.True(vm.IsAudibleLibraryTab);
        Assert.Equal("Ikigai", vm.AudibleSearchText);
        Assert.Single(vm.AudibleBooks);
        Assert.Equal(1, detailProvider.CallCount);
    }

    [Fact]
    public async Task AudibleLibrarySystemBack_ClearsSearchAndStaysOnLibraryBeforeLeaving()
    {
        using var directory = new TemporaryDirectory();
        var repository = new JsonLibraryRepository(Path.Combine(directory.Path, "library.json"));
        var vm = new MainViewModel(
            repository,
            new JsonDownloadQueue(Path.Combine(directory.Path, "queue.json")),
            new LibrarySyncService(new FakeCatalogProvider([
                new RemoteAudiobook("ikigai-1", "Ikigai", "Hector Garcia", "Narrator", "3h"),
                new RemoteAudiobook("other-1", "Another book", "Someone Else", "Narrator", "5h")
            ]), repository));

        await vm.InitializeAsync();
        await vm.SyncLibraryAsync();
        vm.SelectTab(1);
        vm.AudibleSearchText = "Ikigai";
        Assert.Single(vm.AudibleBooks);

        Assert.True(vm.TryNavigateBack());
        Assert.True(vm.IsAudibleLibraryTab);
        Assert.Empty(vm.AudibleSearchText);
        Assert.Equal(2, vm.AudibleBooks.Count);

        Assert.True(vm.TryNavigateBack());
        Assert.True(vm.IsHomeTab);
    }

    [Fact]
    public async Task HomeSearchPage_FiltersCatalogueAndOpensSharedAudiobookDetails()
    {
        using var directory = new TemporaryDirectory();
        var vm = CreateViewModel(directory.Path);
        await vm.InitializeAsync();

        vm.NewTitle = "Atomic Habits";
        vm.NewAuthor = "James Clear";
        await vm.AddBookAsync();
        vm.NewTitle = "The Psychology of Money";
        vm.NewAuthor = "Morgan Housel";
        await vm.AddBookAsync();

        vm.OpenHomeSearch();
        Assert.True(vm.IsHomeSearchVisible);
        Assert.False(vm.IsHomeTab);
        Assert.True(vm.IsHomeNavigationSelected);

        vm.SearchText = "Atomic";
        var result = Assert.Single(vm.Books);
        Assert.Equal("Atomic Habits", result.Title);

        vm.OpenBookDetails(result.Id);
        Assert.True(vm.IsBookDetailsVisible);
        Assert.False(vm.IsHomeSearchVisible);
        Assert.Equal("Atomic Habits", vm.DetailBook?.Title);

        Assert.True(vm.TryNavigateBack());
        Assert.False(vm.IsBookDetailsVisible);
        Assert.True(vm.IsHomeSearchVisible);
        Assert.Single(vm.Books);
        Assert.Equal("Atomic", vm.SearchText);

        Assert.True(vm.TryNavigateBack());
        Assert.False(vm.IsHomeSearchVisible);
        Assert.True(vm.IsHomeTab);
        Assert.Equal("Atomic", vm.SearchText);
        Assert.False(vm.TryNavigateBack());
    }

    [Fact]
    public void HomeIndicator_IsVisibleOnlyOnTheHomeTab()
    {
        using var directory = new TemporaryDirectory();
        var vm = CreateViewModel(directory.Path);

        Assert.True(vm.IsHomeTab);
        vm.SelectTab(1);
        Assert.False(vm.IsHomeTab);
        vm.SelectTab(2);
        Assert.False(vm.IsHomeTab);
        vm.SelectTab(3);
        Assert.False(vm.IsHomeTab);
        vm.SelectTab(0);
        Assert.True(vm.IsHomeTab);
    }

    [Fact]
    public async Task SyncLibraryAsync_EmptyRemoteLibraryRemainsEmptyInsteadOfReseedingDemoBooks()
    {
        using var directory = new TemporaryDirectory();
        var repository = new JsonLibraryRepository(Path.Combine(directory.Path, "library.json"));
        var vm = new MainViewModel(
            repository,
            new JsonDownloadQueue(Path.Combine(directory.Path, "queue.json")),
            new LibrarySyncService(new FakeCatalogProvider([]), repository));

        await vm.InitializeAsync();
        Assert.Empty(vm.Books);

        await vm.SyncLibraryAsync();

        Assert.Empty(vm.Books);
        Assert.Empty(await repository.GetBooksAsync());
        Assert.Contains("0 audiobook metadata entries updated", vm.SyncStatus);
    }

    [Fact]
    public async Task EmptyCatalogue_RemainsEmptyAfterRestart()
    {
        using var directory = new TemporaryDirectory();
        var repository = new JsonLibraryRepository(Path.Combine(directory.Path, "library.json"));
        var first = new MainViewModel(
            repository,
            new JsonDownloadQueue(Path.Combine(directory.Path, "queue.json")),
            new LibrarySyncService(new FakeCatalogProvider([]), repository));

        await first.InitializeAsync();
        await first.SyncLibraryAsync();
        Assert.Empty(first.Books);

        var reopened = new MainViewModel(
            repository,
            new JsonDownloadQueue(Path.Combine(directory.Path, "queue.json")));
        await reopened.InitializeAsync();

        Assert.Empty(reopened.Books);
        Assert.Empty(await repository.GetBooksAsync());
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
    public async Task SyncLibraryAsync_WhenCancelled_ReportsCancellationWithoutThrowing()
    {
        using var directory = new TemporaryDirectory();
        var repository = new JsonLibraryRepository(Path.Combine(directory.Path, "library.json"));
        using var cts = new CancellationTokenSource();
        var provider = new FakeCatalogProvider(error: new OperationCanceledException());
        var vm = new MainViewModel(repository, new JsonDownloadQueue(Path.Combine(directory.Path, "queue.json")), new LibrarySyncService(provider, repository));
        await vm.InitializeAsync();
        cts.Cancel();

        await vm.SyncLibraryAsync(cts.Token);

        Assert.Contains("Sync cancelled", vm.SyncStatus);
        Assert.False(vm.IsSyncing);
        Assert.Null(vm.LastSuccessfulSync);
    }

    [Fact]
    public async Task SyncLibraryAsync_ConcurrentCallDoesNotStartSecondProviderRequest()
    {
        using var directory = new TemporaryDirectory();
        var repository = new JsonLibraryRepository(Path.Combine(directory.Path, "library.json"));
        var provider = new BlockingCatalogProvider();
        var vm = new MainViewModel(repository, new JsonDownloadQueue(Path.Combine(directory.Path, "queue.json")), new LibrarySyncService(provider, repository));
        await vm.InitializeAsync();

        var first = vm.SyncLibraryAsync();
        await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = vm.SyncLibraryAsync();
        await second;
        Assert.True(vm.IsSyncing);
        Assert.Equal(1, provider.CallCount);

        provider.Complete([]);
        await first;

        Assert.Equal(1, provider.CallCount);
        Assert.False(vm.IsSyncing);
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
        Assert.Empty(vm.Books);
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
    public async Task Delete_RemovesImportedEntry()
    {
        using var directory = new TemporaryDirectory();
        var vm = CreateViewModel(directory.Path);
        await vm.InitializeAsync();
        vm.NewTitle = "To delete";
        vm.NewAuthor = "Author";
        await vm.AddBookAsync();
        var manual = Assert.Single(vm.Books.Where(x => x.Title == "To delete"));
        await vm.RemoveBookAsync(manual.Id);

        Assert.DoesNotContain(vm.Books, x => x.Id == manual.Id);
    }

    [Fact]
    public async Task AddBookAsync_PersistsSeriesGenreAndDescriptionAndSearchFindsThem()
    {
        using var directory = new TemporaryDirectory();
        var vm = CreateViewModel(directory.Path);
        await vm.InitializeAsync();
        vm.NewTitle = "The Archive";
        vm.NewAuthor = "A. Writer";
        vm.NewSeries = "Forgotten Worlds";
        vm.NewGenre = "Science Fiction";
        vm.NewDescription = "A journey through distant galaxies.";

        await vm.AddBookAsync();

        var saved = Assert.Single((await new JsonLibraryRepository(Path.Combine(directory.Path, "library.json")).GetBooksAsync())
            .Where(book => book.Title == "The Archive"));
        Assert.Equal("Forgotten Worlds", saved.Series);
        Assert.Equal("Science Fiction", saved.Genre);
        Assert.Equal("A journey through distant galaxies.", saved.Description);

        vm.SearchText = "Forgotten Worlds";
        Assert.Contains(vm.Books, book => book.Id == saved.Id);
        vm.SearchText = "distant galaxies";
        Assert.Contains(vm.Books, book => book.Id == saved.Id);
    }

    [Fact]
    public async Task SyncLibraryAsync_MapsExtendedProviderMetadata()
    {
        using var directory = new TemporaryDirectory();
        var repository = new JsonLibraryRepository(Path.Combine(directory.Path, "library.json"));
        var remote = new RemoteAudiobook("remote-rich", "The Archive", "A. Writer", "Narrator", "9h")
        {
            Series = "Forgotten Worlds",
            Genre = "Science Fiction",
            Description = "A journey through distant galaxies."
        };
        var vm = new MainViewModel(
            repository,
            new JsonDownloadQueue(Path.Combine(directory.Path, "queue.json")),
            new LibrarySyncService(new FakeCatalogProvider([remote]), repository));

        await vm.InitializeAsync();
        await vm.SyncLibraryAsync();

        var saved = Assert.Single((await repository.GetBooksAsync()).Where(book => book.Id == "provider:remote-rich"));
        Assert.Equal("Forgotten Worlds", saved.Series);
        Assert.Equal("Science Fiction", saved.Genre);
        Assert.Equal("A journey through distant galaxies.", saved.Description);
    }

    [Fact]
    public async Task ListeningStatusFilter_DefaultsToAllAndFiltersEachStatus()
    {
        using var directory = new TemporaryDirectory();
        var repository = new JsonLibraryRepository(Path.Combine(directory.Path, "library.json"));
        await repository.ReplaceAllAsync([
            new Audiobook("not-started", "Not started book", "Author", "", "", "Manually added")
            {
                AudioFilePath = "not-started.mp3",
                ListeningStatus = "Not started"
            },
            new Audiobook("in-progress", "In progress book", "Author", "", "", "Manually added")
            {
                AudioFilePath = "in-progress.mp3",
                ListeningStatus = "In progress"
            },
            new Audiobook("finished", "Finished book", "Author", "", "", "Manually added")
            {
                AudioFilePath = "finished.mp3",
                ListeningStatus = "Finished"
            }
        ]);
        var vm = new MainViewModel(repository, new JsonDownloadQueue(Path.Combine(directory.Path, "queue.json")));

        await vm.InitializeAsync();

        Assert.Equal("All", vm.ListeningStatusFilter);
        Assert.Equal(3, vm.AllCount);
        Assert.Equal(3, vm.FilteredLocalAudioBooks.Count);

        vm.SetListeningStatusFilter("Not started");
        Assert.Single(vm.FilteredLocalAudioBooks);
        Assert.Equal("Not started book", vm.FilteredLocalAudioBooks[0].Title);

        vm.SetListeningStatusFilter("In progress");
        Assert.Single(vm.FilteredLocalAudioBooks);
        Assert.Equal("In progress book", vm.FilteredLocalAudioBooks[0].Title);

        vm.SetListeningStatusFilter("Finished");
        Assert.Single(vm.FilteredLocalAudioBooks);
        Assert.Equal("Finished book", vm.FilteredLocalAudioBooks[0].Title);

        vm.SetListeningStatusFilter("All");
        Assert.Equal(3, vm.FilteredLocalAudioBooks.Count);
    }

    [Fact]
    public async Task MarkPlaybackStartedAsync_ChangesNotStartedBookToInProgressAndPersistsCounts()
    {
        using var directory = new TemporaryDirectory();
        var repository = new JsonLibraryRepository(Path.Combine(directory.Path, "library.json"));
        await repository.ReplaceAllAsync([
            new Audiobook("local-1", "Test audiobook", "Author", "", "", "Manually added")
            {
                AudioFilePath = "test-audio.mp3",
                ListeningStatus = "Not started"
            }
        ]);
        var vm = new MainViewModel(repository, new JsonDownloadQueue(Path.Combine(directory.Path, "queue.json")));
        await vm.InitializeAsync();

        Assert.Equal(1, vm.NotStartedCount);
        Assert.Equal(0, vm.InProgressCount);

        await vm.MarkPlaybackStartedAsync("local-1");

        Assert.Equal(0, vm.NotStartedCount);
        Assert.Equal(1, vm.InProgressCount);
        Assert.Single(vm.FilteredLocalAudioBooks);
        Assert.Equal("In progress", Assert.Single(await repository.GetBooksAsync()).ListeningStatus);

        vm.SetListeningStatusFilter("In progress");
        Assert.Single(vm.FilteredLocalAudioBooks);
    }

    [Fact]
    public async Task Search_FiltersByTitleAuthorAndNarrator()
    {
        using var directory = new TemporaryDirectory();
        var vm = CreateViewModel(directory.Path);
        await vm.InitializeAsync();

        vm.NewTitle = "Project Hail Mary";
        vm.NewAuthor = "Andy Weir";
        vm.NewNarrator = "Ray Porter";
        await vm.AddBookAsync();
        vm.SearchText = "Ray Porter";
        Assert.Contains(vm.Books, x => x.Title == "Project Hail Mary");
        Assert.Single(vm.Books);
        vm.SearchText = "not found";
        Assert.Empty(vm.Books);
    }

    [Fact]
    public async Task Search_RaisesEmptyMessageChangedOnlyOncePerFilter()
    {
        using var directory = new TemporaryDirectory();
        var vm = CreateViewModel(directory.Path);
        await vm.InitializeAsync();
        vm.NewTitle = "A real book";
        vm.NewAuthor = "An author";
        await vm.AddBookAsync();
        var notifications = 0;
        vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(vm.EmptyMessage)) notifications++;
        };

        vm.SearchText = "no matching title";

        Assert.Equal(1, notifications);
        Assert.Contains("No matching audiobooks", vm.EmptyMessage);
    }

    [Fact]
    public async Task Search_NoMatchesShowsNoResultsInsteadOfEmptyCatalogue()
    {
        using var directory = new TemporaryDirectory();
        var vm = CreateViewModel(directory.Path);
        await vm.InitializeAsync();
        vm.NewTitle = "Project Hail Mary";
        vm.NewAuthor = "Andy Weir";
        await vm.AddBookAsync();

        vm.SearchText = "Atomic Habits";
        Assert.Empty(vm.Books);
        Assert.Equal("No matching audiobooks. Try another title, author or narrator.", vm.EmptyMessage);
        Assert.Equal("0 of 1 audiobooks", vm.CatalogueSummaryLabel);

        vm.SearchText = "Project Hail Mary";
        Assert.Single(vm.Books);
        Assert.Empty(vm.EmptyMessage);
        Assert.Equal("1 of 1 audiobooks", vm.CatalogueSummaryLabel);
    }

    [Fact]
    public async Task Enqueue_AudibleCatalogueEntryIsAddedToTheVOrathQueue()
    {
        using var directory = new TemporaryDirectory();
        var queue = new JsonDownloadQueue(Path.Combine(directory.Path, "queue.json"));
        var repository = new JsonLibraryRepository(Path.Combine(directory.Path, "library.json"));
        var vm = new MainViewModel(repository, queue,
            new LibrarySyncService(new FakeCatalogProvider([
                new RemoteAudiobook("B012345678", "Audible title", "Author", "Narrator", "5h")
            ]), repository));
        await vm.InitializeAsync();
        await vm.SyncLibraryAsync();

        var book = Assert.Single(vm.Books.Where(item => item.Id == "provider:B012345678"));
        Assert.False(book.CanQueueDownload); // Audible IDs cannot be sent to the generic direct-URL form.
        Assert.Equal("⇩  Download & play offline", book.DownloadActionLabel);
        Assert.Equal("Protected backup saved",
            (book with { ProtectedAudioFileName = "audible-title.aaxc" }).DownloadActionLabel);
        Assert.Equal("▶  Play offline",
            (book with { IsWidevineOfflineAvailable = true }).DownloadActionLabel);

        await vm.EnqueueAsync(book.Id);

        var queued = Assert.Single(await queue.GetItemsAsync());
        Assert.Equal(book.Id, queued.AudiobookId);
        Assert.Equal(DownloadStatus.Queued, queued.Status);
        Assert.Empty(vm.ErrorMessage);
        Assert.Contains("Audible download queue", vm.DownloadSettingsStatus);
    }

    [Fact]
    public async Task QueueSummary_ReflectsPersistedStatusCounts()
    {
        using var directory = new TemporaryDirectory();
        var queue = new JsonDownloadQueue(Path.Combine(directory.Path, "queue.json"));
        var vm = new MainViewModel(
            new JsonLibraryRepository(Path.Combine(directory.Path, "library.json")), queue);
        await vm.InitializeAsync();

        Assert.Equal("0 queued · 0 downloading · 0 paused · 0 completed · 0 failed", vm.QueueSummaryLabel);

        await vm.EnqueueAsync("book-1");
        Assert.Equal("1 queued · 0 downloading · 0 paused · 0 completed · 0 failed", vm.QueueSummaryLabel);

        await queue.UpdateStatusAsync("book-1", DownloadStatus.Downloading);
        await queue.UpdateStatusAsync("book-1", DownloadStatus.Completed);
        await vm.ReloadAsync();

        Assert.Equal("0 queued · 0 downloading · 0 paused · 1 completed · 0 failed", vm.QueueSummaryLabel);
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
    public async Task BackupImport_InvalidFileReportsSafeErrorWithoutExposingExceptionDetails()
    {
        using var directory = new TemporaryDirectory();
        var vm = CreateViewModel(directory.Path);
        await vm.InitializeAsync();
        await using var invalidBackup = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("private-path-token: /storage/emulated/0/secret.json"));

        await vm.PrepareImportAsync(invalidBackup);

        Assert.False(vm.HasPendingImport);
        Assert.Equal("Could not read this backup file. Check that it is a valid catalogue backup and try again.", vm.ErrorMessage);
        Assert.DoesNotContain("/storage/emulated/0/secret.json", vm.ErrorMessage);
        Assert.DoesNotContain("private-path-token", vm.ErrorMessage);
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
        vm.NewTitle = "Project Hail Mary";
        vm.NewAuthor = "Andy Weir";
        await vm.AddBookAsync();
        vm.NewTitle = "The Three-Body Problem";
        vm.NewAuthor = "Cixin Liu";
        await vm.AddBookAsync();
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
        Assert.Empty(vm.Books);
        vm.CycleCatalogueFilter();
        Assert.Single(vm.Books);
    }

    [Fact]
    public async Task ImportAudioAsync_AttachesFileToSelectedBookAndPersistsPath()
    {
        using var directory = new TemporaryDirectory();
        var repository = new JsonLibraryRepository(Path.Combine(directory.Path, "library.json"));
        var importService = new LocalAudioImportService(Path.Combine(directory.Path, "audio"));
        var vm = new MainViewModel(repository, new JsonDownloadQueue(Path.Combine(directory.Path, "queue.json")), audioImportService: importService);
        await vm.InitializeAsync();
        vm.NewTitle = "Project Hail Mary";
        vm.NewAuthor = "Andy Weir";
        await vm.AddBookAsync();
        var book = Assert.Single(vm.Books.Where(x => x.Title == "Project Hail Mary"));
        await using var audio = new MemoryStream([1, 2, 3, 4, 5]);

        await vm.ImportAudioAsync(book.Id, "sample.m4b", audio);

        var saved = Assert.Single((await repository.GetBooksAsync()).Where(x => x.Id == book.Id));
        Assert.NotNull(saved.AudioFilePath);
        Assert.True(File.Exists(saved.AudioFilePath));
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, await File.ReadAllBytesAsync(saved.AudioFilePath));
        Assert.Contains("Audio attached", vm.AudioImportStatus);
    }

    [Fact]
    public async Task ImportAudioAsync_RejectsUnsupportedFileAndDoesNotAttachPath()
    {
        using var directory = new TemporaryDirectory();
        var repository = new JsonLibraryRepository(Path.Combine(directory.Path, "library.json"));
        var vm = new MainViewModel(repository, new JsonDownloadQueue(Path.Combine(directory.Path, "queue.json")),
            audioImportService: new LocalAudioImportService(Path.Combine(directory.Path, "audio")));
        await vm.InitializeAsync();
        vm.NewTitle = "Test book";
        vm.NewAuthor = "Test author";
        await vm.AddBookAsync();
        var book = vm.Books[0];
        await using var audio = new MemoryStream([1, 2, 3]);

        await vm.ImportAudioAsync(book.Id, "protected.aax", audio);

        var saved = Assert.Single((await repository.GetBooksAsync()).Where(x => x.Id == book.Id));
        Assert.Null(saved.AudioFilePath);
        Assert.Contains("Could not import", vm.AudioImportStatus);
    }
    [Fact]
    public async Task PlaybackPosition_IsPersistedAndClampedToZero()
    {
        using var directory = new TemporaryDirectory();
        var repository = new JsonLibraryRepository(Path.Combine(directory.Path, "library.json"));
        var vm = new MainViewModel(repository, new JsonDownloadQueue(Path.Combine(directory.Path, "queue.json")),
            audioImportService: new LocalAudioImportService(Path.Combine(directory.Path, "audio")));
        await vm.InitializeAsync();
        vm.NewTitle = "Project Hail Mary";
        vm.NewAuthor = "Andy Weir";
        await vm.AddBookAsync();
        var book = vm.Books.First(x => x.Title == "Project Hail Mary");
        await using var audio = new MemoryStream([1, 2, 3]);
        await vm.ImportAudioAsync(book.Id, "sample.m4b", audio);

        await vm.SavePlaybackPositionAsync(book.Id, 123456);
        Assert.Equal(123456, vm.GetResumePositionMilliseconds(book.Id));

        await vm.SavePlaybackPositionAsync(book.Id, -50);
        Assert.Equal(0, vm.GetResumePositionMilliseconds(book.Id));

        var saved = Assert.Single((await repository.GetBooksAsync()).Where(x => x.Id == book.Id));
        Assert.Equal(0, saved.PlaybackPositionMilliseconds);
    }

    [Fact]
    public async Task PlaybackPosition_IsIgnoredWhenBookHasNoImportedAudio()
    {
        using var directory = new TemporaryDirectory();
        var repository = new JsonLibraryRepository(Path.Combine(directory.Path, "library.json"));
        var vm = new MainViewModel(repository, new JsonDownloadQueue(Path.Combine(directory.Path, "queue.json")));
        await vm.InitializeAsync();
        vm.NewTitle = "A book";
        vm.NewAuthor = "An author";
        await vm.AddBookAsync();
        var book = Assert.Single(vm.Books);

        await vm.SavePlaybackPositionAsync(book.Id, 9000);

        Assert.Equal(0, vm.GetResumePositionMilliseconds(book.Id));
    }

    [Fact]
    public async Task TryNavigateBack_ClosesSearchPageAndPreservesItsQuery()
    {
        using var directory = new TemporaryDirectory();
        var vm = CreateViewModel(directory.Path);
        await vm.InitializeAsync();
        vm.NewTitle = "Searchable title";
        vm.NewAuthor = "Author";
        await vm.AddBookAsync();
        vm.OpenHomeSearch();
        vm.SearchText = "Searchable";
        Assert.Single(vm.Books);
        Assert.True(vm.IsHomeSearchVisible);
        Assert.True(vm.IsHomeNavigationSelected);

        Assert.True(vm.TryNavigateBack());

        Assert.False(vm.IsHomeSearchVisible);
        Assert.True(vm.IsHomeTab);
        Assert.Equal("Searchable", vm.SearchText);
        Assert.Single(vm.Books);
        Assert.False(vm.TryNavigateBack());
    }

    [Fact]
    public async Task TryNavigateBack_ClosesNestedPlayerAndReturnsToHomeWithoutChangingPlaybackBook()
    {
        using var directory = new TemporaryDirectory();
        var vm = CreateViewModel(directory.Path);
        await vm.InitializeAsync();
        vm.NewTitle = "Now playing";
        vm.NewAuthor = "Author";
        await vm.AddBookAsync();
        var book = Assert.Single(vm.Books);
        vm.SetNowPlayingBook(book.Id);
        vm.SelectTab(2);
        vm.OpenFullPlayer();
        vm.OpenChapters();

        Assert.True(vm.TryNavigateBack());
        Assert.True(vm.IsFullPlayerVisible);
        Assert.False(vm.IsChaptersVisible);

        Assert.True(vm.TryNavigateBack());
        Assert.False(vm.IsFullPlayerVisible);
        Assert.Equal(book.Id, vm.NowPlayingBook?.Id);
    }

    [Fact]
    public async Task ConfirmPendingRemoval_RemovesBookAndClosesStaleDetailsAndMenu()
    {
        using var directory = new TemporaryDirectory();
        var repository = new JsonLibraryRepository(Path.Combine(directory.Path, "library.json"));
        var vm = new MainViewModel(repository, new JsonDownloadQueue(Path.Combine(directory.Path, "queue.json")));
        await vm.InitializeAsync();
        vm.NewTitle = "Imported title";
        vm.NewAuthor = "Author";
        await vm.AddBookAsync();
        var book = Assert.Single(vm.Books);
        vm.OpenBookMenu(book.Id);
        vm.OpenBookDetails(book.Id);
        vm.RequestRemoveBook(book.Id);

        Assert.True(vm.IsRemovalConfirmationVisible);
        Assert.True(vm.TryNavigateBack());
        Assert.False(vm.IsRemovalConfirmationVisible);
        Assert.Single(vm.Books);

        vm.RequestRemoveBook(book.Id);
        await vm.ConfirmPendingRemovalAsync();

        Assert.False(vm.IsRemovalConfirmationVisible);
        Assert.False(vm.IsBookDetailsVisible);
        Assert.Null(vm.DetailBook);
        Assert.Null(vm.MenuBook);
        Assert.Empty(vm.Books);
        Assert.Empty(await repository.GetBooksAsync());
    }

    private static MainViewModel CreateViewModel(string directory)
        => new(new JsonLibraryRepository(Path.Combine(directory, "library.json")),
            new JsonDownloadQueue(Path.Combine(directory, "queue.json")));

    private sealed class FakeCatalogProvider(IReadOnlyList<RemoteAudiobook>? books = null, Exception? error = null) : ILibraryCatalogProvider
    {
        public Task<IReadOnlyList<RemoteAudiobook>> GetLibraryAsync(CancellationToken cancellationToken = default)
            => error is null ? Task.FromResult(books ?? (IReadOnlyList<RemoteAudiobook>)[]) : Task.FromException<IReadOnlyList<RemoteAudiobook>>(error);
    }

    private sealed class FakeAudiobookDetailsProvider(ProviderAudiobookDetails details) : IAudiobookDetailsProvider
    {
        public int CallCount { get; private set; }

        public Task<ProviderAudiobookDetails?> GetDetailsAsync(
            string providerId,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult<ProviderAudiobookDetails?>(details);
        }
    }

    private sealed class BlockingCatalogProvider : ILibraryCatalogProvider
    {
        private readonly TaskCompletionSource<IReadOnlyList<RemoteAudiobook>> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int CallCount { get; private set; }

        public Task<IReadOnlyList<RemoteAudiobook>> GetLibraryAsync(CancellationToken cancellationToken = default)
        {
            CallCount++;
            Started.TrySetResult(true);
            return _completion.Task;
        }

        public void Complete(IReadOnlyList<RemoteAudiobook> books) => _completion.TrySetResult(books);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "LibationMobileViewModelTests", Guid.NewGuid().ToString("N"));
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, true); }
    }
}
