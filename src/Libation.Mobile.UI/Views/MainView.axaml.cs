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
    private static WeakReference<MainView>? _activeView;
    
    public static bool TryHandleSystemBack()
    {
        return _activeView is not null
            && _activeView.TryGetTarget(out var view)
            && view._viewModel.TryNavigateBack();
    }
    private ILocalAudioPlayer? _localAudioPlayer;
    private string? _playingAudiobookId;
    private readonly DispatcherTimer _playbackProgressTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly DispatcherTimer _playbackCheckpointTimer = new() { Interval = TimeSpan.FromSeconds(15) };
    private readonly DispatcherTimer _sleepTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private DateTimeOffset? _sleepTimerEndsAt;
    private static readonly int[] SleepTimerMinutes = [15, 30, 45, 60];
    private int _sleepTimerSelection = -1;
    private long? _sleepAtPositionMilliseconds;
    private bool _isSavingPlaybackPosition;
    private bool _isUpdatingPlaybackSlider;
    private static readonly FilePickerFileType AudioFileType = new("Audiobook audio") { Patterns = ["*.mp3", "*.m4a", "*.m4b", "*.aac", "*.flac", "*.ogg", "*.opus", "*.wav"] };
    private static readonly FilePickerFileType BackupFileType = new("Libation Mobile backup") { Patterns = ["*.json"] };
    private static readonly FilePickerFileType CoverImageFileType = new("Audiobook cover image") { Patterns = ["*.jpg", "*.jpeg", "*.png", "*.webp"] };

    public MainView()
    {
        var dataDirectory = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var repository = new JsonLibraryRepository(Path.Combine(dataDirectory, "library.json"));
        var queue = new JsonDownloadQueue(Path.Combine(dataDirectory, "download-queue.json"));
        var provider = PlatformLibraryCatalogProviderFactory?.Invoke();
        var syncService = provider is null ? null : new LibrarySyncService(provider, repository);
        var syncStateStore = new JsonSyncStateStore(Path.Combine(dataDirectory, "library-sync-state.json"));
        var audioDirectory = Path.Combine(dataDirectory, "audiobooks");
        var coverDirectory = Path.Combine(dataDirectory, "covers");
        var audioImport = new LocalAudioImportService(audioDirectory);
        _viewModel = new MainViewModel(repository, queue, syncService, audioImport, new CoverArtImportService(coverDirectory),
            syncStateStore, PlatformWidevineDiagnosticFactory, new AudiobookFileImportService(audioImport, coverDirectory));
        _localAudioPlayer = PlatformLocalAudioPlayerFactory?.Invoke();
        InitializeComponent();
        DataContext = _viewModel;
        // XAML-generated named controls can be null on some Android/Avalonia startup paths.
        // Never let an optional seek control prevent the entire app from opening.
        PlaybackSeekSlider?.AddHandler(Avalonia.Input.InputElement.PointerReleasedEvent, PlaybackSeekSliderPointerReleased);
        _activeView = new WeakReference<MainView>(this);
        Loaded += MainViewLoaded;
        Unloaded += MainViewUnloaded;
    }

    public MainView(ILibraryRepository repository, IDownloadQueue downloadQueue, LibrarySyncService? syncService = null)
    {
        _viewModel = new MainViewModel(repository, downloadQueue, syncService);
        InitializeComponent();
        DataContext = _viewModel;
        _activeView = new WeakReference<MainView>(this);
        Loaded += MainViewLoaded;
        Unloaded += MainViewUnloaded;
    }

    private void MainViewLoaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _playbackProgressTimer.Tick -= PlaybackProgressTimerTick;
        _playbackProgressTimer.Tick += PlaybackProgressTimerTick;
        _playbackCheckpointTimer.Tick -= PlaybackCheckpointTimerTick;
        _playbackCheckpointTimer.Tick += PlaybackCheckpointTimerTick;
        _sleepTimer.Tick -= SleepTimerTick;
        _sleepTimer.Tick += SleepTimerTick;

        // Let Avalonia present the first Home frame before opening the local
        // catalogue and restoring the playback adapter. This avoids making
        // disk initialization part of the first visible frame.
        Dispatcher.UIThread.Post(
            async () =>
            {
                try
                {
                    await InitializeAndRestorePlaybackAsync();
                }
                catch (Exception ex)
                {
                    // A catalogue or playback-restore failure must not crash the UI thread.
                    System.Diagnostics.Debug.WriteLine($"Audiobook startup initialization failed: {ex}");
                    _viewModel.ReportAudioStatus("Your library could not be fully loaded. The app is still available; try Reload from Home.");
                }
            },
            DispatcherPriority.Background);
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
        _viewModel.SetNowPlayingBook(currentBook.Id);
        _localAudioPlayer.SetArtwork(currentBook.CoverImagePath);
        // Older library entries may predate embedded chapter extraction. Read
        // their local file asynchronously without interrupting restored playback.
        await _viewModel.LoadChapterMetadataAsync(currentBook.Id);
        var refreshedBook = _viewModel.LocalAudioBooks.FirstOrDefault(book => book.Id == currentBook.Id) ?? currentBook;
        SetPlayerChapterPositions(refreshedBook);
        if (MiniPlayPauseButton is not null) MiniPlayPauseButton.Content = _localAudioPlayer.IsPlaying ? "⏸" : "▶";
        if (PlayPauseButton is not null) PlayPauseButton.Content = _localAudioPlayer.IsPlaying ? "⏸" : "▶";
        _viewModel.ReportAudioStatus((_localAudioPlayer.IsPlaying ? "Playing: " : "Paused: ") + currentBook.Title);
        // Keep the lightweight UI poll alive while paused as well, so play/pause
        // changes made from the Android notification are reflected in the app.
        _playbackProgressTimer.Start();
        if (_localAudioPlayer.IsPlaying)
            _playbackCheckpointTimer.Start();
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
        if (sender is Button { DataContext: Audiobook book })
            await PlayAudiobookAsync(book);
    }

    private async void PlayMenuBookClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_viewModel.MenuBook is not { } book) return;
        _viewModel.CloseBookMenu();
        await PlayAudiobookAsync(book);
    }

    private async void PlayDetailBookClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_viewModel.DetailBook is { } book)
            await PlayAudiobookAsync(book);
    }

    private async Task PlayAudiobookAsync(Audiobook book)
    {
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
            _localAudioPlayer.Play(book.AudioFilePath, _viewModel.GetResumePositionMilliseconds(book.Id), book.Title);
            _localAudioPlayer.SetArtwork(book.CoverImagePath);
            _playingAudiobookId = book.Id;
            await _viewModel.MarkPlaybackStartedAsync(book.Id);
            _viewModel.SetNowPlayingBook(book.Id);
            // Start audio first; metadata parsing runs off the UI thread and
            // can populate chapter navigation for older imported files.
            await _viewModel.LoadChapterMetadataAsync(book.Id);
            var refreshedBook = _viewModel.LocalAudioBooks.FirstOrDefault(item => item.Id == book.Id) ?? book;
            SetPlayerChapterPositions(refreshedBook);
            if (PlayPauseButton is not null) PlayPauseButton.Content = "⏸";
            if (MiniPlayPauseButton is not null) MiniPlayPauseButton.Content = "⏸";
            _playbackProgressTimer.Start();
            _playbackCheckpointTimer.Start();
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
            if (PlayPauseButton is not null) PlayPauseButton.Content = "▶";
            if (MiniPlayPauseButton is not null) MiniPlayPauseButton.Content = "▶";
            // Keep UI polling active while paused so notification controls stay in sync.
            _playbackCheckpointTimer.Stop();
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
            if (PlayPauseButton is not null) PlayPauseButton.Content = "⏸";
            if (MiniPlayPauseButton is not null) MiniPlayPauseButton.Content = "⏸";
            _playbackProgressTimer.Start();
            _playbackCheckpointTimer.Start();
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
        => await SeekRelativeAsync(-10_000);

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
            // Keep UI polling active while paused so notification controls stay in sync.
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
            UpdatePlaybackProgressUi();
            _viewModel.ReportAudioStatus($"Playback speed set to {nextSpeed:0.##}×.");
        }
        catch (Exception)
        {
            _viewModel.ReportAudioStatus("Could not change playback speed.");
        }
    }

    private async void ForwardAudioClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => await SeekRelativeAsync(10_000);

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
                ? "Moved back 10 seconds. Playback position saved."
                : "Moved forward 10 seconds. Playback position saved.");
        }
        catch (Exception)
        {
            _viewModel.ReportAudioStatus("Could not change the playback position.");
        }
    }

    private async void MainViewUnloaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _playbackProgressTimer.Stop();
        _playbackCheckpointTimer.Stop();
        _sleepTimer.Stop();
        PlaybackSeekSlider?.RemoveHandler(Avalonia.Input.InputElement.PointerReleasedEvent, PlaybackSeekSliderPointerReleased);
        _sleepTimer.Tick -= SleepTimerTick;
        _playbackProgressTimer.Tick -= PlaybackProgressTimerTick;
        _playbackCheckpointTimer.Tick -= PlaybackCheckpointTimerTick;
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
        var player = _localAudioPlayer;
        var position = Math.Max(0, player?.PositionMilliseconds ?? 0);
        var duration = Math.Max(0, player?.DurationMilliseconds ?? 0);
        var isPlaying = player?.IsPlaying == true;

        // These controls are refreshed by the 500 ms UI timer, including while
        // paused, so Android notification actions cannot leave stale icons.
        if (PlayPauseButton is not null)
            PlayPauseButton.Content = isPlaying ? "⏸" : "▶";
        if (MiniPlayPauseButton is not null)
            MiniPlayPauseButton.Content = isPlaying ? "⏸" : "▶";
        if (PlaybackSpeedButton is not null)
        {
            var speed = player?.PlaybackSpeed ?? 1f;
            PlaybackSpeedButton.Content = $"{speed:0.##}×";
        }

        if (PlaybackSeekSlider is null || PlaybackPositionLabel is null || PlaybackDurationLabel is null)
            return;

        var chapterRange = GetCurrentChapterRange(position, duration);
        var chapterElapsed = Math.Max(0, position - chapterRange.Start);
        var chapterDuration = Math.Max(0, chapterRange.End - chapterRange.Start);
        UpdateCurrentChapterUi(position, duration);

        _isUpdatingPlaybackSlider = true;
        try
        {
            PlaybackSeekSlider.Maximum = Math.Max(1, chapterDuration);
            PlaybackSeekSlider.Value = chapterDuration > 0 ? Math.Min(chapterElapsed, chapterDuration) : 0;
            PlaybackSeekSlider.IsEnabled = duration > 0 && _playingAudiobookId is not null;

            // The time row shows whole-book elapsed / total / remaining.
            PlaybackPositionLabel.Text = FormatPlaybackTime(position);
            PlaybackDurationLabel.Text = duration > 0 ? FormatPlaybackTime(duration) : "--:--";
            if (PlaybackRemainingLabel is not null)
                PlaybackRemainingLabel.Text = duration > 0
                    ? "−" + FormatPlaybackTime(Math.Max(0, duration - position))
                    : "−--:--";
        }
        finally { _isUpdatingPlaybackSlider = false; }
    }

    private (long Start, long End) GetCurrentChapterRange(long positionMilliseconds, long durationMilliseconds)
    {
        var book = _playingAudiobookId is null ? null : _viewModel.LocalAudioBooks.FirstOrDefault(item => item.Id == _playingAudiobookId);
        return AudiobookChapterService.GetChapterRange(book?.Chapters, positionMilliseconds, durationMilliseconds);
    }

    private void SetPlayerChapterPositions(Audiobook book)
    {
        if (_localAudioPlayer is not IChapterPositionAudioPlayer chapterPlayer) return;
        var chapters = AudiobookChapterService.Normalize(book.Chapters, _localAudioPlayer.DurationMilliseconds);
        chapterPlayer.SetChapterPositions(chapters.Select(chapter => chapter.StartPositionMilliseconds));
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
        var chapterDuration = Math.Max(0, end - current.StartPositionMilliseconds);
        var chapterElapsed = Math.Clamp(positionMilliseconds - current.StartPositionMilliseconds, 0, chapterDuration);
        ChapterProgressLabel.Text = chapterDuration > 0
            ? $"Chapter {currentIndex + 1} of {chapters.Count} · {FormatPlaybackTime(chapterElapsed)} / {FormatPlaybackTime(chapterDuration)}"
            : $"Chapter {currentIndex + 1} of {chapters.Count}";
    }

    private static string FormatPlaybackTime(long milliseconds)
    {
        var time = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
        return time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"m\:ss");
    }

    private void PlaybackSeekSliderPointerReleased(object? sender, Avalonia.Input.PointerReleasedEventArgs e)
    {
        if (_isUpdatingPlaybackSlider || _localAudioPlayer is null || _playingAudiobookId is null)
            return;

        var duration = _localAudioPlayer.DurationMilliseconds;
        var chapterRange = GetCurrentChapterRange(_localAudioPlayer.PositionMilliseconds, duration);
        var target = Math.Max(0, chapterRange.Start + (long)PlaybackSeekSlider.Value);
        if (duration > 0) target = Math.Min(target, duration);
        _localAudioPlayer.Seek(target);
        UpdateCurrentChapterUi(target, duration);
        UpdatePlaybackProgressUi();
        _ = _viewModel.SavePlaybackPositionAsync(_playingAudiobookId, target);
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

    private void PlaybackProgressTimerTick(object? sender, EventArgs e) => UpdatePlaybackProgressUi();

    private async void PlaybackCheckpointTimerTick(object? sender, EventArgs e)
    {
        if (_isSavingPlaybackPosition || _localAudioPlayer is null || _playingAudiobookId is null) return;
        _isSavingPlaybackPosition = true;
        try { await _viewModel.SavePlaybackPositionAsync(_playingAudiobookId, _localAudioPlayer.PositionMilliseconds); }
        catch (Exception) { /* Checkpoint failure must not interrupt playback. */ }
        finally { _isSavingPlaybackPosition = false; }
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

    private void ProbeWidevineClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => _viewModel.ProbeWidevine();

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

    private void AddAccountClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        AddAccountSheetOverlay.IsVisible = true;
    }

    private void CloseAddAccountSheetClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        AddAccountSheetOverlay.IsVisible = false;
    }

    private void AudibleAddAccountClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        AddAccountSheetOverlay.IsVisible = false;
        AudibleSignInClicked(sender, e);
    }

    private async void AudibleSignInClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            if (PlatformSignInViewModelFactory is null)
            {
                _viewModel.ReportAudioStatus("Audible sign-in is unavailable in this build.");
                return;
            }
            var vm = PlatformSignInViewModelFactory();
            await vm.BeginSignInAsync();
            await vm.LoadStatusAsync();
            await vm.LoadStatusAsync();
            _viewModel.ReportAudioStatus(vm.ConnectionStatusLabel + ". " + vm.Status);
        }
        catch (Exception ex)
        {
            _viewModel.ReportAudioStatus($"Audible sign-in failed ({ex.GetType().Name}).");
        }
    }

    private async void AudibleDisconnectClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            if (PlatformSignInViewModelFactory is null) return;
            var vm = PlatformSignInViewModelFactory();
            await vm.DisconnectAsync();
            await vm.LoadStatusAsync();
            _viewModel.ReportAudioStatus(vm.Status);
        }
        catch (Exception ex)
        {
            _viewModel.ReportAudioStatus($"Audible disconnect failed ({ex.GetType().Name}).");
        }
    }

    private void TogglePlaybackClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_localAudioPlayer?.IsPlaying == true)
            PauseAudioClicked(sender, e);
        else if (_playingAudiobookId is not null)
            ResumeAudioClicked(sender, e);
        else
            _viewModel.ReportAudioStatus("Choose an audiobook in Library and tap Play first.");
    }

    private async void ImportAudiobookClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var provider = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (provider is null) return;
        var files = await provider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import an audiobook",
            AllowMultiple = false,
            FileTypeFilter = [AudioFileType]
        });
        if (files.Count == 0) return;
        await using var stream = await files[0].OpenReadAsync();
        await _viewModel.ImportAudiobookAsync(files[0].Name, stream);
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

    private void HomeTabClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => _viewModel.SelectTab(0);
    private void AudibleLibraryTabClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => _viewModel.SelectTab(1);
    private void AudibleAccountTabClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => _viewModel.SelectTab(2);
    private void SettingsTabClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => _viewModel.SelectTab(3);

    private void OpenFullPlayerClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _viewModel.OpenFullPlayer();
        UpdatePlaybackProgressUi();
    }

    private void CloseFullPlayerClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => _viewModel.CloseFullPlayer();

    private void OpenChaptersClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => _viewModel.OpenChapters();

    private void CloseChaptersClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => _viewModel.CloseChapters();

    private async void ChapterSelectedClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not Button { Tag: long startPositionMilliseconds } || _localAudioPlayer is null || _playingAudiobookId is null)
            return;
        try
        {
            _localAudioPlayer.Seek(startPositionMilliseconds);
            await _viewModel.SavePlaybackPositionAsync(_playingAudiobookId, startPositionMilliseconds);
            _viewModel.CloseChapters();
            UpdatePlaybackProgressUi();
            _viewModel.ReportAudioStatus("Jumped to selected chapter.");
        }
        catch (Exception)
        {
            _viewModel.ReportAudioStatus("Could not open that chapter.");
        }
    }

    private void OpenBookDetailsClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Button { Tag: string audiobookId })
            _viewModel.OpenBookDetails(audiobookId);
    }
    private void CloseBookDetailsClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => _viewModel.CloseBookDetails();
    private void DetailBookMenuClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_viewModel.DetailBook is { } book) _viewModel.OpenBookMenu(book.Id);
    }
    private void EditDetailBookClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => _viewModel.BeginDetailBookEdit();
    private void CancelDetailEditClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => _viewModel.CancelDetailBookEdit();
    private async void SaveDetailEditClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => await _viewModel.SaveDetailBookEditAsync();
    private async void ChooseCoverFromDetailClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_viewModel.DetailBook is not { } book) return;
        await ChooseCoverForBookAsync(book);
    }
    private void ShowAllClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => _viewModel.SetListeningStatusFilter("All");

    private void ShowNotStartedClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => _viewModel.SetListeningStatusFilter("Not started");
    private void ShowInProgressClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => _viewModel.SetListeningStatusFilter("In progress");
    private void ShowFinishedClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => _viewModel.SetListeningStatusFilter("Finished");

    private void BookOverflowClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Button { DataContext: Audiobook book }) _viewModel.OpenBookMenu(book.Id);
    }
    private void CloseBookMenuClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => _viewModel.CloseBookMenu();
    private void BookInfoClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => _viewModel.OpenBookInfo();
    private void CloseBookInfoClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => _viewModel.CloseBookInfo();
    private void BeginSelectionClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => _viewModel.BeginSelectionMode();
    private void ToggleSelectionClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is CheckBox { DataContext: Audiobook book })
            _viewModel.ToggleAudiobookSelection(book.Id);
    }
    private void SelectAllClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => _viewModel.SelectAllLocalAudiobooks();
    private void ExitSelectionClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => _viewModel.ExitSelectionMode();
    private void OpenStatusPickerClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => _viewModel.OpenStatusPicker();
    private void CloseStatusPickerClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => _viewModel.CloseStatusPicker();
    private async void MarkFinishedClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => await _viewModel.ApplyListeningStatusAsync("Finished");
    private async void MarkInProgressClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => await _viewModel.ApplyListeningStatusAsync("In progress");
    private async void MarkNotStartedClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => await _viewModel.ApplyListeningStatusAsync("Not started");

    private void RemoveImportedBookClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_viewModel.MenuBook is not { } book) return;
        _viewModel.CloseBookMenu();
        _viewModel.RequestRemoveBook(book.Id);
    }

    private void EditImportedBookClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_viewModel.MenuBook is not { } book) return;
        _viewModel.CloseBookMenu();
        _viewModel.OpenBookDetails(book.Id);
        _viewModel.BeginDetailBookEdit();
    }

    private async void ChooseCoverFromMenuClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_viewModel.MenuBook is not { } book) return;
        _viewModel.CloseBookMenu();
        await ChooseCoverForBookAsync(book);
    }

    private async void ChooseCoverFromInfoClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_viewModel.MenuBook is not { } book) return;
        _viewModel.CloseBookInfo();
        await ChooseCoverForBookAsync(book);
    }

    private async Task ChooseCoverForBookAsync(Audiobook book)
    {
        var provider = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (provider is null) return;
        var files = await provider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose cover artwork for " + book.Title,
            AllowMultiple = false,
            FileTypeFilter = [CoverImageFileType]
        });
        if (files.Count == 0) return;
        await using var stream = await files[0].OpenReadAsync();
        await _viewModel.ImportCoverArtAsync(book.Id, files[0].Name, stream);
        _viewModel.OpenBookDetails(book.Id);
    }

    private void RemoveSelectedClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => _viewModel.RequestRemoveSelectedAudiobooks();

    private async void ConfirmRemovalClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => await _viewModel.ConfirmPendingRemovalAsync();

    private void CancelRemovalClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => _viewModel.CancelPendingRemoval();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
