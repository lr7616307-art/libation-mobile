using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Libation.Mobile.Core;
using Libation.Mobile.UI.ViewModels;

namespace Libation.Mobile.UI.Views;

public partial class MainView : UserControl
{
    private readonly MainViewModel _viewModel;
    private ILocalAudioPlayer? _localAudioPlayer;
    private string? _playingAudiobookId;
    private readonly DispatcherTimer _playbackProgressTimer = new() { Interval = TimeSpan.FromSeconds(15) };
    private bool _isSavingPlaybackPosition;
    private static readonly FilePickerFileType AudioFileType = new("Audiobook audio") { Patterns = ["*.mp3", "*.m4a", "*.m4b", "*.aac", "*.flac", "*.ogg", "*.opus", "*.wav"] };
    private static readonly FilePickerFileType BackupFileType = new("Libation Mobile backup") { Patterns = ["*.json"] };

    public MainView()
    {
        var dataDirectory = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var repository = new JsonLibraryRepository(Path.Combine(dataDirectory, "library.json"));
        var queue = new JsonDownloadQueue(Path.Combine(dataDirectory, "download-queue.json"));
        var provider = PlatformLibraryCatalogProviderFactory?.Invoke();
        var syncService = provider is null ? null : new LibrarySyncService(provider, repository);
        var audioDirectory = Path.Combine(dataDirectory, "audiobooks");
        _viewModel = new MainViewModel(repository, queue, syncService, new LocalAudioImportService(audioDirectory));
        _localAudioPlayer = PlatformLocalAudioPlayerFactory?.Invoke();
        InitializeComponent();
        DataContext = _viewModel;
        Loaded += MainViewLoaded;
        Unloaded += MainViewUnloaded;
    }

    public MainView(ILibraryRepository repository, IDownloadQueue downloadQueue, LibrarySyncService? syncService = null)
    {
        _viewModel = new MainViewModel(repository, downloadQueue, syncService);
        InitializeComponent();
        DataContext = _viewModel;
        Loaded += MainViewLoaded;
        Unloaded += MainViewUnloaded;
    }

    private async void MainViewLoaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _playbackProgressTimer.Tick -= PlaybackProgressTimerTick;
        _playbackProgressTimer.Tick += PlaybackProgressTimerTick;
        await InitializeAndRestorePlaybackAsync();
    }

    private async Task InitializeAndRestorePlaybackAsync()
    {
        await _viewModel.InitializeAsync();
        if (_localAudioPlayer?.CurrentFilePath is not { Length: > 0 } currentPath)
            return;

        var currentBook = _viewModel.LocalAudioBooks.FirstOrDefault(book =>
            string.Equals(book.AudioFilePath, currentPath, StringComparison.Ordinal));
        if (currentBook is null)
            return;

        _playingAudiobookId = currentBook.Id;
        _viewModel.ReportAudioStatus((_localAudioPlayer.IsPlaying ? "Playing: " : "Paused: ") + currentBook.Title);
        if (_localAudioPlayer.IsPlaying)
            _playbackProgressTimer.Start();
    }

    private async void PlayAudioClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: Audiobook book }) return;
        if (string.IsNullOrWhiteSpace(book.AudioFilePath))
        {
            _viewModel.ReportAudioStatus("Import an audio file for this audiobook before playing it.");
            return;
        }
        if (_localAudioPlayer is null)
        {
            _viewModel.ReportAudioStatus("Audio playback is unavailable on this platform.");
            return;
        }
        try
        {
            _localAudioPlayer.Play(book.AudioFilePath, _viewModel.GetResumePositionMilliseconds(book.Id));
            _playingAudiobookId = book.Id;
            _playbackProgressTimer.Start();
            _viewModel.ReportAudioStatus("Playing: " + book.Title);
        }
        catch (Exception)
        {
            _viewModel.ReportAudioStatus("Could not play this audio file. Re-import it and try again.");
        }
    }

    private async void PauseAudioClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_localAudioPlayer is null || _playingAudiobookId is null) return;
        try
        {
            _localAudioPlayer.Pause();
            _playbackProgressTimer.Stop();
            await _viewModel.SavePlaybackPositionAsync(_playingAudiobookId, _localAudioPlayer.PositionMilliseconds);
            _viewModel.ReportAudioStatus("Playback paused. Your position has been saved.");
        }
        catch (Exception)
        {
            _viewModel.ReportAudioStatus("Could not pause playback cleanly.");
        }
    }

    private void ResumeAudioClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_localAudioPlayer is null || _playingAudiobookId is null) return;
        try
        {
            _localAudioPlayer.Resume();
            _playbackProgressTimer.Start();
            var book = _viewModel.LocalAudioBooks.FirstOrDefault(item => item.Id == _playingAudiobookId);
            if (book is not null) _viewModel.ReportAudioStatus("Playing: " + book.Title);
        }
        catch (Exception)
        {
            _viewModel.ReportAudioStatus("Could not resume playback.");
        }
    }

    private async void RewindAudioClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => await SeekRelativeAsync(-15_000);

    private async void ForwardAudioClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => await SeekRelativeAsync(15_000);

    private async Task SeekRelativeAsync(long offsetMilliseconds)
    {
        if (_localAudioPlayer is null || _playingAudiobookId is null)
        {
            _viewModel.ReportAudioStatus("Start an audiobook before using the skip controls.");
            return;
        }

        try
        {
            var target = Math.Max(0, _localAudioPlayer.PositionMilliseconds + offsetMilliseconds);
            var duration = _localAudioPlayer.DurationMilliseconds;
            if (duration > 0) target = Math.Min(target, duration);
            _localAudioPlayer.Seek(target);
            await _viewModel.SavePlaybackPositionAsync(_playingAudiobookId, target);
            _viewModel.ReportAudioStatus(offsetMilliseconds < 0
                ? "Moved back 15 seconds. Playback position saved."
                : "Moved forward 15 seconds. Playback position saved.");
        }
        catch (Exception)
        {
            _viewModel.ReportAudioStatus("Could not change the playback position.");
        }
    }

    private async void MainViewUnloaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _playbackProgressTimer.Stop();
        _playbackProgressTimer.Tick -= PlaybackProgressTimerTick;
        if (_localAudioPlayer is null) return;
        try
        {
            if (_playingAudiobookId is not null)
            {
                // The Android foreground service owns playback independently of
                // this view. Unloading the UI must checkpoint progress, not pause
                // the audiobook; the service continues while the app is backgrounded.
                await _viewModel.SavePlaybackPositionAsync(_playingAudiobookId, _localAudioPlayer.PositionMilliseconds);
            }
        }
        catch (Exception) { /* Cleanup must continue even if saving progress fails. */ }
        finally
        {
            _localAudioPlayer.Dispose();
            _localAudioPlayer = null;
            _playingAudiobookId = null;
        }
    }

    private async void PlaybackProgressTimerTick(object? sender, EventArgs e)
    {
        if (_isSavingPlaybackPosition || _localAudioPlayer is null || _playingAudiobookId is null) return;
        _isSavingPlaybackPosition = true;
        try
        {
            await _viewModel.SavePlaybackPositionAsync(_playingAudiobookId, _localAudioPlayer.PositionMilliseconds);
        }
        catch (Exception)
        {
            // Progress checkpoints are best-effort; playback continues uninterrupted.
        }
        finally
        {
            _isSavingPlaybackPosition = false;
        }
    }

    private async void ExportBackupClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var provider = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (provider is null) return;
        var file = await provider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export catalogue backup",
            SuggestedFileName = "libation-mobile-backup.json",
            DefaultExtension = "json",
            FileTypeChoices = [BackupFileType]
        });
        if (file is null) return;
        await using var stream = await file.OpenWriteAsync();
        await _viewModel.ExportBackupAsync(stream);
    }

    private async void ImportBackupClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var provider = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (provider is null) return;
        var files = await provider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import catalogue backup",
            AllowMultiple = false,
            FileTypeFilter = [BackupFileType]
        });
        if (files.Count == 0) return;
        await using var stream = await files[0].OpenReadAsync();
        await _viewModel.PrepareImportAsync(stream);
    }

    private async void ConfirmImportClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => await _viewModel.ConfirmImportAsync();

    private void CancelImportClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => _viewModel.CancelImport();

    private async void ReloadClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => await _viewModel.ReloadAsync();

    private async void SyncLibraryClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => await _viewModel.SyncLibraryAsync();

    private void ShowDetailsClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Button { DataContext: Audiobook book })
            _viewModel.SelectBook(book.Id);
    }

    private void CloseDetailsClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => _viewModel.SelectBook(string.Empty);

    private void SortClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => _viewModel.ToggleSort();
    private void FilterClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => _viewModel.CycleCatalogueFilter();

    private async void AddBookClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => await _viewModel.AddBookAsync();

    private void EditBookClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Button { DataContext: Audiobook book } && book.CanDelete)
            _viewModel.BeginEdit(book.Id);
    }

    private void CancelEditClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => _viewModel.CancelEdit();

    private async void DeleteBookClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Button { DataContext: Audiobook book } && book.CanDelete)
            await _viewModel.RemoveBookAsync(book.Id);
    }

    private async void ImportAudioClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: Audiobook book }) return;
        var provider = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (provider is null) return;
        var files = await provider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose audiobook audio",
            AllowMultiple = false,
            FileTypeFilter = [AudioFileType]
        });
        if (files.Count == 0) return;
        await using var stream = await files[0].OpenReadAsync();
        await _viewModel.ImportAudioAsync(book.Id, files[0].Name, stream);
    }
    private async void QueueClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Button { DataContext: Audiobook book })
            await _viewModel.EnqueueAsync(book.Id);
    }

    private async void RetryQueueClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Button { DataContext: DownloadQueueItem item } && item.Status == DownloadStatus.Failed)
            await _viewModel.RetryFailedAsync(item.AudiobookId);
    }

    private async void RemoveQueueClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Button { DataContext: DownloadQueueItem item })
            await _viewModel.RemoveFromQueueAsync(item.AudiobookId);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
