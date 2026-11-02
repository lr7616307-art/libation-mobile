using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Libation.Mobile.Core;

namespace Libation.Mobile.UI.ViewModels;

/// <summary>Displays a locally persisted catalogue and metadata-only download queue.</summary>
public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly ILibraryRepository _repository;
    private readonly IDownloadQueue _downloadQueue;
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private string _searchText = string.Empty;
    private string _newTitle = string.Empty;
    private string _newAuthor = string.Empty;
    private string _newNarrator = string.Empty;
    private string _newLength = string.Empty;
    private bool _isLoading;
    private string _errorMessage = string.Empty;

    private static readonly Audiobook[] InitialSamples =
    [
        new("demo-001", "The Long Way to a Small, Angry Planet", "Becky Chambers", "Rachel Dulude", "12h 20m", "Demo"),
        new("demo-002", "Project Hail Mary", "Andy Weir", "Ray Porter", "16h 10m", "Demo"),
        new("demo-003", "The Three-Body Problem", "Cixin Liu", "Luke Daniels", "13h 40m", "Demo")
    ];

    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<Audiobook> Books { get; } = [];
    public ObservableCollection<DownloadQueueItem> QueueItems { get; } = [];

    public MainViewModel(ILibraryRepository repository, IDownloadQueue downloadQueue)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _downloadQueue = downloadQueue ?? throw new ArgumentNullException(nameof(downloadQueue));
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (_searchText == value) return;
            _searchText = value;
            OnPropertyChanged();
            ApplyFilter(_allBooks);
        }
    }

    public string NewTitle
    {
        get => _newTitle;
        set => SetField(ref _newTitle, value);
    }

    public string NewAuthor
    {
        get => _newAuthor;
        set => SetField(ref _newAuthor, value);
    }

    public string NewNarrator
    {
        get => _newNarrator;
        set => SetField(ref _newNarrator, value);
    }

    public string NewLength
    {
        get => _newLength;
        set => SetField(ref _newLength, value);
    }

    private IReadOnlyList<Audiobook> _allBooks = [];

    public bool IsLoading
    {
        get => _isLoading;
        private set { if (_isLoading == value) return; _isLoading = value; OnPropertyChanged(); }
    }

    public string ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (_errorMessage == value) return;
            _errorMessage = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasError));
        }
    }

    public bool HasBooks => Books.Count > 0;
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool HasQueueItems => QueueItems.Count > 0;
    public bool IsQueueEmpty => QueueItems.Count == 0;
    public string QueueCountLabel => $"Download queue ({QueueItems.Count})";
    public string EmptyMessage => HasBooks ? string.Empty :
        string.IsNullOrWhiteSpace(SearchText) ? "Your local catalogue is empty." : "No matching audiobooks.";

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _loadGate.WaitAsync(cancellationToken);
        try
        {
            IsLoading = true;
            ErrorMessage = string.Empty;
            var stored = await _repository.GetBooksAsync(cancellationToken);
            if (stored.Count == 0)
            {
                await _repository.ReplaceAllAsync(InitialSamples, cancellationToken);
                stored = await _repository.GetBooksAsync(cancellationToken);
            }

            _allBooks = stored;
            ApplyFilter(_allBooks);
            await ReloadQueueAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            ErrorMessage = "Could not load local data. Check app storage and try again.";
            _allBooks = [];
            ApplyFilter(_allBooks);
        }
        finally
        {
            IsLoading = false;
            _loadGate.Release();
        }
    }

    public Task ReloadAsync(CancellationToken cancellationToken = default)
        => InitializeAsync(cancellationToken);

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
            var book = new Audiobook(
                Guid.NewGuid().ToString("N"),
                title,
                author,
                NewNarrator.Trim(),
                NewLength.Trim(),
                "Manually added");
            await _repository.UpsertAsync(book, cancellationToken);
            _allBooks = await _repository.GetBooksAsync(cancellationToken);
            NewTitle = string.Empty;
            NewAuthor = string.Empty;
            NewNarrator = string.Empty;
            NewLength = string.Empty;
            ApplyFilter(_allBooks);
            ErrorMessage = string.Empty;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            ErrorMessage = "Could not save this audiobook to the local catalogue.";
        }
    }

    public async Task EnqueueAsync(string audiobookId, CancellationToken cancellationToken = default)
    {
        try
        {
            await _downloadQueue.EnqueueAsync(audiobookId, cancellationToken);
            await ReloadQueueAsync(cancellationToken);
            ErrorMessage = string.Empty;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            ErrorMessage = "Could not update the local download queue.";
        }
    }

    public Task RetryFailedAsync(string audiobookId, CancellationToken cancellationToken = default)
        => EnqueueAsync(audiobookId, cancellationToken);

    public async Task RemoveFromQueueAsync(string audiobookId, CancellationToken cancellationToken = default)
    {
        try
        {
            await _downloadQueue.RemoveAsync(audiobookId, cancellationToken);
            await ReloadQueueAsync(cancellationToken);
            ErrorMessage = string.Empty;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            ErrorMessage = "Could not remove the item from the local download queue.";
        }
    }

    private async Task ReloadQueueAsync(CancellationToken cancellationToken)
    {
        var items = await _downloadQueue.GetItemsAsync(cancellationToken);
        QueueItems.Clear();
        foreach (var item in items)
            QueueItems.Add(item);
        OnPropertyChanged(nameof(HasQueueItems));
        OnPropertyChanged(nameof(IsQueueEmpty));
        OnPropertyChanged(nameof(QueueCountLabel));
    }

    private void ApplyFilter(IReadOnlyList<Audiobook> source)
    {
        var query = SearchText.Trim();
        var matches = string.IsNullOrEmpty(query)
            ? source
            : source.Where(book =>
                book.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                book.Author.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                book.Narrator.Contains(query, StringComparison.OrdinalIgnoreCase));

        Books.Clear();
        foreach (var book in matches)
            Books.Add(book);

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
