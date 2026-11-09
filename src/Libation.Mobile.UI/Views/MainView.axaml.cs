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
    private readonly DispatcherTimer _sleepTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _queueRefreshTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private DateTimeOffset? _sleepTimerEndsAt;
    private static readonly int[] SleepTimerMinutes = [15, 30, 45, 60];
    private int _sleepTimerSelection = -1;
    private long? _sleepAtPositionMilliseconds;
    private bool _isSavingPlaybackPosition;
    private bool _isUpdatingPlaybackSlider;
    private bool _isRefreshingQueue;
    private static readonly FilePickerFileType AudioFileType = new("Audiobook audio") { Patterns = ["*.mp3", "*.m4a", "*.m4b", "*.aac", "*.flac", "*.ogg", "*.opus", "*.wav"] };
    private static readonly FilePickerFileType BackupFileType = new("Libation Mobile backup") { Patterns = ["*.json"] };
    private static readonly FilePickerFileType CoverImageFileType = new("Audiobook cover image") { Patterns = ["*.jpg", "*.jpeg", "*.png", "*.webp"] };

    public MainView()
    {
        var dataDirectory = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var repository = new JsonLibraryRepository(Path.Combine(dataDirectory, "library.json"));
        var queue = PlatformDownloadQueueFactory?.Invoke()
            ?? new JsonDownloadQueue(Path.Combine(dataDirectory, "download-queue.json"));
        var provider = PlatformLibraryCatalogProviderFactory?.Invoke();
        var syncService = provider is null ? null : new LibrarySyncService(provider, repository);
        var audioDirectory = Path.Combine(dataDirectory, "audiobooks");
        _viewModel = new MainViewModel(repository, queue, syncService, new LocalAudioImportService(audioDirectory), new CoverArtImportService(Path.Combine(dataDirectory, "covers")));
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
        _sleepTimer.Tick -= SleepTimerTick;
        _sleepTimer.Tick += SleepTimerTick;
        _queueRefreshTimer.Tick -= QueueRefreshTimerTick;
        _queueRefreshTimer.Tick += QueueRefreshTimerTick;
        await InitializeAndRestorePlaybackAsync();
        UpdateQueueRefreshTimer();
    }

    private async Task InitializeAndRestorePlaybackAsync()
    {
        // The view may be unloaded and loaded again while the Android playback
        // service keeps running. Recreate its lightweight adapter on each load.
        _localAudioPlayer ??= PlatformLocalAudioPlayerFactory?.Invoke();
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
        RestoreSleepTimerUi();
        UpdatePlaybackProgressUi();
    }

    private void RestoreSleepTimerUi()
    {
        if (_localAudioPlayer is not ISleepTimerAudioPlayer timerPlayer)
            return;

        var snapshot = timerPlayer.GetSleepTimer();
        if (snapshot is null)
            return;

        _sleepTimerEndsAt = snapshot.Deadline;
        _sleepAtPositionMilliseconds = snapshot.ChapterPositionMilliseconds;
        // A restored timer can be cancelled with the next tap. Its original
        // preset is intentionally not guessed from the remaining duration.
        _sleepTimerSelection = SleepTimerMinutes.Length;
        _sleepTimer.Start();
        UpdateSleepTimerButton();
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
            // Save the currently active session before replacing it. The
            // periodic checkpoint may not have fired yet, so switching books
            // (or restarting the same book) must not fall back to an older
            // catalogue position.
            if (_playingAudiobookId is not null)
                await _viewModel.SavePlaybackPositionAsync(
                    _playingAudiobookId, _localAudioPlayer.PositionMilliseconds);

            _localAudioPlayer.Play(book.AudioFilePath, _viewModel.GetResumePositionMilliseconds(book.Id), book.Title);
            _playingAudiobookId = book.Id;
            _playbackProgressTimer.Start();
            _viewModel.ReportAudioStatus("Playing: " + book.Title);
            UpdatePlaybackProgressUi();
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
            UpdatePlaybackProgressUi();
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
            UpdatePlaybackProgressUi();
        }
        catch (Exception)
        {
            _viewModel.ReportAudioStatus("Could not resume playback.");
        }
    }

    private async void RewindAudioClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => await SeekRelativeAsync(-15_000);

    private void SleepTimerClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_localAudioPlayer is null || _playingAudiobookId is null)
        {
            _viewModel.ReportAudioStatus("Start an audiobook before setting a sleep timer.");
            return;
        }

        _sleepTimerSelection++;
        if (_sleepTimerSelection < SleepTimerMinutes.Length)
        {
            var minutes = SleepTimerMinutes[_sleepTimerSelection];
            _sleepAtPositionMilliseconds = null;
            _sleepTimerEndsAt = DateTimeOffset.UtcNow.AddMinutes(minutes);
            if (_localAudioPlayer is ISleepTimerAudioPlayer timerPlayer)
                timerPlayer.SetSleepTimer(_sleepTimerEndsAt, null);
            _sleepTimer.Start();
            UpdateSleepTimerButton();
            _viewModel.ReportAudioStatus($"Sleep timer set for {minutes} minutes.");
            return;
        }

        if (_sleepTimerSelection == SleepTimerMinutes.Length)
        {
            var book = _viewModel.LocalAudioBooks.FirstOrDefault(item => item.Id == _playingAudiobookId);
            var chapters = AudiobookChapterService.Normalize(book?.Chapters, _localAudioPlayer.DurationMilliseconds);
            var nextChapter = AudiobookChapterService.GetNextChapterPosition(chapters, _localAudioPlayer.PositionMilliseconds);
            if (chapters.Count == 0 || nextChapter is null)
            {
                _sleepTimerSelection = SleepTimerMinutes.Length;
                _viewModel.ReportAudioStatus("Add a following chapter marker to use end-of-chapter sleep. Tap again to cancel.");
                return;
            }

            _sleepAtPositionMilliseconds = nextChapter.Value;
            _sleepTimerEndsAt = null;
            if (_localAudioPlayer is ISleepTimerAudioPlayer timerPlayer)
                timerPlayer.SetSleepTimer(null, _sleepAtPositionMilliseconds);
            _sleepTimer.Start();
            UpdateSleepTimerButton();
            _viewModel.ReportAudioStatus("Sleep timer will pause at the end of this chapter.");
            return;
        }

        CancelSleepTimer();
        _viewModel.ReportAudioStatus("Sleep timer cancelled.");
    }

    private void CancelSleepTimer()
    {
        _sleepTimer.Stop();
        _sleepTimerEndsAt = null;
        _sleepAtPositionMilliseconds = null;
        _sleepTimerSelection = -1;
        if (_localAudioPlayer is ISleepTimerAudioPlayer timerPlayer)
            timerPlayer.CancelSleepTimer();
        if (SleepTimerButton is not null)
            SleepTimerButton.Content = "Sleep timer · Off";
    }

    private void UpdateSleepTimerButton()
    {
        if (SleepTimerButton is null)
            return;

        if (_sleepAtPositionMilliseconds is not null)
        {
            SleepTimerButton.Content = "Sleep timer · End of chapter";
            return;
        }

        if (_sleepTimerEndsAt is not { } endsAt)
        {
            SleepTimerButton.Content = "Sleep timer · Off";
            return;
        }

        var remaining = endsAt - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            SleepTimerButton.Content = "Sleep timer · Finishing…";
            return;
        }

        var totalSeconds = (int)Math.Ceiling(remaining.TotalSeconds);
        SleepTimerButton.Content = $"Sleep timer · {totalSeconds / 60:D2}:{totalSeconds % 60:D2}";
    }

    private async void SleepTimerTick(object? sender, EventArgs e)
    {
        var shouldPause = false;
        if (_sleepAtPositionMilliseconds is { } chapterEnd)
        {
            shouldPause = _localAudioPlayer is not null && _localAudioPlayer.PositionMilliseconds >= chapterEnd;
            if (!shouldPause)
            {
                UpdateSleepTimerButton();
                return;
            }
        }
        else if (_sleepTimerEndsAt is { } endsAt)
        {
            if (DateTimeOffset.UtcNow < endsAt)
            {
                UpdateSleepTimerButton();
                return;
            }
            shouldPause = true;
        }
        else
        {
            CancelSleepTimer();
            return;
        }

        if (!shouldPause)
            return;

        _sleepTimer.Stop();
        _sleepTimerEndsAt = null;
        _sleepAtPositionMilliseconds = null;
        _sleepTimerSelection = -1;
        if (SleepTimerButton is not null)
            SleepTimerButton.Content = "Sleep timer · Off";

        if (_localAudioPlayer is null || _playingAudiobookId is null)
        {
            _viewModel.ReportAudioStatus("Sleep timer ended; no active audiobook was playing.");
            return;
        }

        try
        {
            _localAudioPlayer.Pause();
            _playbackProgressTimer.Stop();
            await _viewModel.SavePlaybackPositionAsync(_playingAudiobookId, _localAudioPlayer.PositionMilliseconds);
            UpdatePlaybackProgressUi();
            _viewModel.ReportAudioStatus("Sleep timer finished. Playback paused and position saved.");
        }
        catch (Exception)
        {
            _viewModel.ReportAudioStatus("Sleep timer finished, but playback could not be paused cleanly.");
        }
    }

    private void PlaybackSpeedClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_localAudioPlayer is null)
        {
            _viewModel.ReportAudioStatus("Playback speed is available when an audiobook is playing.");
            return;
        }

        float[] speeds = [0.5f, 0.75f, 1f, 1.25f, 1.5f, 1.75f, 2f, 2.5f, 3f];
        var currentIndex = Array.FindIndex(speeds, speed => Math.Abs(speed - _localAudioPlayer.PlaybackSpeed) < 0.01f);
        var nextSpeed = speeds[(currentIndex + 1) % speeds.Length];
        try
        {
            _localAudioPlayer.SetPlaybackSpeed(nextSpeed);
            PlaybackSpeedButton.Content = $"Speed · {nextSpeed:0.##}×";
            _viewModel.ReportAudioStatus($"Playback speed set to {nextSpeed:0.##}×.");
        }
        catch (Exception)
        {
            _viewModel.ReportAudioStatus("Could not change playback speed.");
        }
    }

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
        _sleepTimer.Stop();
        _queueRefreshTimer.Stop();
        _queueRefreshTimer.Tick -= QueueRefreshTimerTick;
        _sleepTimer.Tick -= SleepTimerTick;
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

    private void UpdatePlaybackProgressUi()
    {
        if (PlaybackSeekSlider is null || PlaybackPositionLabel is null || PlaybackDurationLabel is null)
            return;

        var position = Math.Max(0, _localAudioPlayer?.PositionMilliseconds ?? 0);
        var duration = Math.Max(0, _localAudioPlayer?.DurationMilliseconds ?? 0);
        UpdateCurrentChapterUi(position, duration);
        if (PlaybackSpeedButton is not null)
        {
            var speed = _localAudioPlayer?.PlaybackSpeed ?? 1f;
            PlaybackSpeedButton.Content = $"Speed · {speed:0.##}×";
        }
        _isUpdatingPlaybackSlider = true;
        try
        {
            PlaybackSeekSlider.Maximum = Math.Max(1, duration);
            PlaybackSeekSlider.Value = duration > 0 ? Math.Min(position, duration) : 0;
            PlaybackSeekSlider.IsEnabled = duration > 0 && _playingAudiobookId is not null;
            PlaybackPositionLabel.Text = FormatPlaybackTime(position);
            PlaybackDurationLabel.Text = duration > 0 ? FormatPlaybackTime(duration) : "--:--";
        }
        finally { _isUpdatingPlaybackSlider = false; }
    }

    private void UpdateCurrentChapterUi(long positionMilliseconds, long durationMilliseconds)
    {
        if (CurrentChapterTitleLabel is null || ChapterProgressLabel is null)
            return;

        var book = _playingAudiobookId is null
            ? null
            : _viewModel.LocalAudioBooks.FirstOrDefault(item => item.Id == _playingAudiobookId);
        var chapters = AudiobookChapterService.Normalize(book?.Chapters, durationMilliseconds);
        var currentIndex = AudiobookChapterService.FindCurrentChapterIndex(chapters, positionMilliseconds);

        if (chapters.Count == 0)
        {
            CurrentChapterTitleLabel.Text = "No chapter markers available";
            ChapterProgressLabel.Text = "Add chapter markers in audiobook details";
            return;
        }

        if (currentIndex < 0)
        {
            CurrentChapterTitleLabel.Text = "Before first chapter";
            ChapterProgressLabel.Text = $"Starts at {FormatPlaybackTime(chapters[0].StartPositionMilliseconds)}";
            return;
        }

        var current = chapters[currentIndex];
        var end = currentIndex + 1 < chapters.Count
            ? chapters[currentIndex + 1].StartPositionMilliseconds
            : durationMilliseconds;
        CurrentChapterTitleLabel.Text = current.Title;
        var chapterRange = end > current.StartPositionMilliseconds
            ? $"{FormatPlaybackTime(current.StartPositionMilliseconds)} – {FormatPlaybackTime(end)}"
            : $"from {FormatPlaybackTime(current.StartPositionMilliseconds)}";
        ChapterProgressLabel.Text = $"Chapter {currentIndex + 1} of {chapters.Count} · {chapterRange}";
    }

    private static string FormatPlaybackTime(long milliseconds)
    {
        var time = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
        return time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"m\:ss");
    }

    private async void SeekToSelectedPositionClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_isUpdatingPlaybackSlider || _localAudioPlayer is null || _playingAudiobookId is null)
            return;

        try
        {
            var target = Math.Max(0, (long)PlaybackSeekSlider.Value);
            var duration = _localAudioPlayer.DurationMilliseconds;
            if (duration > 0) target = Math.Min(target, duration);
            _localAudioPlayer.Seek(target);
            await _viewModel.SavePlaybackPositionAsync(_playingAudiobookId, target);
            UpdatePlaybackProgressUi();
            _viewModel.ReportAudioStatus("Playback position updated.");
        }
        catch (Exception)
        {
            _viewModel.ReportAudioStatus("Could not seek to that position.");
        }
    }

    private async void PreviousChapterClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => await SeekChapterAsync(forward: false);

    private async void NextChapterClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => await SeekChapterAsync(forward: true);

    private async Task SeekChapterAsync(bool forward)
    {
        if (_localAudioPlayer is null || _playingAudiobookId is null)
        {
            _viewModel.ReportAudioStatus("Start an audiobook before using chapter navigation.");
            return;
        }

        var book = _viewModel.LocalAudioBooks.FirstOrDefault(item => item.Id == _playingAudiobookId);
        var chapters = AudiobookChapterService.Normalize(book?.Chapters, _localAudioPlayer.DurationMilliseconds);
        if (chapters.Count == 0)
        {
            _viewModel.ReportAudioStatus("No chapter markers are available for this audiobook yet.");
            return;
        }

        var position = Math.Max(0, _localAudioPlayer.PositionMilliseconds);
        var currentIndex = AudiobookChapterService.FindCurrentChapterIndex(chapters, position);
        long target;
        if (forward)
        {
            var next = AudiobookChapterService.GetNextChapterPosition(chapters, position);
            if (next is null)
            {
                _viewModel.ReportAudioStatus("You are at the last chapter.");
                return;
            }
            target = next.Value;
        }
        else
        {
            // A second press near the beginning of a chapter returns to the previous chapter.
            var currentStart = currentIndex >= 0 ? chapters[currentIndex].StartPositionMilliseconds : 0;
            if (currentIndex >= 0 && position - currentStart > 5_000)
                target = currentStart;
            else if (currentIndex > 0)
                target = chapters[currentIndex - 1].StartPositionMilliseconds;
            else
                target = chapters[0].StartPositionMilliseconds;
        }

        try
        {
            _localAudioPlayer.Seek(target);
            await _viewModel.SavePlaybackPositionAsync(_playingAudiobookId, target);
            var targetIndex = AudiobookChapterService.FindCurrentChapterIndex(chapters, target);
            var chapterTitle = targetIndex >= 0 ? chapters[targetIndex].Title : "chapter";
            UpdatePlaybackProgressUi();
            _viewModel.ReportAudioStatus($"Jumped to {chapterTitle}.");
        }
        catch (Exception)
        {
            _viewModel.ReportAudioStatus("Could not navigate to that chapter.");
        }
    }

    private async void PlaybackProgressTimerTick(object? sender, EventArgs e)
    {
        if (_isSavingPlaybackPosition || _localAudioPlayer is null || _playingAudiobookId is null) return;
        _isSavingPlaybackPosition = true;
        try
        {
            await _viewModel.SavePlaybackPositionAsync(_playingAudiobookId, _localAudioPlayer.PositionMilliseconds);
            UpdatePlaybackProgressUi();
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

    private async void AddChapterClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => await _viewModel.AddChapterAsync();

    private async void RemoveChapterClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Button { Tag: long startPositionMilliseconds })
            await _viewModel.RemoveChapterAsync(startPositionMilliseconds);
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
    private async void ImportCoverArtClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: Audiobook book }) return;
        var provider = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (provider is null) return;
        var files = await provider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose audiobook cover artwork",
            AllowMultiple = false,
            FileTypeFilter = [CoverImageFileType]
        });
        if (files.Count == 0) return;
        await using var stream = await files[0].OpenReadAsync();
        await _viewModel.ImportCoverArtAsync(book.Id, files[0].Name, stream);
    }

    private void UpdateQueueRefreshTimer()
    {
        if (_viewModel.QueueItems.Count > 0)
            _queueRefreshTimer.Start();
        else
            _queueRefreshTimer.Stop();
    }

    private async void QueueRefreshTimerTick(object? sender, EventArgs e)
    {
        if (_isRefreshingQueue)
            return;

        _isRefreshingQueue = true;
        try
        {
            await _viewModel.RefreshQueueAsync();
            UpdateQueueRefreshTimer();
        }
        catch (Exception)
        {
            // A transient storage read must not interrupt playback or the UI loop.
        }
        finally
        {
            _isRefreshingQueue = false;
        }
    }

    private void PauseQueueClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => PlatformDownloadQueuePauseAction?.Invoke();

    private void ResumeQueueClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => PlatformDownloadQueueStartAction?.Invoke();

    private async void QueueClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Button { DataContext: Audiobook book })
        {
            await _viewModel.EnqueueAsync(book.Id);
            PlatformDownloadQueueStartAction?.Invoke();
            UpdateQueueRefreshTimer();
        }
    }

    private async void RetryQueueClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Button { DataContext: DownloadQueueItem item } && item.Status == DownloadStatus.Failed)
        {
            await _viewModel.RetryFailedAsync(item.AudiobookId);
            PlatformDownloadQueueStartAction?.Invoke();
            UpdateQueueRefreshTimer();
        }
    }

    private async void RemoveQueueClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Button { DataContext: DownloadQueueItem item })
        {
            await _viewModel.RemoveFromQueueAsync(item.AudiobookId);
            UpdateQueueRefreshTimer();
        }
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
