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
    private readonly AudiobookFileImportService? _audiobookFileImportService;
    private readonly IAudiobookDetailsProvider? _audiobookDetailsProvider;
    private readonly SemaphoreSlim _providerDetailsGate = new(1, 1);
    private string _detailMetadataStatus = string.Empty;
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private readonly SemaphoreSlim _syncGate = new(1, 1);
    private readonly LibrarySyncService? _syncService;
    private readonly ISyncStateStore? _syncStateStore;
    private readonly IDownloadSettingsStore? _downloadSettingsStore;
    private DownloadSettings _downloadSettings = new();
    private string _downloadSettingsStatus = string.Empty;
    private DownloadQueueProgress? _activeDownloadProgress;
    private bool _isDownloadQueueRunning;
    private bool _isProtectedAudibleOperationInProgress;
    private readonly Func<string>? _widevineDiagnosticFactory;
    private string _widevineDiagnostic = "Widevine check is available only in the Android app.";
    private bool _isSyncing;
    private string _syncStatus = "Provider not connected. Audible library sync is not available yet.";
    private DateTimeOffset? _lastSuccessfulSync;
    private string _searchText = string.Empty;
    private bool _isHomeSearchVisible;
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
    private Audiobook? _nowPlayingBook;
    private string _audibleSearchText = string.Empty;
    private int _selectedTabIndex;
    private bool _isDownloadsPageVisible;
    private string? _pendingQueueRemovalId;
    private string _queueRemovalConfirmationMessage = string.Empty;
    private bool _isQueueRemovalConfirmationVisible;
    private bool _isFullPlayerVisible;
    private bool _isChaptersVisible;
    private Audiobook? _menuBook;
    private Audiobook? _detailBook;
    private bool _isBookDetailsVisible;
    private string _listeningStatusFilter = "All";
    private bool _isBookMenuVisible;
    private bool _isInfoVisible;
    private bool _isSelectionMode;
    private bool _isStatusPickerVisible;
    private string[] _pendingRemovalIds = [];
    private string _removalConfirmationMessage = string.Empty;
    private bool _isRemovalConfirmationVisible;
    private bool _isAddAccountSheetVisible;
    private readonly HashSet<string> _selectedAudiobookIds = new(StringComparer.Ordinal);

    private static readonly Audiobook[] InitialSamples = [];

    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<Audiobook> Books { get; } = [];
    public ObservableCollection<Audiobook> LocalAudioBooks { get; } = [];
    public ObservableCollection<Audiobook> FilteredLocalAudioBooks { get; } = [];
    public ObservableCollection<Audiobook> AudibleBooks { get; } = [];
    public ObservableCollection<Audiobook> RecentlyPlayedBooks { get; } = [];
    public bool HasAudibleBooks => AudibleBooks.Count > 0;
    public bool HasNoAudibleBooks => !HasAudibleBooks;
    public bool HasRecentlyPlayedBooks => RecentlyPlayedBooks.Count > 0;
    public bool HasNoRecentlyPlayedBooks => !HasRecentlyPlayedBooks;
    public string AudibleLibraryEmptyMessage => HasAudibleBooks ? string.Empty : "Your Audible library will appear here after you sign in and sync.";
    public bool HasLocalAudioBooks => LocalAudioBooks.Count > 0;
    public bool HasNoLocalAudioBooks => !HasLocalAudioBooks;
    public string LocalAudioEmptyMessage => HasLocalAudioBooks ? string.Empty : "Your library is empty. Import an audiobook file to get started.";
    public ObservableCollection<DownloadQueueItem> QueueItems { get; } = [];

    public bool IsProtectedAudibleOperationInProgress => _isProtectedAudibleOperationInProgress;
    public void SetProtectedAudibleOperationInProgress(bool isRunning)
    {
        if (_isProtectedAudibleOperationInProgress == isRunning) return;
        _isProtectedAudibleOperationInProgress = isRunning;
        OnPropertyChanged(nameof(IsProtectedAudibleOperationInProgress));
    }

    public DownloadSettings CurrentDownloadSettings => _downloadSettings;
    public string DownloadDirectoryLabel => _downloadSettings.DirectoryLabel;
    public string DownloadFolderOrganizationLabel => _downloadSettings.FolderOrganizationLabel;
    public string AudibleDownloadQualityLabel => _downloadSettings.AudibleQuality == AudibleDownloadQuality.High
        ? "High quality"
        : "Normal quality";
    public string DownloadFolderOrganizationExample => _downloadSettings.FolderOrganizationExample;
    public bool HasSelectedDownloadDirectory => _downloadSettings.HasSelectedDirectory;
    public string DownloadSettingsStatus
    {
        get => _downloadSettingsStatus;
        private set
        {
            if (_downloadSettingsStatus == value) return;
            _downloadSettingsStatus = value;
            OnPropertyChanged();
        }
    }

    public async Task SetDownloadDirectoryAsync(string bookmark, string directoryName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bookmark);
        _downloadSettings = (_downloadSettings with
        {
            DirectoryBookmark = bookmark,
            DirectoryName = directoryName
        }).Normalize();
        if (_downloadSettingsStore is not null)
            await _downloadSettingsStore.SaveAsync(_downloadSettings, cancellationToken);
        RaiseDownloadSettingsChanged();
        DownloadSettingsStatus = "Download folder saved. Android storage access will be reused for future downloads.";
    }

    public async Task CycleDownloadFolderOrganizationAsync(CancellationToken cancellationToken = default)
    {
        var next = _downloadSettings.FolderOrganization switch
        {
            DownloadFolderOrganization.Flat => DownloadFolderOrganization.AuthorBook,
            DownloadFolderOrganization.AuthorBook => DownloadFolderOrganization.AuthorSeriesBook,
            _ => DownloadFolderOrganization.Flat
        };
        _downloadSettings = (_downloadSettings with { FolderOrganization = next }).Normalize();
        if (_downloadSettingsStore is not null)
            await _downloadSettingsStore.SaveAsync(_downloadSettings, cancellationToken);
        RaiseDownloadSettingsChanged();
        DownloadSettingsStatus = "File organization preference saved.";
    }

    public async Task CycleAudibleDownloadQualityAsync(CancellationToken cancellationToken = default)
    {
        var next = _downloadSettings.AudibleQuality == AudibleDownloadQuality.High
            ? AudibleDownloadQuality.Normal
            : AudibleDownloadQuality.High;
        _downloadSettings = (_downloadSettings with { AudibleQuality = next }).Normalize();
        if (_downloadSettingsStore is not null)
            await _downloadSettingsStore.SaveAsync(_downloadSettings, cancellationToken);
        RaiseDownloadSettingsChanged();
        DownloadSettingsStatus = $"Audible download quality set to {AudibleDownloadQualityLabel.ToLowerInvariant()}. Applies to all new Audible downloads.";
    }

    public void ReportDownloadSettingsStatus(string message) => DownloadSettingsStatus = message;

    private async Task LoadDownloadSettingsAsync(CancellationToken cancellationToken)
    {
        if (_downloadSettingsStore is null) return;
        try
        {
            _downloadSettings = (await _downloadSettingsStore.GetAsync(cancellationToken)).Normalize();
            RaiseDownloadSettingsChanged();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            DownloadSettingsStatus = "Download settings could not be loaded. The library is still available.";
        }
    }

    private void RaiseDownloadSettingsChanged()
    {
        OnPropertyChanged(nameof(CurrentDownloadSettings));
        OnPropertyChanged(nameof(DownloadDirectoryLabel));
        OnPropertyChanged(nameof(DownloadFolderOrganizationLabel));
        OnPropertyChanged(nameof(AudibleDownloadQualityLabel));
        OnPropertyChanged(nameof(DownloadFolderOrganizationExample));
        OnPropertyChanged(nameof(HasSelectedDownloadDirectory));
    }

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
    public Audiobook? NowPlayingBook
    {
        get => _nowPlayingBook;
        private set
        {
            if (_nowPlayingBook == value) return;
            _nowPlayingBook = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasNowPlayingBook));
            OnPropertyChanged(nameof(NowPlayingChapterCountLabel));
            OnPropertyChanged(nameof(HasNowPlayingChapters));
            OnPropertyChanged(nameof(HasNoNowPlayingChapters));
        }
    }
    public bool HasNowPlayingChapters => NowPlayingBook?.Chapters.Count > 0;
    public bool HasNoNowPlayingChapters => !HasNowPlayingChapters;
    public string NowPlayingChapterCountLabel => NowPlayingBook is null
        ? "Import an audiobook to begin listening"
        : $"{NowPlayingBook.Chapters.Count} chapters";
    public void SetNowPlayingBook(string audiobookId)
    {
        NowPlayingBook = _allBooks.FirstOrDefault(book => book.Id == audiobookId);
        RefreshRecentlyPlayedBooks();
    }

    public async Task<bool> LoadChapterMetadataAsync(string audiobookId, CancellationToken cancellationToken = default)
    {
        var book = _allBooks.FirstOrDefault(item => item.Id == audiobookId);
        if (book is null || string.IsNullOrWhiteSpace(book.AudioFilePath))
            return false;
        if (AudiobookChapterService.Normalize(book.Chapters).Count > 0)
            return true;
        if (_audiobookFileImportService is null)
        {
            AudioImportStatus = "Chapter metadata reading is unavailable on this platform.";
            return false;
        }

        try
        {
            AudioImportStatus = "Checking the imported audio file for embedded chapter markers…";
            var metadata = await _audiobookFileImportService.ReadExistingAsync(
                book.Id, book.AudioFilePath, book.Title, cancellationToken);
            var chapters = AudiobookChapterService.Normalize(metadata.Chapters);
            if (chapters.Count == 0)
            {
                AudioImportStatus = "No embedded chapter markers were found. You can add chapter markers manually in audiobook details.";
                return false;
            }

            await _repository.UpsertAsync(book with { Chapters = chapters }, cancellationToken);
            _allBooks = await _repository.GetBooksAsync(cancellationToken);
            ApplyFilter(_allBooks);
            if (SelectedBook?.Id == audiobookId)
                SelectedBook = _allBooks.FirstOrDefault(item => item.Id == audiobookId);
            if (_detailBook?.Id == audiobookId)
            {
                _detailBook = _allBooks.FirstOrDefault(item => item.Id == audiobookId);
                OnPropertyChanged(nameof(DetailBook));
                OnPropertyChanged(nameof(DetailChapterLabel));
                OnPropertyChanged(nameof(HasDetailBookChapters));
                OnPropertyChanged(nameof(HasNoDetailBookChapters));
            }
            SetNowPlayingBook(audiobookId);
            AudioImportStatus = $"Loaded {chapters.Count} embedded chapter markers for {book.Title}.";
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Chapter metadata read failed: {ex}");
            AudioImportStatus = "Could not read embedded chapter markers. Playback can continue; add markers manually if needed.";
            return false;
        }
    }

    public string AudibleSearchText
    {
        get => _audibleSearchText;
        set
        {
            if (_audibleSearchText == value) return;
            _audibleSearchText = value;
            OnPropertyChanged();
            RefreshAudibleBooks(_allBooks);
        }
    }
    public bool IsHomeNavigationSelected => _selectedTabIndex == 0;
    public bool IsHomeTab => _selectedTabIndex == 0 && !_isHomeSearchVisible && !IsFullPlayerVisible && !IsChaptersVisible && !IsBookDetailsVisible && !IsEditVisible;
    public bool IsHomeSearchVisible => _selectedTabIndex == 0 && _isHomeSearchVisible && !IsFullPlayerVisible && !IsChaptersVisible && !IsBookDetailsVisible && !IsEditVisible;
    public bool IsAudibleLibraryTab => _selectedTabIndex == 1 && !IsFullPlayerVisible && !IsChaptersVisible && !IsBookDetailsVisible && !IsEditVisible;
    public bool IsAudibleAccountTab => _selectedTabIndex == 2 && !IsFullPlayerVisible && !IsChaptersVisible && !IsBookDetailsVisible && !IsEditVisible;
    public bool IsSettingsTab => _selectedTabIndex == 3 && !IsFullPlayerVisible && !IsChaptersVisible && !IsBookDetailsVisible && !IsEditVisible;
    public bool IsSettingsHomeVisible => IsSettingsTab && !_isDownloadsPageVisible;
    public bool IsDownloadsPageVisible => IsSettingsTab && _isDownloadsPageVisible && !IsFullPlayerVisible && !IsChaptersVisible;
    public bool IsQueueRemovalConfirmationVisible => _isQueueRemovalConfirmationVisible;
    public string? PendingQueueRemovalId => _pendingQueueRemovalId;
    public string QueueRemovalConfirmationMessage => _queueRemovalConfirmationMessage;
    public bool IsFullPlayerVisible => _isFullPlayerVisible;
    public bool IsChaptersVisible => _isChaptersVisible;
    public bool IsBookDetailsVisible => _isBookDetailsVisible;
    public Audiobook? DetailBook => _detailBook;
    public bool IsDetailProviderBook => _detailBook?.IsProviderCatalogueEntry == true;
    public bool IsDetailLocalBook => _detailBook is not null && !_detailBook.IsProviderCatalogueEntry;
    public bool CanEditDetailBook => IsDetailLocalBook;
    public string DetailMetadataStatus
    {
        get => _detailMetadataStatus;
        private set
        {
            if (_detailMetadataStatus == value) return;
            _detailMetadataStatus = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DetailChapterEmptyMessage));
        }
    }
    public string DetailChapterEmptyMessage => HasDetailBookChapters
        ? string.Empty
        : IsDetailProviderBook
            ? (string.IsNullOrWhiteSpace(DetailMetadataStatus) ? "Chapter details have not been loaded yet." : DetailMetadataStatus)
            : "No chapter markers were found in this audiobook file.";
    public string DetailAudibleAvailabilityLabel => IsDetailProviderBook
        ? _detailBook!.AudibleAvailabilityLabel
        : string.Empty;
    public string DetailProtectedDownloadLabel => _detailBook?.IsWidevineOfflineAvailable == true
        ? "Encrypted Audible DASH audio is stored in VØRATH's private offline cache and ready to play."
        : !string.IsNullOrWhiteSpace(_detailBook?.ProtectedAudioFileName)
            ? $"Saved as {_detailBook.ProtectedAudioFileName} in {(_detailBook.ProtectedAudioFolderName ?? "the selected folder")}. Audible protection is preserved. Tap Download offline to cache encrypted DASH audio with a Widevine licence."
            : string.Empty;
    public bool HasPlayableDetailAudio => IsDetailProviderBook && !string.IsNullOrWhiteSpace(_detailBook?.AudioFilePath);
    public bool HasProtectedDetailDownload => IsDetailProviderBook &&
        (_detailBook?.IsWidevineOfflineAvailable == true || !string.IsNullOrWhiteSpace(_detailBook?.ProtectedAudioFileName));
    public string DetailBookDescription
    {
        get
        {
            if (IsDetailProviderBook && string.IsNullOrWhiteSpace(_detailBook?.Description))
                return "Audible did not include a description in the library response.";
            if (string.IsNullOrWhiteSpace(_detailBook?.Description))
                return "No summary was embedded in this audiobook file.";
            var decoded = System.Net.WebUtility.HtmlDecode(_detailBook.Description);
            var plainText = System.Text.RegularExpressions.Regex.Replace(decoded, "<[^>]+>", " ");
            return System.Text.RegularExpressions.Regex.Replace(plainText.Replace('\u00A0', ' '), @"\s+", " ").Trim();
        }
    }
    public string DetailFileSizeLabel => _detailBook?.FileSizeBytes > 0
        ? FormatFileSize(_detailBook.FileSizeBytes)
        : IsDetailProviderBook ? "Not supplied by Audible" : "File size unavailable";
    public string DetailDurationLabel => string.IsNullOrWhiteSpace(_detailBook?.Length) ? "Duration unavailable" : _detailBook.Length;
    public string DetailChapterLabel => HasDetailBookChapters
        ? $"{_detailBook!.Chapters.Count} chapters"
        : "Not available";
    public bool HasDetailBookChapters => (_detailBook?.Chapters.Count ?? 0) > 0;
    public bool HasNoDetailBookChapters => !HasDetailBookChapters;
    public int AllCount => LocalAudioBooks.Count;
    public int NotStartedCount => LocalAudioBooks.Count(book => book.ListeningStatus == "Not started");
    public int InProgressCount => LocalAudioBooks.Count(book => book.ListeningStatus == "In progress");
    public int FinishedCount => LocalAudioBooks.Count(book => book.ListeningStatus == "Finished");
    public string AllTabLabel => $"All ({AllCount})";
    public string NotStartedTabLabel => $"Not started ({NotStartedCount})";
    public string InProgressTabLabel => $"In progress ({InProgressCount})";
    public string FinishedTabLabel => $"Finished ({FinishedCount})";
    public bool IsAllFilter => _listeningStatusFilter == "All";
    public bool IsNotStartedFilter => _listeningStatusFilter == "Not started";
    public bool IsInProgressFilter => _listeningStatusFilter == "In progress";
    public bool IsFinishedFilter => _listeningStatusFilter == "Finished";
    public string ListeningStatusFilter => _listeningStatusFilter;
    public void SetListeningStatusFilter(string status)
    {
        if (status is not ("All" or "Not started" or "In progress" or "Finished")) return;
        if (_listeningStatusFilter == status) return;
        _listeningStatusFilter = status;
        RefreshFilteredLocalAudioBooks();
        OnPropertyChanged(nameof(ListeningStatusFilter));
        OnPropertyChanged(nameof(IsAllFilter));
        OnPropertyChanged(nameof(IsNotStartedFilter));
        OnPropertyChanged(nameof(IsInProgressFilter));
        OnPropertyChanged(nameof(IsFinishedFilter));
    }
    public void OpenBookDetails(string audiobookId)
    {
        _detailBook = _allBooks.FirstOrDefault(book => book.Id == audiobookId);
        if (_detailBook is null) return;
        DetailMetadataStatus = string.Empty;
        _isBookDetailsVisible = true;
        _isBookMenuVisible = false;
        _isInfoVisible = false;
        RaiseNavigationState();
        OnPropertyChanged(nameof(IsBookMenuVisible));
        OnPropertyChanged(nameof(IsInfoVisible));
        OnPropertyChanged(nameof(DetailBook));
        OnPropertyChanged(nameof(DetailMetadataStatus));
        OnPropertyChanged(nameof(DetailChapterEmptyMessage));
        OnPropertyChanged(nameof(IsBookDetailsVisible));
        OnPropertyChanged(nameof(IsDetailProviderBook));
        OnPropertyChanged(nameof(IsDetailLocalBook));
        OnPropertyChanged(nameof(CanEditDetailBook));
        OnPropertyChanged(nameof(DetailAudibleAvailabilityLabel));
        OnPropertyChanged(nameof(DetailProtectedDownloadLabel));
        OnPropertyChanged(nameof(HasPlayableDetailAudio));
        OnPropertyChanged(nameof(HasProtectedDetailDownload));
        OnPropertyChanged(nameof(DetailBookDescription));
        OnPropertyChanged(nameof(DetailFileSizeLabel));
        OnPropertyChanged(nameof(DetailDurationLabel));
        OnPropertyChanged(nameof(DetailChapterLabel));
        OnPropertyChanged(nameof(HasDetailBookChapters));
        OnPropertyChanged(nameof(HasNoDetailBookChapters));
    }
    public async Task LoadProviderBookDetailsAsync(string audiobookId, CancellationToken cancellationToken = default)
    {
        if (!audiobookId.StartsWith("provider:", StringComparison.Ordinal))
            return;

        await _providerDetailsGate.WaitAsync(cancellationToken);
        try
        {
            var current = _allBooks.FirstOrDefault(book => book.Id == audiobookId);
            if (current is null)
                return;
            if (current.FileSizeBytes > 0 && current.Chapters.Count > 0)
            {
                DetailMetadataStatus = $"Loaded {current.Chapters.Count} chapters and {FormatFileSize(current.FileSizeBytes)}.";
                return;
            }
            if (_audiobookDetailsProvider is null)
            {
                DetailMetadataStatus = "Audible chapter and file-size lookup is unavailable in this build.";
                return;
            }

            DetailMetadataStatus = "Loading chapter and file-size details from Audible…";
            var providerId = audiobookId["provider:".Length..];
            var metadata = await _audiobookDetailsProvider.GetDetailsAsync(providerId, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (metadata is null)
            {
                DetailMetadataStatus = "Audible did not return chapter or file-size metadata for this title.";
                return;
            }

            var chapters = AudiobookChapterService.Normalize(metadata.Chapters);
            var updated = current with
            {
                Chapters = chapters.Count > 0 ? chapters : current.Chapters,
                FileSizeBytes = metadata.FileSizeBytes > 0 ? metadata.FileSizeBytes : current.FileSizeBytes
            };
            if (updated != current)
            {
                await _repository.UpsertAsync(updated, cancellationToken);
                _allBooks = await _repository.GetBooksAsync(cancellationToken);
                ApplyFilter(_allBooks);
                RefreshAudibleBooks(_allBooks);
            }

            if (_detailBook?.Id == audiobookId)
            {
                _detailBook = _allBooks.FirstOrDefault(book => book.Id == audiobookId);
                OnPropertyChanged(nameof(DetailBook));
                OnPropertyChanged(nameof(DetailFileSizeLabel));
                OnPropertyChanged(nameof(DetailChapterLabel));
                OnPropertyChanged(nameof(HasDetailBookChapters));
                OnPropertyChanged(nameof(HasNoDetailBookChapters));
            }

            var statusParts = new List<string>();
            statusParts.Add(chapters.Count > 0
                ? $"Loaded {chapters.Count} chapters."
                : "Audible did not provide chapter markers.");
            statusParts.Add(updated.FileSizeBytes > 0
                ? $"Audio size: {FormatFileSize(updated.FileSizeBytes)}."
                : "Audio size was not supplied.");
            DetailMetadataStatus = string.Join(" ", statusParts);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Audible detail metadata request failed: {ex}");
            DetailMetadataStatus = "Could not load Audible chapter and file-size details. Try opening this title again.";
        }
        finally
        {
            _providerDetailsGate.Release();
        }
    }

    private static string FormatFileSize(long bytes)
    {
        const double bytesPerMegabyte = 1024d * 1024d;
        return bytes >= bytesPerMegabyte
            ? $"{bytes / bytesPerMegabyte:0.0} MB"
            : $"{bytes / 1024d:0} KB";
    }

    public void CloseBookDetails()
    {
        _isBookDetailsVisible = false;
        RaiseNavigationState();
        OnPropertyChanged(nameof(IsBookDetailsVisible));
    }
    public void BeginDetailBookEdit()
    {
        if (_detailBook is null || _detailBook.IsProviderCatalogueEntry) return;
        BeginEdit(_detailBook.Id);
        if (IsEditing) { _isBookDetailsVisible = false; _isEditVisible = true; RaiseNavigationState(); OnPropertyChanged(nameof(IsEditVisible)); }
    }
    private bool _isEditVisible;
    public bool IsEditVisible => _isEditVisible;
    public void CancelDetailBookEdit()
    {
        CancelEdit();
        _isEditVisible = false;
        _isBookDetailsVisible = _detailBook is not null;
        RaiseNavigationState();
        OnPropertyChanged(nameof(IsEditVisible));
        OnPropertyChanged(nameof(IsBookDetailsVisible));
    }
    public async Task SaveDetailBookEditAsync(CancellationToken cancellationToken = default)
    {
        if (!IsEditing) return;
        await AddBookAsync(cancellationToken);
        if (!HasError)
        {
            _detailBook = _allBooks.FirstOrDefault(book => book.Id == _detailBook?.Id);
            _isEditVisible = false;
            _isBookDetailsVisible = _detailBook is not null;
            RaiseNavigationState();
            OnPropertyChanged(nameof(IsEditVisible));
            OnPropertyChanged(nameof(IsBookDetailsVisible));
            OnPropertyChanged(nameof(DetailBook));
            OnPropertyChanged(nameof(DetailBookDescription));
            OnPropertyChanged(nameof(DetailDurationLabel));
            OnPropertyChanged(nameof(DetailChapterLabel));
        }
    }
    public Audiobook? MenuBook => _menuBook;
    public bool IsBookMenuVisible => _isBookMenuVisible;
    public bool IsInfoVisible => _isInfoVisible;
    public bool IsSelectionMode => _isSelectionMode;
    public bool IsStatusPickerVisible => _isStatusPickerVisible;
    public bool IsRemovalConfirmationVisible => _isRemovalConfirmationVisible;
    public bool IsAddAccountSheetVisible => _isAddAccountSheetVisible;
    public string RemovalConfirmationMessage => _removalConfirmationMessage;
    public bool HasSelectedAudiobooks => _selectedAudiobookIds.Count > 0;
    public string SelectedAudiobookCountLabel => $"{_selectedAudiobookIds.Count} selected";
    public bool IsMenuBookSelected => _menuBook is not null && _selectedAudiobookIds.Contains(_menuBook.Id);
    public bool IsAudiobookSelected(string audiobookId) => _selectedAudiobookIds.Contains(audiobookId);
    public string MenuBookDescription => string.IsNullOrWhiteSpace(_menuBook?.Description) ? "No description was embedded in this audiobook file." : _menuBook.Description;
    public string MenuBookMetadataLabel => _menuBook is null ? string.Empty :
        $"{_menuBook.Author}\n{(string.IsNullOrWhiteSpace(_menuBook.Narrator) ? "Narrator not provided" : _menuBook.Narrator)}\n{(string.IsNullOrWhiteSpace(_menuBook.Length) ? "Duration not provided" : _menuBook.Length)}\n{_menuBook.Chapters.Count} chapters";
    public string MenuBookStatus => _menuBook?.ListeningStatus ?? "Not started";
    public string MenuBookStatusColor => MenuBookStatus switch { "Finished" => "#2EAD65", "In progress" => "#E34B4B", _ => "#E69A21" };
    public void OpenAddAccountSheet()
    {
        if (_isAddAccountSheetVisible) return;
        _isAddAccountSheetVisible = true;
        OnPropertyChanged(nameof(IsAddAccountSheetVisible));
    }

    public void CloseAddAccountSheet()
    {
        if (!_isAddAccountSheetVisible) return;
        _isAddAccountSheetVisible = false;
        OnPropertyChanged(nameof(IsAddAccountSheetVisible));
    }

    public void OpenBookMenu(string audiobookId)
    {
        _menuBook = _allBooks.FirstOrDefault(book => book.Id == audiobookId && !book.Id.StartsWith("provider:", StringComparison.Ordinal));
        _isBookMenuVisible = _menuBook is not null;
        OnPropertyChanged(nameof(MenuBook));
        OnPropertyChanged(nameof(IsBookMenuVisible));
        OnPropertyChanged(nameof(MenuBookDescription));
        OnPropertyChanged(nameof(MenuBookMetadataLabel));
        OnPropertyChanged(nameof(MenuBookStatus));
        OnPropertyChanged(nameof(MenuBookStatusColor));
    }
    public void CloseBookMenu() { _isBookMenuVisible = false; OnPropertyChanged(nameof(IsBookMenuVisible)); }
    public void OpenBookInfo()
    {
        if (_menuBook is null) return;
        OpenBookDetails(_menuBook.Id);
    }
    public void CloseBookInfo() { _isInfoVisible = false; OnPropertyChanged(nameof(IsInfoVisible)); }
    public void BeginSelectionMode()
    {
        _isBookMenuVisible = false;
        _isSelectionMode = true;
        _selectedAudiobookIds.Clear();
        if (_menuBook is not null) _selectedAudiobookIds.Add(_menuBook.Id);
        RaiseSelectionState();
        RefreshLocalAudioBooks(_allBooks);
        OnPropertyChanged(nameof(IsBookMenuVisible));
    }
    public void ToggleAudiobookSelection(string audiobookId)
    {
        if (!_isSelectionMode) return;
        if (!_selectedAudiobookIds.Add(audiobookId)) _selectedAudiobookIds.Remove(audiobookId);
        RaiseSelectionState();
        RefreshLocalAudioBooks(_allBooks);
    }
    public void SelectAllLocalAudiobooks()
    {
        foreach (var book in LocalAudioBooks) _selectedAudiobookIds.Add(book.Id);
        RaiseSelectionState();
        RefreshLocalAudioBooks(_allBooks);
    }
    public void ExitSelectionMode()
    {
        _isSelectionMode = false;
        _isStatusPickerVisible = false;
        _selectedAudiobookIds.Clear();
        RaiseSelectionState();
        RefreshLocalAudioBooks(_allBooks);
        OnPropertyChanged(nameof(IsStatusPickerVisible));
    }
    public void OpenStatusPicker() { if (!_selectedAudiobookIds.Any()) return; _isStatusPickerVisible = true; OnPropertyChanged(nameof(IsStatusPickerVisible)); }
    public void CloseStatusPicker() { _isStatusPickerVisible = false; OnPropertyChanged(nameof(IsStatusPickerVisible)); }
    public void RequestRemoveBook(string audiobookId)
    {
        var book = _allBooks.FirstOrDefault(item => item.Id == audiobookId && item.CanDelete);
        if (book is null) return;
        _pendingRemovalIds = [book.Id];
        _removalConfirmationMessage = $"Remove \"{book.Title}\" from your library?";
        _isRemovalConfirmationVisible = true;
        OnPropertyChanged(nameof(RemovalConfirmationMessage));
        OnPropertyChanged(nameof(IsRemovalConfirmationVisible));
    }

    public void RequestRemoveSelectedAudiobooks()
    {
        var selected = _allBooks.Where(book => book.CanDelete && _selectedAudiobookIds.Contains(book.Id)).ToArray();
        if (selected.Length == 0) return;
        _pendingRemovalIds = selected.Select(book => book.Id).ToArray();
        _removalConfirmationMessage = $"Remove {selected.Length} selected audiobook(s) from your library?";
        _isRemovalConfirmationVisible = true;
        OnPropertyChanged(nameof(RemovalConfirmationMessage));
        OnPropertyChanged(nameof(IsRemovalConfirmationVisible));
    }

    public void CancelPendingRemoval()
    {
        _pendingRemovalIds = [];
        _removalConfirmationMessage = string.Empty;
        _isRemovalConfirmationVisible = false;
        OnPropertyChanged(nameof(RemovalConfirmationMessage));
        OnPropertyChanged(nameof(IsRemovalConfirmationVisible));
    }

    public async Task ConfirmPendingRemovalAsync(CancellationToken cancellationToken = default)
    {
        if (!_isRemovalConfirmationVisible || _pendingRemovalIds.Length == 0) return;
        var ids = _pendingRemovalIds.ToArray();
        CancelPendingRemoval();
        foreach (var id in ids)
            await RemoveBookAsync(id, cancellationToken);
        if (_isSelectionMode) ExitSelectionMode();
        AudioImportStatus = $"Removed {ids.Length} audiobook(s).";
    }

    public async Task ApplyListeningStatusAsync(string status, CancellationToken cancellationToken = default)
    {
        if (status is not ("Finished" or "In progress" or "Not started")) return;
        try
        {
            var selected = _allBooks.Where(book => _selectedAudiobookIds.Contains(book.Id)).ToArray();
            foreach (var book in selected)
                await _repository.UpsertAsync(book with { ListeningStatus = status }, cancellationToken);
            _allBooks = await _repository.GetBooksAsync(cancellationToken);
            ApplyFilter(_allBooks);
            ExitSelectionMode();
            AudioImportStatus = $"Updated {selected.Length} audiobook(s): {status}.";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { ErrorMessage = "Could not update listening status for the selected audiobooks."; }
    }
    public async Task RemoveSelectedAudiobooksAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var selected = _selectedAudiobookIds.ToArray();
            foreach (var id in selected) await _repository.RemoveAsync(id, cancellationToken);
            _allBooks = await _repository.GetBooksAsync(cancellationToken);
            ApplyFilter(_allBooks);
            ExitSelectionMode();
            AudioImportStatus = $"Removed {selected.Length} audiobook(s).";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { ErrorMessage = "Could not remove the selected audiobooks."; }
    }
    private void RaiseSelectionState()
    {
        OnPropertyChanged(nameof(IsSelectionMode));
        OnPropertyChanged(nameof(HasSelectedAudiobooks));
        OnPropertyChanged(nameof(SelectedAudiobookCountLabel));
        OnPropertyChanged(nameof(IsMenuBookSelected));
    }

    public bool HasNowPlayingBook => NowPlayingBook is not null;

    /// <summary>Handles one Android system Back press without tearing down the activity or player.</summary>
    public bool TryNavigateBack()
    {
        if (_isAddAccountSheetVisible) { CloseAddAccountSheet(); return true; }
        if (IsQueueRemovalConfirmationVisible) { CancelQueueRemoval(); return true; }
        if (IsDownloadsPageVisible) { CloseDownloadsPage(); return true; }
        if (IsRemovalConfirmationVisible) { CancelPendingRemoval(); return true; }
        if (_isStatusPickerVisible) { CloseStatusPicker(); return true; }
        if (_isBookMenuVisible) { CloseBookMenu(); return true; }
        if (_isSelectionMode) { ExitSelectionMode(); return true; }
        if (_isChaptersVisible) { CloseChapters(); return true; }
        if (_isFullPlayerVisible) { CloseFullPlayer(); return true; }
        if (_isEditVisible) { CancelDetailBookEdit(); return true; }
        if (_isBookDetailsVisible) { CloseBookDetails(); return true; }
        if (_isInfoVisible) { CloseBookInfo(); return true; }
        if (_isHomeSearchVisible) { CloseHomeSearch(); return true; }
        if (_selectedTabIndex == 1 && !string.IsNullOrWhiteSpace(AudibleSearchText))
        {
            AudibleSearchText = string.Empty;
            return true;
        }
        if (_selectedTabIndex != 0) { SelectTab(0); return true; }
        return false;
    }

    public void OpenHomeSearch()
    {
        if (_selectedTabIndex != 0 || _isHomeSearchVisible) return;
        _isHomeSearchVisible = true;
        RaiseNavigationState();
    }

    public void CloseHomeSearch()
    {
        if (!_isHomeSearchVisible) return;
        _isHomeSearchVisible = false;
        RaiseNavigationState();
    }

    public void SelectTab(int index)
    {
        _selectedTabIndex = Math.Clamp(index, 0, 3);
        _isDownloadsPageVisible = false;
        _isHomeSearchVisible = false;
        _isFullPlayerVisible = false;
        _isChaptersVisible = false;
        RaiseNavigationState();
    }
    public void OpenDownloadsPage()
    {
        if (_selectedTabIndex != 3 || _isDownloadsPageVisible) return;
        _isDownloadsPageVisible = true;
        RaiseNavigationState();
    }

    public void CloseDownloadsPage()
    {
        if (!_isDownloadsPageVisible) return;
        _isDownloadsPageVisible = false;
        RaiseNavigationState();
    }

    public void RequestQueueRemoval(string audiobookId, string displayTitle)
    {
        if (string.IsNullOrWhiteSpace(audiobookId) ||
            QueueItems.FirstOrDefault(item => item.AudiobookId == audiobookId)?.CanRemove != true)
            return;

        _pendingQueueRemovalId = audiobookId;
        _queueRemovalConfirmationMessage =
            $"Remove “{displayTitle}” from this list? This only removes the queue entry. It does not delete the audiobook file or any partial download data.";
        _isQueueRemovalConfirmationVisible = true;
        OnPropertyChanged(nameof(QueueRemovalConfirmationMessage));
        OnPropertyChanged(nameof(IsQueueRemovalConfirmationVisible));
    }

    public void CancelQueueRemoval()
    {
        _pendingQueueRemovalId = null;
        _isQueueRemovalConfirmationVisible = false;
        OnPropertyChanged(nameof(IsQueueRemovalConfirmationVisible));
    }

    public async Task ConfirmQueueRemovalAsync(CancellationToken cancellationToken = default)
    {
        var audiobookId = _pendingQueueRemovalId;
        if (string.IsNullOrWhiteSpace(audiobookId)) return;
        await RemoveFromQueueAsync(audiobookId, cancellationToken);
        _pendingQueueRemovalId = null;
        _isQueueRemovalConfirmationVisible = false;
        OnPropertyChanged(nameof(IsQueueRemovalConfirmationVisible));
    }

    public void OpenFullPlayer()
    {
        if (NowPlayingBook is null) return;
        _isChaptersVisible = false;
        _isFullPlayerVisible = true;
        RaiseNavigationState();
    }
    public void OpenChapters()
    {
        if (NowPlayingBook is null) return;
        _isFullPlayerVisible = false;
        _isChaptersVisible = true;
        RaiseNavigationState();
    }
    public void CloseFullPlayer()
    {
        _isFullPlayerVisible = false;
        RaiseNavigationState();
    }
    public void CloseChapters()
    {
        _isChaptersVisible = false;
        _isFullPlayerVisible = true;
        RaiseNavigationState();
    }
    private void RaiseNavigationState()
    {
        OnPropertyChanged(nameof(IsHomeTab));
        OnPropertyChanged(nameof(IsHomeNavigationSelected));
        OnPropertyChanged(nameof(IsHomeSearchVisible));
        OnPropertyChanged(nameof(IsAudibleLibraryTab));
        OnPropertyChanged(nameof(IsAudibleAccountTab));
        OnPropertyChanged(nameof(IsSettingsTab));
        OnPropertyChanged(nameof(IsSettingsHomeVisible));
        OnPropertyChanged(nameof(IsDownloadsPageVisible));
        OnPropertyChanged(nameof(IsFullPlayerVisible));
        OnPropertyChanged(nameof(IsChaptersVisible));
        OnPropertyChanged(nameof(IsBookDetailsVisible));
        OnPropertyChanged(nameof(IsEditVisible));
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

    public MainViewModel(ILibraryRepository repository, IDownloadQueue downloadQueue, LibrarySyncService? syncService = null, LocalAudioImportService? audioImportService = null, CoverArtImportService? coverArtImportService = null, ISyncStateStore? syncStateStore = null, Func<string>? widevineDiagnosticFactory = null, AudiobookFileImportService? audiobookFileImportService = null, IAudiobookDetailsProvider? audiobookDetailsProvider = null, IDownloadSettingsStore? downloadSettingsStore = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _downloadQueue = downloadQueue ?? throw new ArgumentNullException(nameof(downloadQueue));
        _syncService = syncService;
        _audioImportService = audioImportService;
        _coverArtImportService = coverArtImportService;
        _audiobookFileImportService = audiobookFileImportService;
        _audiobookDetailsProvider = audiobookDetailsProvider;
        _syncStateStore = syncStateStore;
        _downloadSettingsStore = downloadSettingsStore;
        _widevineDiagnosticFactory = widevineDiagnosticFactory;
    }

    public string WidevineDiagnostic { get => _widevineDiagnostic; private set { if (_widevineDiagnostic == value) return; _widevineDiagnostic = value; OnPropertyChanged(); } }
    public bool CanProbeWidevine => _widevineDiagnosticFactory is not null;
    public void ProbeWidevine()
    {
        if (_widevineDiagnosticFactory is null) { WidevineDiagnostic = "Widevine check is available only in the Android app."; return; }
        try { WidevineDiagnostic = _widevineDiagnosticFactory(); }
        catch (Exception ex) { WidevineDiagnostic = $"Widevine check failed ({ex.GetType().Name})."; }
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
            RefreshAudibleBooks(_allBooks);
            await ReloadQueueAsync(cancellationToken);
            LastSuccessfulSync = DateTimeOffset.UtcNow;
            var timestampSaved = true;
            if (_syncStateStore is not null)
            {
                try { await _syncStateStore.SaveLastSuccessfulSyncAsync(LastSuccessfulSync.Value, cancellationToken); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception) { timestampSaved = false; }
            }
            SyncStatus = timestampSaved
                ? $"Sync complete. {count} audiobook metadata entr{(count == 1 ? "y" : "ies")} updated."
                : $"Sync complete. {count} audiobook metadata entr{(count == 1 ? "y" : "ies")} updated, but the timestamp could not be saved.";
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
    public string EditorTitle => IsEditing ? "Edit audiobook" : "Add an audiobook";
    public string SaveButtonLabel => IsEditing ? "Save changes" : "Save to local catalogue";
    public string CatalogueSummaryLabel => $"{Books.Count} of {_allBooks.Count} audiobooks";

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
    public void ReportSyncStatus(string message) => SyncStatus = message;
    public string SortLabel => _sortAscending ? "Sort: A-Z" : "Sort: Z-A";
    public string FilterLabel => _catalogueFilterIndex switch { 1 => "Filter: Manual", 2 => "Filter: Demo", _ => "Filter: All" };
    public void ToggleSort() { _sortAscending = !_sortAscending; OnPropertyChanged(nameof(SortLabel)); ApplyFilter(_allBooks); }
    public void CycleCatalogueFilter() { _catalogueFilterIndex = (_catalogueFilterIndex + 1) % 3; OnPropertyChanged(nameof(FilterLabel)); ApplyFilter(_allBooks); }
    public string BackupMessage { get => _backupMessage; private set { if (_backupMessage == value) return; _backupMessage = value; OnPropertyChanged(); } }
    public bool HasPendingImport => _pendingImport is not null;
    public string PendingImportLabel => _pendingImport is null ? string.Empty : "Backup contains " + _pendingImport.Count + " entries. Confirm to replace the current catalogue.";
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool IsQueueEmpty => QueueItems.Count == 0;
    public bool HasPendingDownloadItems => QueueItems.Any(x => x.Status == DownloadStatus.Queued && !x.AudiobookId.StartsWith("provider:", StringComparison.Ordinal));
    public string QueueCountLabel => $"Download queue ({QueueItems.Count})";
    public string QueueSummaryLabel
    {
        get
        {
            var queued = QueueItems.Count(x => x.Status == DownloadStatus.Queued);
            var downloading = QueueItems.Count(x => x.Status == DownloadStatus.Downloading);
            var completed = QueueItems.Count(x => x.Status == DownloadStatus.Completed);
            var protectedCount = QueueItems.Count(x => x.Status == DownloadStatus.Protected);
            var paused = QueueItems.Count(x => x.Status == DownloadStatus.Paused);
            var failed = QueueItems.Count(x => x.Status == DownloadStatus.Failed);
            var completedLabel = $"{completed} completed" +
                (protectedCount > 0 ? $" · {protectedCount} protected" : string.Empty);
            return $"{queued} queued · {downloading} downloading · {paused} paused · {completedLabel} · {failed} failed";
        }
    }
    public bool IsDownloadQueueRunning => _isDownloadQueueRunning;
    public string DownloadQueueActionLabel => IsDownloadQueueRunning ? "Pause downloads" : "Start pending downloads";
    public bool HasActiveDownloadProgress => _activeDownloadProgress is not null;
    public string ActiveDownloadProgressTitle => _activeDownloadProgress is null
        ? string.Empty
        : QueueItems.FirstOrDefault(item => item.AudiobookId == _activeDownloadProgress.AudiobookId)?.DisplayTitle
            ?? "Downloading audiobook";
    public string ActiveDownloadProgressLabel => _activeDownloadProgress is null
        ? string.Empty
        : _activeDownloadProgress.Percentage is { } percentage
            ? $"{percentage:0}% · {FormatDownloadBytes(_activeDownloadProgress.BytesReceived)} of {FormatDownloadBytes(_activeDownloadProgress.TotalBytes ?? 0)}"
            : $"{FormatDownloadBytes(_activeDownloadProgress.BytesReceived)} downloaded";
    public double ActiveDownloadProgressPercent => _activeDownloadProgress?.Percentage ?? 0d;
    public string EmptyMessage => Books.Count > 0 ? string.Empty :
        _allBooks.Count == 0 ? "Your local catalogue is empty." : "No matching audiobooks. Try another title, author or narrator.";

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _loadGate.WaitAsync(cancellationToken);
        try
        {
            IsLoading = true;
            ErrorMessage = string.Empty;
            await _repository.EnsureInitializedAsync(InitialSamples, cancellationToken);
            await LoadDownloadSettingsAsync(cancellationToken);
            if (_syncStateStore is not null)
                LastSuccessfulSync = await _syncStateStore.GetLastSuccessfulSyncAsync(cancellationToken);
            var stored = await _repository.GetBooksAsync(cancellationToken);
            // Remove only the three seeded demo rows from older installations.
            foreach (var demo in stored.Where(book => book.Status == "Demo" || book.Id.StartsWith("demo-", StringComparison.Ordinal)).ToArray())
                await _repository.RemoveAsync(demo.Id, cancellationToken);
            _allBooks = await _repository.GetBooksAsync(cancellationToken);
            ApplyFilter(_allBooks);
            RefreshAudibleBooks(_allBooks);
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
        if (book is null || !book.CanDelete || book.IsProviderCatalogueEntry) return;
        _editingBookId = book.Id;
        NewTitle = book.Title;
        NewAuthor = book.Author;
        NewNarrator = book.Narrator;
        NewLength = book.Length;
        NewSeries = book.Series;
        NewGenre = book.Genre;
        NewDescription = book.Description;
        OnPropertyChanged(nameof(IsEditing));
        OnPropertyChanged(nameof(EditorTitle));
        OnPropertyChanged(nameof(SaveButtonLabel));
        ErrorMessage = string.Empty;
    }

    public void CancelEdit()
    {
        _editingBookId = null;
        ClearEditor();
        OnPropertyChanged(nameof(IsEditing));
        OnPropertyChanged(nameof(EditorTitle));
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
            OnPropertyChanged(nameof(EditorTitle));
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
            if (_editingBookId == audiobookId)
            {
                _editingBookId = null;
                ClearEditor();
                OnPropertyChanged(nameof(IsEditing));
                OnPropertyChanged(nameof(EditorTitle));
                OnPropertyChanged(nameof(SaveButtonLabel));
                OnPropertyChanged(nameof(IsEditVisible));
            }
            if (SelectedBook?.Id == audiobookId) SelectedBook = null;
            if (_detailBook?.Id == audiobookId)
            {
                _detailBook = null;
                _isBookDetailsVisible = false;
                _isEditVisible = false;
                OnPropertyChanged(nameof(DetailBook));
                OnPropertyChanged(nameof(IsBookDetailsVisible));
                OnPropertyChanged(nameof(DetailBookDescription));
            }
            if (_menuBook?.Id == audiobookId)
            {
                _menuBook = null;
                _isBookMenuVisible = false;
                _isInfoVisible = false;
                OnPropertyChanged(nameof(MenuBook));
                OnPropertyChanged(nameof(IsBookMenuVisible));
                OnPropertyChanged(nameof(IsInfoVisible));
            }
            _selectedAudiobookIds.Remove(audiobookId);
            RaiseSelectionState();
            RaiseNavigationState();
            await _repository.RemoveAsync(audiobookId, cancellationToken);
            _allBooks = await _repository.GetBooksAsync(cancellationToken);
            ApplyFilter(_allBooks);
            RefreshFilteredLocalAudioBooks();
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
            ErrorMessage = "Could not read this backup file. Check that it is a valid catalogue backup and try again.";
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

    public async Task ImportAudiobookAsync(string fileName, Stream source, CancellationToken cancellationToken = default)
    {
        if (_audiobookFileImportService is null)
        {
            AudioImportStatus = "Audiobook file import is unavailable on this platform.";
            return;
        }

        var id = Guid.NewGuid().ToString("N");
        try
        {
            AudioImportStatus = "Importing audiobook and reading its embedded metadata…";
            var imported = await _audiobookFileImportService.ImportAsync(id, fileName, source, cancellationToken);
            var title = string.IsNullOrWhiteSpace(imported.Title)
                ? Path.GetFileNameWithoutExtension(fileName) : imported.Title;
            var book = new Audiobook(id, title, imported.Author, imported.Narrator, imported.Length, "Imported")
            {
                AudioFilePath = imported.AudioFilePath,
                CoverImagePath = imported.CoverImagePath,
                Description = imported.Description,
                FileSizeBytes = imported.FileSizeBytes,
                Chapters = imported.Chapters
            };
            await _repository.UpsertAsync(book, cancellationToken);
            _allBooks = await _repository.GetBooksAsync(cancellationToken);
            ApplyFilter(_allBooks);
            AudioImportStatus = imported.Chapters.Count > 0
                ? $"Imported {title}. Found {imported.Chapters.Count} chapters and {(imported.CoverImagePath is null ? "no embedded cover" : "embedded cover artwork")}."
                : $"Imported {title}. {(imported.CoverImagePath is null ? "No embedded cover artwork was found." : "Embedded cover artwork was imported.")} Chapter markers were not present in the file.";
            ErrorMessage = string.Empty;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            AudioImportStatus = "Audiobook import cancelled.";
        }
        catch (Exception)
        {
            AudioImportStatus = "Could not import this audiobook. Check that the file is a supported audio format and try again.";
        }
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

    /// <summary>Automatically move a locally playable audiobook into the In progress state when playback begins.</summary>
    public async Task MarkPlaybackStartedAsync(string audiobookId, CancellationToken cancellationToken = default)
    {
        var book = _allBooks.FirstOrDefault(x => x.Id == audiobookId);
        if (book is null || string.IsNullOrWhiteSpace(book.AudioFilePath) || book.ListeningStatus == "In progress")
            return;

        try
        {
            await _repository.UpsertAsync(book with { ListeningStatus = "In progress" }, cancellationToken);
            _allBooks = await _repository.GetBooksAsync(cancellationToken);
            ApplyFilter(_allBooks);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            AudioImportStatus = "Playback started, but listening progress could not be saved.";
        }
    }

    public long GetResumePositionMilliseconds(string audiobookId)
        => _allBooks.FirstOrDefault(x => x.Id == audiobookId)?.PlaybackPositionMilliseconds ?? 0;

    public async Task EnqueueAsync(string audiobookId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(audiobookId))
            return;

        try
        {
            if (audiobookId.StartsWith("provider:", StringComparison.Ordinal))
            {
                var providerTitle = _allBooks.FirstOrDefault(book => book.Id == audiobookId)?.Title
                    ?? "Audible audiobook";
                await _downloadQueue.EnqueueProviderAsync(audiobookId, providerTitle, cancellationToken);
            }
            else
            {
                await _downloadQueue.EnqueueAsync(audiobookId, cancellationToken);
            }
            await ReloadQueueAsync(cancellationToken);
            ErrorMessage = string.Empty;
            DownloadSettingsStatus = audiobookId.StartsWith("provider:", StringComparison.Ordinal)
                ? "Added to the Audible download queue. VØRATH will request an Audible-issued offline licence and save the granted file to your selected folder."
                : "Added to the download queue.";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { ErrorMessage = "Could not update the local download queue."; }
    }

    public async Task EnqueueAudibleDownloadAsync(
        string audiobookId,
        string displayTitle,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(audiobookId) ||
            !audiobookId.StartsWith("provider:", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(displayTitle))
            return;

        try
        {
            await _downloadQueue.EnqueueProviderAsync(audiobookId, displayTitle, cancellationToken);
            await ReloadQueueAsync(cancellationToken);
            ErrorMessage = string.Empty;
            DownloadSettingsStatus = "Added to the Audible download queue. Downloads will run one at a time.";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            ErrorMessage = "Could not add this Audible title to the persistent download queue.";
            DownloadSettingsStatus = ErrorMessage;
        }
    }

    public async Task EnqueueAuthorizedDownloadAsync(
        string audiobookId,
        string sourceUrl,
        CancellationToken cancellationToken = default)
    {
        var book = _allBooks.FirstOrDefault(item => string.Equals(item.Id, audiobookId, StringComparison.Ordinal));
        if (book is null || !book.CanQueueDownload)
        {
            DownloadSettingsStatus = "Only local audiobook entries can use a direct audio URL. Audible titles stay in the official Audible app.";
            return;
        }

        if (!_downloadSettings.HasSelectedDirectory)
        {
            DownloadSettingsStatus = "Choose a download folder in Settings before starting a download.";
            return;
        }

        try
        {
            var uri = AuthorizedAudioDownloadUrl.Parse(sourceUrl);
            var existing = QueueItems.FirstOrDefault(item => item.AudiobookId == audiobookId);
            if (existing?.Status == DownloadStatus.Completed)
            {
                DownloadSettingsStatus = "This audiobook is already downloaded. Remove its completed queue entry before downloading it again.";
                return;
            }

            await _downloadQueue.EnqueueAuthorizedAsync(
                audiobookId, uri.AbsoluteUri, book.Title, cancellationToken);
            await ReloadQueueAsync(cancellationToken);
            DownloadSettingsStatus = "Added to the download queue. The direct URL is kept only in the app-private queue and is not included in backups.";
            ErrorMessage = string.Empty;
        }
        catch (ArgumentException exception)
        {
            DownloadSettingsStatus = exception.Message;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            DownloadSettingsStatus = "Could not add this audio URL to the download queue.";
        }
    }

    public void SetDownloadQueueRunning(bool isRunning)
    {
        if (_isDownloadQueueRunning == isRunning) return;
        _isDownloadQueueRunning = isRunning;
        OnPropertyChanged(nameof(IsDownloadQueueRunning));
        OnPropertyChanged(nameof(DownloadQueueActionLabel));
    }

    public void ReportDownloadProgress(DownloadQueueProgress? progress)
    {
        _activeDownloadProgress = progress;
        OnPropertyChanged(nameof(HasActiveDownloadProgress));
        OnPropertyChanged(nameof(ActiveDownloadProgressTitle));
        OnPropertyChanged(nameof(ActiveDownloadProgressLabel));
        OnPropertyChanged(nameof(ActiveDownloadProgressPercent));
    }

    public async Task RefreshDownloadQueueAsync(CancellationToken cancellationToken = default)
        => await ReloadQueueAsync(cancellationToken);

    public async Task MarkWidevineOfflineAvailableAsync(
        string audiobookId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(audiobookId))
            return;

        var book = _allBooks.FirstOrDefault(item =>
            string.Equals(item.Id, audiobookId, StringComparison.Ordinal));
        if (book is null || !book.IsProviderCatalogueEntry || book.IsWidevineOfflineAvailable)
            return;

        await _repository.UpsertAsync(
            book with { IsWidevineOfflineAvailable = true }, cancellationToken).ConfigureAwait(false);
        _allBooks = await _repository.GetBooksAsync(cancellationToken).ConfigureAwait(false);
        ApplyFilter(_allBooks);
        RefreshAudibleBooks(_allBooks);

        if (_detailBook is not null &&
            string.Equals(_detailBook.Id, audiobookId, StringComparison.Ordinal))
        {
            _detailBook = _allBooks.FirstOrDefault(item => item.Id == audiobookId);
            OnPropertyChanged(nameof(DetailBook));
            OnPropertyChanged(nameof(DetailAudibleAvailabilityLabel));
            OnPropertyChanged(nameof(DetailProtectedDownloadLabel));
            OnPropertyChanged(nameof(HasProtectedDetailDownload));
        }
    }

    public async Task MarkWidevineOfflineRemovedAsync(
        string audiobookId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(audiobookId))
            return;

        var book = _allBooks.FirstOrDefault(item =>
            string.Equals(item.Id, audiobookId, StringComparison.Ordinal));
        if (book is null || !book.IsProviderCatalogueEntry || !book.IsWidevineOfflineAvailable)
            return;

        await _repository.UpsertAsync(
            book with { IsWidevineOfflineAvailable = false }, cancellationToken).ConfigureAwait(false);
        _allBooks = await _repository.GetBooksAsync(cancellationToken).ConfigureAwait(false);
        ApplyFilter(_allBooks);
        RefreshAudibleBooks(_allBooks);

        if (_detailBook is not null &&
            string.Equals(_detailBook.Id, audiobookId, StringComparison.Ordinal))
        {
            _detailBook = _allBooks.FirstOrDefault(item => item.Id == audiobookId);
            OnPropertyChanged(nameof(DetailBook));
            OnPropertyChanged(nameof(DetailAudibleAvailabilityLabel));
            OnPropertyChanged(nameof(DetailProtectedDownloadLabel));
            OnPropertyChanged(nameof(HasProtectedDetailDownload));
        }
    }

    /// <summary>
    /// Clears catalogue "offline available" labels only after account-wide
    /// device cleanup has succeeded during Audible disconnect.
    /// </summary>
    public async Task ClearAllWidevineOfflineFlagsAsync(CancellationToken cancellationToken = default)
    {
        var changed = _allBooks
            .Where(book => book.IsProviderCatalogueEntry && book.IsWidevineOfflineAvailable)
            .ToArray();
        if (changed.Length == 0)
            return;

        var changedIds = changed.Select(book => book.Id).ToHashSet(StringComparer.Ordinal);
        var updated = _allBooks
            .Select(book => changedIds.Contains(book.Id)
                ? book with { IsWidevineOfflineAvailable = false }
                : book)
            .ToArray();
        await _repository.ReplaceAllAsync(updated, cancellationToken).ConfigureAwait(false);

        _allBooks = await _repository.GetBooksAsync(cancellationToken).ConfigureAwait(false);
        ApplyFilter(_allBooks);
        RefreshAudibleBooks(_allBooks);
        if (_detailBook is not null && changedIds.Contains(_detailBook.Id))
        {
            _detailBook = _allBooks.FirstOrDefault(book => book.Id == _detailBook.Id);
            OnPropertyChanged(nameof(DetailBook));
            OnPropertyChanged(nameof(DetailAudibleAvailabilityLabel));
            OnPropertyChanged(nameof(DetailProtectedDownloadLabel));
            OnPropertyChanged(nameof(HasProtectedDetailDownload));
        }
    }

    public async Task RefreshLibraryAfterDownloadAsync(CancellationToken cancellationToken = default)
    {
        _allBooks = await _repository.GetBooksAsync(cancellationToken);
        ApplyFilter(_allBooks);
        RefreshAudibleBooks(_allBooks);
        if (_detailBook is not null)
        {
            _detailBook = _allBooks.FirstOrDefault(book => book.Id == _detailBook.Id);
            OnPropertyChanged(nameof(DetailBook));
            OnPropertyChanged(nameof(DetailAudibleAvailabilityLabel));
            OnPropertyChanged(nameof(DetailProtectedDownloadLabel));
            OnPropertyChanged(nameof(HasPlayableDetailAudio));
            OnPropertyChanged(nameof(HasProtectedDetailDownload));
        }
    }

    private static string FormatDownloadBytes(long bytes)
    {
        if (bytes <= 0) return "0 B";
        string[] units = ["B", "KB", "MB", "GB"];
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:0.0} {units[unit]}";
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
        var booksById = _allBooks.ToDictionary(book => book.Id, StringComparer.Ordinal);
        QueueItems.Clear();
        foreach (var item in items)
        {
            if (string.IsNullOrWhiteSpace(item.DisplayTitle) &&
                booksById.TryGetValue(item.AudiobookId, out var book))
                QueueItems.Add(item with { DisplayTitle = $"{book.Title} — {book.Author}" });
            else
                QueueItems.Add(item);
        }
        OnPropertyChanged(nameof(IsQueueEmpty));
        OnPropertyChanged(nameof(HasPendingDownloadItems));
        OnPropertyChanged(nameof(QueueCountLabel));
        OnPropertyChanged(nameof(QueueSummaryLabel));
        OnPropertyChanged(nameof(ActiveDownloadProgressTitle));
    }

    private void RefreshAudibleBooks(IReadOnlyList<Audiobook> source)
    {
        var query = AudibleSearchText.Trim();
        var books = source
            .Where(book => book.Id.StartsWith("provider:", StringComparison.Ordinal))
            .Where(book => string.IsNullOrWhiteSpace(query)
                || book.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
                || book.Author.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(book => book.Title, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        AudibleBooks.Clear();
        foreach (var book in books) AudibleBooks.Add(book);
        OnPropertyChanged(nameof(HasAudibleBooks));
        OnPropertyChanged(nameof(HasNoAudibleBooks));
        OnPropertyChanged(nameof(AudibleLibraryEmptyMessage));
    }

    private void RefreshRecentlyPlayedBooks()
    {
        var books = _allBooks
            .Where(book => !string.IsNullOrWhiteSpace(book.AudioFilePath) && book.PlaybackPositionMilliseconds > 0)
            .OrderByDescending(book => book.PlaybackPositionMilliseconds)
            .ThenBy(book => book.Title, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        RecentlyPlayedBooks.Clear();
        foreach (var book in books) RecentlyPlayedBooks.Add(book);
        OnPropertyChanged(nameof(HasRecentlyPlayedBooks));
        OnPropertyChanged(nameof(HasNoRecentlyPlayedBooks));
    }

    private void RefreshLocalAudioBooks(IReadOnlyList<Audiobook> source)
    {
        var localBooks = source
            .Where(book => !string.IsNullOrWhiteSpace(book.AudioFilePath))
            .OrderBy(book => book.Title, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        LocalAudioBooks.Clear();
        foreach (var book in localBooks)
            LocalAudioBooks.Add(book with { IsSelectedForBulkAction = _selectedAudiobookIds.Contains(book.Id) });
        RefreshFilteredLocalAudioBooks();
        OnPropertyChanged(nameof(AllCount));
        OnPropertyChanged(nameof(AllTabLabel));
        OnPropertyChanged(nameof(NotStartedCount));
        OnPropertyChanged(nameof(InProgressCount));
        OnPropertyChanged(nameof(FinishedCount));
        OnPropertyChanged(nameof(NotStartedTabLabel));
        OnPropertyChanged(nameof(InProgressTabLabel));
        OnPropertyChanged(nameof(FinishedTabLabel));
        RefreshRecentlyPlayedBooks();
        OnPropertyChanged(nameof(HasLocalAudioBooks));
        OnPropertyChanged(nameof(HasNoLocalAudioBooks));
        OnPropertyChanged(nameof(LocalAudioEmptyMessage));
    }

    private void RefreshFilteredLocalAudioBooks()
    {
        FilteredLocalAudioBooks.Clear();
        foreach (var book in LocalAudioBooks.Where(book =>
            _listeningStatusFilter == "All" || book.ListeningStatus == _listeningStatusFilter))
            FilteredLocalAudioBooks.Add(book);
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
        OnPropertyChanged(nameof(CatalogueSummaryLabel));
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
