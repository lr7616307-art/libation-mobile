using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using Libation.Mobile.Core;

namespace Libation.Mobile.UI.ViewModels;

/// <summary>Displays a locally persisted catalogue and metadata-only download queue.</summary>
public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly ILibraryRepository _repository;
    private readonly IDownloadQueue _downloadQueue;
    private readonly LocalAudioImportService? _audioImportService;
    private readonly CoverArtImportService? _coverArtImportService;
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private readonly SemaphoreSlim _syncGate = new(1, 1);
    private readonly LibrarySyncService? _syncService;
    private bool _isSyncing;
    private string _syncStatus = "Provider not connected. Audible library sync is not available yet.";
    private DateTimeOffset? _lastSuccessfulSync;
    private string _searchText = string.Empty;
    private string _newTitle = string.Empty;
    private string _newAuthor = string.Empty;
    private string _newNarrator = string.Empty;
    private string _newLength = string.Empty;
    private string _newSeries = string.Empty;
    private string _newGenre = string.Empty;
    private string _newDescription = string.Empty;
    private string _newChapterTitle = string.Empty;
    private string _newChapterStart = string.Empty;
    private string? _editingBookId;
    private bool _isLoading;
    private string _errorMessage = string.Empty;
    private string _audioImportStatus = string.Empty;
    private IReadOnlyList<Audiobook>? _pendingImport;
    private string _backupMessage = string.Empty;
    private bool _sortAscending = true;
    private int _catalogueFilterIndex;
    private Audiobook? _selectedBook;

    private static readonly Audiobook[] InitialSamples =
    [
        new("demo-001", "The Long Way to a Small, Angry Planet", "Becky Chambers", "Rachel Dulude", "12h 20m", "Demo"),
        new("demo-002", "Project Hail Mary", "Andy Weir", "Ray Porter", "16h 10m", "Demo"),
        new("demo-003", "The Three-Body Problem", "Cixin Liu", "Luke Daniels", "13h 40m", "Demo")
    ];

    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<Audiobook> Books { get; } = [];
    public ObservableCollection<Audiobook> LocalAudioBooks { get; } = [];
    public bool HasLocalAudioBooks => LocalAudioBooks.Count > 0;
    public bool HasNoLocalAudioBooks => !HasLocalAudioBooks;
    public string LocalAudioEmptyMessage => HasLocalAudioBooks ? string.Empty : "No locally imported audiobooks yet. Import audio from the Library tab to see it here.";
    public ObservableCollection<DownloadQueueItem> QueueItems { get; } = [];
    public Audiobook? SelectedBook
    {
        get => _selectedBook;
        private set
        {
            if (_selectedBook == value) return;
            _selectedBook = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasSelectedBook));
            OnPropertyChanged(nameof(SelectedBookAudioLabel));
            OnPropertyChanged(nameof(SelectedBookResumeLabel));
            OnPropertyChanged(nameof(SelectedBookChapterCountLabel));
        }
    }
    public bool HasSelectedBook => SelectedBook is not null;
    public string SelectedBookAudioLabel => SelectedBook is null ? string.Empty :
        string.IsNullOrWhiteSpace(SelectedBook.AudioFilePath) ? "Audio not imported on this device" : "Local audio is ready to play";
    public string SelectedBookResumeLabel => SelectedBook is null ? string.Empty :
        SelectedBook.PlaybackPositionMilliseconds > 0
            ? $"Saved position: {TimeSpan.FromMilliseconds(SelectedBook.PlaybackPositionMilliseconds):h\\:mm\\:ss}"
            : "Not started yet";
    public void SelectBook(string audiobookId)
    {
        SelectedBook = _allBooks.FirstOrDefault(book => book.Id == audiobookId);
    }

    public MainViewModel(ILibraryRepository repository, IDownloadQueue downloadQueue, LibrarySyncService? syncService = null, LocalAudioImportService? audioImportService = null, CoverArtImportService? coverArtImportService = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _downloadQueue = downloadQueue ?? throw new ArgumentNullException(nameof(downloadQueue));
        _syncService = syncService;
        _audioImportService = audioImportService;
        _coverArtImportService = coverArtImportService;
    }

    public bool IsSyncing { get => _isSyncing; private set { if (_isSyncing == value) return; _isSyncing = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanSync)); } }
    public bool CanSync => _syncService is not null && !IsSyncing;
    public string SyncStatus { get => _syncStatus; private set { if (_syncStatus == value) return; _syncStatus = value; OnPropertyChanged(); } }
    public DateTimeOffset? LastSuccessfulSync
    {
        get => _lastSuccessfulSync;
        private set
        {
            if (_lastSuccessfulSync == value) return;
            _lastSuccessfulSync = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(LastSuccessfulSyncLabel));
        }
    }
    public string LastSuccessfulSyncLabel => LastSuccessfulSync is null
        ? "No successful sync yet."
        : $"Last successful sync: {LastSuccessfulSync.Value.ToLocalTime():g}";

    public async Task SyncLibraryAsync(CancellationToken cancellationToken = default)
    {
        if (_syncService is null)
        {
            SyncStatus = "Provider not connected. Audible library sync is not available yet.";
            return;
        }
        var gateAcquired = false;
        try
        {
            if (!await _syncGate.WaitAsync(0, cancellationToken)) return;
            gateAcquired = true;
            IsSyncing = true;
            SyncStatus = "Syncing library metadata…";
            var count = await _syncService.SyncAsync(cancellationToken);
            // Refresh directly instead of InitializeAsync: an intentionally empty
            // provider library must remain empty, not trigger first-run demo seeding.
            var stored = await _repository.GetBooksAsync(cancellationToken);
            _allBooks = stored;
            ApplyFilter(_allBooks);
            await _downloadQueue.RecoverInterruptedDownloadsAsync(cancellationToken);
            await ReloadQueueAsync(cancellationToken);
            LastSuccessfulSync = DateTimeOffset.UtcNow;
            SyncStatus = $"Sync complete. {count} audiobook metadata entr{(count == 1 ? "y" : "ies")} updated.";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            SyncStatus = "Sync cancelled. Your local catalogue was not intentionally changed.";
            return;
        }
        catch (Exception)
        {
            SyncStatus = "Sync failed. Your existing local catalogue was kept; try again later.";
        }
        finally
        {
            if (gateAcquired)
            {
                IsSyncing = false;
                _syncGate.Release();
            }
        }
    }

    public string SearchText
    {
        get => _searchText;
        set { if (_searchText == value) return; _searchText = value; OnPropertyChanged(); ApplyFilter(_allBooks); }
    }

    public string NewTitle { get => _newTitle; set => SetField(ref _newTitle, value); }
    public string NewAuthor { get => _newAuthor; set => SetField(ref _newAuthor, value); }
    public string NewNarrator { get => _newNarrator; set => SetField(ref _newNarrator, value); }
    public string NewLength { get => _newLength; set => SetField(ref _newLength, value); }
    public string NewSeries { get => _newSeries; set => SetField(ref _newSeries, value); }
    public string NewGenre { get => _newGenre; set => SetField(ref _newGenre, value); }
    public string NewDescription { get => _newDescription; set => SetField(ref _newDescription, value); }
    public string NewChapterTitle { get => _newChapterTitle; set => SetField(ref _newChapterTitle, value); }
    public string NewChapterStart { get => _newChapterStart; set => SetField(ref _newChapterStart, value); }
    public string SelectedBookChapterCountLabel => SelectedBook is null ? string.Empty : $"{SelectedBook.Chapters.Count} chapter marker(s)";
    public bool IsEditing => _editingBookId is not null;
    public string SaveButtonLabel => IsEditing ? "Save changes" : "Save to local catalogue";

    private IReadOnlyList<Audiobook> _allBooks = [];
    public bool IsLoading { get => _isLoading; private set { if (_isLoading == value) return; _isLoading = value; OnPropertyChanged(); } }
    public string ErrorMessage
    {
        get => _errorMessage;
        private set { if (_errorMessage == value) return; _errorMessage = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasError)); }
    }

    public bool HasBooks => Books.Count > 0;
    public string AudioImportStatus { get => _audioImportStatus; private set { if (_audioImportStatus == value) return; _audioImportStatus = value; OnPropertyChanged(); } }
    public bool CanImportAudio => _audioImportService is not null;
    public void ReportAudioStatus(string message) => AudioImportStatus = message;
    public string SortLabel => _sortAscending ? "Sort: A-Z" : "Sort: Z-A";
    public string FilterLabel => _catalogueFilterIndex switch { 1 => "Filter: Manual", 2 => "Filter: Demo", _ => "Filter: All" };
    public void ToggleSort() { _sortAscending = !_sortAscending; OnPropertyChanged(nameof(SortLabel)); ApplyFilter(_allBooks); }
    public void CycleCatalogueFilter() { _catalogueFilterIndex = (_catalogueFilterIndex + 1) % 3; OnPropertyChanged(nameof(FilterLabel)); ApplyFilter(_allBooks); }
    public string BackupMessage { get => _backupMessage; private set { if (_backupMessage == value) return; _backupMessage = value; OnPropertyChanged(); } }
    public bool HasPendingImport => _pendingImport is not null;
    public string PendingImportLabel => _pendingImport is null ? string.Empty : "Backup contains " + _pendingImport.Count + " entries. Confirm to replace the current catalogue.";
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool IsQueueEmpty => QueueItems.Count == 0;
    public string QueueCountLabel => $"Download queue ({QueueItems.Count})";
    public string QueueSummaryLabel => $"{QueueItems.Count(x => x.Status == DownloadStatus.Queued)} queued · {QueueItems.Count(x => x.Status == DownloadStatus.Downloading)} downloading · {QueueItems.Count(x => x.Status == DownloadStatus.Completed)} completed · {QueueItems.Count(x => x.Status == DownloadStatus.Failed)} failed";
    public string EmptyMessage => HasBooks ? string.Empty :
        string.IsNullOrWhiteSpace(SearchText) ? "Your local catalogue is empty." : "No matching audiobooks.";

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _loadGate.WaitAsync(cancellationToken);
        try
        {
            IsLoading = true;
            ErrorMessage = string.Empty;
            await _repository.EnsureInitializedAsync(InitialSamples, cancellationToken);
            var stored = await _repository.GetBooksAsync(cancellationToken);
            _allBooks = stored;
            ApplyFilter(_allBooks);
            await ReloadQueueAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { ErrorMessage = "Could not load local data. Check app storage and try again."; _allBooks = []; ApplyFilter(_allBooks); }
        finally { IsLoading = false; _loadGate.Release(); }
    }

    public Task ReloadAsync(CancellationToken cancellationToken = default) => InitializeAsync(cancellationToken);

    public void BeginEdit(string audiobookId)
    {
        var book = _allBooks.FirstOrDefault(x => x.Id == audiobookId);
        if (book is null || !book.CanDelete) return;
        _editingBookId = book.Id;
        NewTitle = book.Title;
        NewAuthor = book.Author;
        NewNarrator = book.Narrator;
        NewLength = book.Length;
        NewSeries = book.Series;
        NewGenre = book.Genre;
        NewDescription = book.Description;
        OnPropertyChanged(nameof(IsEditing));
        OnPropertyChanged(nameof(SaveButtonLabel));
        ErrorMessage = string.Empty;
    }

    public void CancelEdit()
    {
        _editingBookId = null;
        ClearEditor();
        OnPropertyChanged(nameof(IsEditing));
        OnPropertyChanged(nameof(SaveButtonLabel));
        ErrorMessage = string.Empty;
    }

    public async Task AddBookAsync(CancellationToken cancellationToken = default)
    {
        var title = NewTitle.Trim();
        var author = NewAuthor.Trim();
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(author))
        {
            ErrorMessage = "Enter at least the audiobook title and author.";
            return;
        }

        try
        {
            Audiobook book;
            if (_editingBookId is not null)
            {
                var existing = _allBooks.FirstOrDefault(x => x.Id == _editingBookId);
                if (existing is null) { CancelEdit(); ErrorMessage = "This audiobook is no longer in the catalogue."; return; }
                book = existing with { Title = title, Author = author, Narrator = NewNarrator.Trim(), Length = NewLength.Trim(), Series = NewSeries.Trim(), Genre = NewGenre.Trim(), Description = NewDescription.Trim() };
            }
            else
            {
                book = new Audiobook(Guid.NewGuid().ToString("N"), title, author, NewNarrator.Trim(), NewLength.Trim(), "Manually added")
                {
                    Series = NewSeries.Trim(),
                    Genre = NewGenre.Trim(),
                    Description = NewDescription.Trim()
                };
            }

            await _repository.UpsertAsync(book, cancellationToken);
            _allBooks = await _repository.GetBooksAsync(cancellationToken);
            _editingBookId = null;
            ClearEditor();
            OnPropertyChanged(nameof(IsEditing));
            OnPropertyChanged(nameof(SaveButtonLabel));
            ApplyFilter(_allBooks);
            if (SelectedBook?.Id == book.Id) SelectBook(book.Id);
            ErrorMessage = string.Empty;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { ErrorMessage = "Could not save this audiobook to the local catalogue."; }
    }

    public async Task AddChapterAsync(CancellationToken cancellationToken = default)
    {
        if (SelectedBook is null) { ErrorMessage = "Open an audiobook's details before adding a chapter."; return; }
        var title = NewChapterTitle.Trim();
        if (string.IsNullOrWhiteSpace(title)) { ErrorMessage = "Enter a chapter title."; return; }
        if (!TimeSpan.TryParseExact(NewChapterStart.Trim(), [@"m\:ss", @"mm\:ss", @"h\:mm\:ss", @"hh\:mm\:ss"], CultureInfo.InvariantCulture, out var start)
            || start < TimeSpan.Zero || start.TotalMilliseconds > long.MaxValue)
        { ErrorMessage = "Enter a valid start time as mm:ss or hh:mm:ss."; return; }
        var startMilliseconds = (long)start.TotalMilliseconds;
        var chapters = AudiobookChapterService.Normalize(SelectedBook.Chapters).ToList();
        if (chapters.Any(chapter => chapter.StartPositionMilliseconds == startMilliseconds))
        { ErrorMessage = "A chapter already starts at that time. Choose a different start time."; return; }
        try
        {
            var updated = SelectedBook with { Chapters = AudiobookChapterService.Normalize(chapters.Append(new AudiobookChapter(title, startMilliseconds))) };
            await _repository.UpsertAsync(updated, cancellationToken);
            _allBooks = await _repository.GetBooksAsync(cancellationToken);
            SelectBook(updated.Id);
            NewChapterTitle = string.Empty; NewChapterStart = string.Empty;
            ErrorMessage = string.Empty; AudioImportStatus = $"Chapter added to: {updated.Title}";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { ErrorMessage = "Could not save this chapter marker."; }
    }

    public async Task RemoveChapterAsync(long startPositionMilliseconds, CancellationToken cancellationToken = default)
    {
        if (SelectedBook is null) return;
        try
        {
            var updated = SelectedBook with { Chapters = AudiobookChapterService.Normalize(SelectedBook.Chapters).Where(chapter => chapter.StartPositionMilliseconds != startPositionMilliseconds).ToArray() };
            await _repository.UpsertAsync(updated, cancellationToken);
            _allBooks = await _repository.GetBooksAsync(cancellationToken);
            SelectBook(updated.Id);
            ErrorMessage = string.Empty; AudioImportStatus = $"Chapter marker removed from: {updated.Title}";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { ErrorMessage = "Could not remove this chapter marker."; }
    }

    public async Task RemoveBookAsync(string audiobookId, CancellationToken cancellationToken = default)
    {
        try
        {
            var book = _allBooks.FirstOrDefault(x => x.Id == audiobookId);
            if (book is null || !book.CanDelete) { ErrorMessage = "Demo entries are protected; only manually added entries can be deleted."; return; }
            if (_editingBookId == audiobookId) CancelEdit();
            if (SelectedBook?.Id == audiobookId) SelectedBook = null;
            await _repository.RemoveAsync(audiobookId, cancellationToken);
            _allBooks = await _repository.GetBooksAsync(cancellationToken);
            ApplyFilter(_allBooks);
            ErrorMessage = string.Empty;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { ErrorMessage = "Could not delete this audiobook from the local catalogue."; }
    }


    public async Task ExportBackupAsync(Stream destination, CancellationToken cancellationToken = default)
    {
        try { await LibraryBackupService.ExportAsync(destination, _allBooks, cancellationToken); BackupMessage = "Catalogue backup exported successfully."; ErrorMessage = string.Empty; }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { ErrorMessage = "Could not export the catalogue backup."; }
    }

    public async Task PrepareImportAsync(Stream source, CancellationToken cancellationToken = default)
    {
        try
        {
            _pendingImport = await LibraryBackupService.ImportAsync(source, cancellationToken);
            OnPropertyChanged(nameof(HasPendingImport));
            OnPropertyChanged(nameof(PendingImportLabel));
            BackupMessage = "Backup validated. Review the entry count before confirming replacement.";
            ErrorMessage = string.Empty;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _pendingImport = null;
            OnPropertyChanged(nameof(HasPendingImport));
            OnPropertyChanged(nameof(PendingImportLabel));
            ErrorMessage = ex.Message;
        }
    }

    public async Task ConfirmImportAsync(CancellationToken cancellationToken = default)
    {
        if (_pendingImport is null) return;
        try
        {
            await _repository.ReplaceAllAsync(_pendingImport, cancellationToken);
            _pendingImport = null;
            OnPropertyChanged(nameof(HasPendingImport));
            OnPropertyChanged(nameof(PendingImportLabel));
            await InitializeAsync(cancellationToken);
            BackupMessage = "Catalogue restored from backup.";
            ErrorMessage = string.Empty;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { ErrorMessage = "Could not restore the catalogue backup."; }
    }

    public void CancelImport()
    {
        _pendingImport = null;
        OnPropertyChanged(nameof(HasPendingImport));
        OnPropertyChanged(nameof(PendingImportLabel));
        BackupMessage = "Backup import cancelled.";
    }

    public async Task ImportAudioAsync(string audiobookId, string fileName, Stream source, CancellationToken cancellationToken = default)
    {
        if (_audioImportService is null) { AudioImportStatus = "Local audio import is unavailable."; return; }
        var book = _allBooks.FirstOrDefault(x => x.Id == audiobookId);
        if (book is null) { AudioImportStatus = "Select an audiobook in your catalogue first."; return; }
        try
        {
            AudioImportStatus = "Importing audio…";
            var path = await _audioImportService.ImportAsync(audiobookId, fileName, source, cancellationToken);
            var updated = book with { AudioFilePath = path };
            await _repository.UpsertAsync(updated, cancellationToken);
            _allBooks = await _repository.GetBooksAsync(cancellationToken);
            ApplyFilter(_allBooks);
            AudioImportStatus = "Audio attached to: " + book.Title;
            ErrorMessage = string.Empty;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { AudioImportStatus = "Audio import cancelled."; }
        catch (Exception) { AudioImportStatus = "Could not import this audio file. Choose a supported audio file and try again."; }
    }
    public async Task ImportCoverArtAsync(string audiobookId, string fileName, Stream source, CancellationToken cancellationToken = default)
    {
        if (_coverArtImportService is null) { AudioImportStatus = "Cover artwork import is unavailable."; return; }
        var book = _allBooks.FirstOrDefault(x => x.Id == audiobookId);
        if (book is null) { AudioImportStatus = "Select an audiobook in your catalogue first."; return; }
        try
        {
            AudioImportStatus = "Importing cover artwork…";
            var path = await _coverArtImportService.ImportAsync(audiobookId, fileName, source, cancellationToken);
            var updated = book with { CoverImagePath = path };
            await _repository.UpsertAsync(updated, cancellationToken);
            _allBooks = await _repository.GetBooksAsync(cancellationToken);
            ApplyFilter(_allBooks);
            if (SelectedBook?.Id == audiobookId) SelectBook(audiobookId);
            AudioImportStatus = "Cover artwork saved for: " + book.Title;
            ErrorMessage = string.Empty;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { AudioImportStatus = "Cover artwork import cancelled."; }
        catch (Exception) { AudioImportStatus = "Could not import this cover image. Choose a valid JPG, PNG or WebP image and try again."; }
    }

    /// <summary>Persist a safe resume position for a locally imported audiobook.</summary>
    public async Task SavePlaybackPositionAsync(string audiobookId, long positionMilliseconds, CancellationToken cancellationToken = default)
    {
        if (positionMilliseconds < 0) positionMilliseconds = 0;
        var book = _allBooks.FirstOrDefault(x => x.Id == audiobookId);
        if (book is null || string.IsNullOrWhiteSpace(book.AudioFilePath)) return;
        try
        {
            var updated = book with { PlaybackPositionMilliseconds = positionMilliseconds };
            await _repository.UpsertAsync(updated, cancellationToken);
            _allBooks = await _repository.GetBooksAsync(cancellationToken);
            ApplyFilter(_allBooks);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { AudioImportStatus = "Could not save playback position."; }
    }

    public long GetResumePositionMilliseconds(string audiobookId)
        => _allBooks.FirstOrDefault(x => x.Id == audiobookId)?.PlaybackPositionMilliseconds ?? 0;

    public async Task EnqueueAsync(string audiobookId, CancellationToken cancellationToken = default)
    {
        try { await _downloadQueue.EnqueueAsync(audiobookId, cancellationToken); await ReloadQueueAsync(cancellationToken); ErrorMessage = string.Empty; }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { ErrorMessage = "Could not update the local download queue."; }
    }

    public Task RetryFailedAsync(string audiobookId, CancellationToken cancellationToken = default) => EnqueueAsync(audiobookId, cancellationToken);

    public async Task RemoveFromQueueAsync(string audiobookId, CancellationToken cancellationToken = default)
    {
        try { await _downloadQueue.RemoveAsync(audiobookId, cancellationToken); await ReloadQueueAsync(cancellationToken); ErrorMessage = string.Empty; }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { ErrorMessage = "Could not remove the item from the local download queue."; }
    }

    private void ClearEditor() { NewTitle = string.Empty; NewAuthor = string.Empty; NewNarrator = string.Empty; NewLength = string.Empty; NewSeries = string.Empty; NewGenre = string.Empty; NewDescription = string.Empty; NewChapterTitle = string.Empty; NewChapterStart = string.Empty; }

    private async Task ReloadQueueAsync(CancellationToken cancellationToken)
    {
        var items = await _downloadQueue.GetItemsAsync(cancellationToken);
        QueueItems.Clear();
        foreach (var item in items)
        {
            var book = _allBooks.FirstOrDefault(candidate => string.Equals(candidate.Id, item.AudiobookId, StringComparison.Ordinal));
            QueueItems.Add(item with
            {
                DisplayTitle = book?.Title ?? item.AudiobookId,
                DisplayAuthor = book?.Author ?? string.Empty
            });
        }
        OnPropertyChanged(nameof(IsQueueEmpty));
        OnPropertyChanged(nameof(QueueCountLabel));
        OnPropertyChanged(nameof(QueueSummaryLabel));
    }

    private void RefreshLocalAudioBooks(IReadOnlyList<Audiobook> source)
    {
        var localBooks = source
            .Where(book => !string.IsNullOrWhiteSpace(book.AudioFilePath))
            .OrderBy(book => book.Title, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        LocalAudioBooks.Clear();
        foreach (var book in localBooks) LocalAudioBooks.Add(book);
        OnPropertyChanged(nameof(HasLocalAudioBooks));
        OnPropertyChanged(nameof(HasNoLocalAudioBooks));
        OnPropertyChanged(nameof(LocalAudioEmptyMessage));
    }

    private void ApplyFilter(IReadOnlyList<Audiobook> source)
    {
        RefreshLocalAudioBooks(source);
        var query = SearchText.Trim();
        var matches = source.Where(book =>
            (_catalogueFilterIndex == 0 ||
             (_catalogueFilterIndex == 1 && book.CanDelete) ||
             (_catalogueFilterIndex == 2 && !book.CanDelete)) &&
            (string.IsNullOrEmpty(query) ||
             book.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
             book.Author.Contains(query, StringComparison.OrdinalIgnoreCase) ||
             book.Narrator.Contains(query, StringComparison.OrdinalIgnoreCase) ||
             book.Series.Contains(query, StringComparison.OrdinalIgnoreCase) ||
             book.Genre.Contains(query, StringComparison.OrdinalIgnoreCase) ||
             book.Description.Contains(query, StringComparison.OrdinalIgnoreCase)));
        matches = _sortAscending
            ? matches.OrderBy(book => book.Title, StringComparer.OrdinalIgnoreCase)
            : matches.OrderByDescending(book => book.Title, StringComparer.OrdinalIgnoreCase);
        Books.Clear();
        foreach (var book in matches) Books.Add(book);
        OnPropertyChanged(nameof(Books));
        OnPropertyChanged(nameof(HasBooks));
        OnPropertyChanged(nameof(EmptyMessage));
    }

    private void SetField(ref string field, string value, [CallerMemberName] string? propertyName = null)
    {
        if (field == value) return;
        field = value;
        OnPropertyChanged(propertyName);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
