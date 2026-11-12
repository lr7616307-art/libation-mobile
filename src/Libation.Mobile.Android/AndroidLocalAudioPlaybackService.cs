using Android.App;
using Android.Content;
using AndroidX.Media3.Common;
using AndroidX.Media3.ExoPlayer;
using Android.OS;
using Android.Graphics;
using Java.Util;

namespace Libation.Mobile.Android;

/// <summary>Owns audiobook playback independently of the Avalonia view lifecycle.</summary>
[Service(Name = "org.libationmobile.app.AndroidLocalAudioPlaybackService", Exported = false)]
public sealed class AndroidLocalAudioPlaybackService : Service, IPlayerListener
{
    public const string ActionPlay = "org.libationmobile.app.action.PLAY";
    public const string ActionPlayWidevineDash = "org.libationmobile.app.action.PLAY_WIDEVINE_DASH";
    public const string ActionPause = "org.libationmobile.app.action.PAUSE";
    public const string ActionResume = "org.libationmobile.app.action.RESUME";
    public const string ActionSeek = "org.libationmobile.app.action.SEEK";
    public const string ActionRewind = "org.libationmobile.app.action.REWIND";
    public const string ActionForward = "org.libationmobile.app.action.FORWARD";
    public const string ActionPreviousChapter = "org.libationmobile.app.action.PREVIOUS_CHAPTER";
    public const string ActionNextChapter = "org.libationmobile.app.action.NEXT_CHAPTER";
    public const string ActionSetChapterPositions = "org.libationmobile.app.action.SET_CHAPTER_POSITIONS";
    public const string ActionSetSpeed = "org.libationmobile.app.action.SET_SPEED";
    public const string ActionRestore = "org.libationmobile.app.action.RESTORE";
    public const string ActionSetSleepTimer = "org.libationmobile.app.action.SET_SLEEP_TIMER";
    public const string ActionCancelSleepTimer = "org.libationmobile.app.action.CANCEL_SLEEP_TIMER";
    public const string ActionSetArtwork = "org.libationmobile.app.action.SET_ARTWORK";
    public const string ExtraFilePath = "file_path";
    public const string ExtraManifestUri = "widevine_manifest_uri";
    public const string ExtraOfflineKeySetId = "widevine_offline_key_set_id";
    public const string ExtraTitle = "title";
    public const string ExtraArtworkPath = "artwork_path";
    public const string ExtraChapterPositions = "chapter_positions";
    public const string ExtraPosition = "position_ms";
    public const string ExtraSpeed = "playback_speed";
    public const string ExtraAutoPlay = "auto_play";
    public const string ExtraSleepDeadlineUtcTicks = "sleep_deadline_utc_ticks";
    public const string ExtraSleepChapterPosition = "sleep_chapter_position";

    private const string ChannelId = "libation_audiobook_playback";
    private const int NotificationId = 4102;
    private static readonly UUID WidevineUuid = UUID.FromString("edef8ba9-79d6-4ace-a3c8-27dcd51d21ed");
    private IExoPlayer? _player;
    private string _title = "Audiobook";
    private string? _currentFilePath;
    private bool _isWidevinePlayback;
    private string? _coverImagePath;
    private Bitmap? _coverBitmap;
    private float _playbackSpeed = 1f;
    private Handler? _checkpointHandler;
    private Java.Lang.Runnable? _checkpointRunnable;
    private Java.Lang.Runnable? _notificationRunnable;
    private Java.Lang.Runnable? _sleepTimerRunnable;
    private long? _sleepDeadlineUtcTicks;
    private long? _sleepChapterPositionMilliseconds;

    public static AndroidLocalAudioPlaybackService? Current { get; private set; }
    public static event Action? PlaybackStateChanged;
    public static bool HasActivePlayer => Current?._player is not null;
    public static bool IsPlaying => Current?._player?.IsPlaying ?? false;
    public static string? CurrentFilePath => Current?._currentFilePath;
    public static long PositionMilliseconds => Current?.ReadPosition() ?? 0;
    public static long DurationMilliseconds => Current?.ReadDuration() ?? 0;
    public static float PlaybackSpeed => Current?._playbackSpeed ?? 1f;

    public override void OnCreate()
    {
        base.OnCreate();
        Current = this;
        PublishPlaybackState();
        CreateNotificationChannel();
        _checkpointHandler = new Handler(Looper.MainLooper!);
        _checkpointRunnable = new Java.Lang.Runnable(() =>
        {
            SavePlaybackSnapshot();
            if (_checkpointRunnable is not null)
                _checkpointHandler?.PostDelayed(_checkpointRunnable, 5_000);
        });
        _checkpointHandler.PostDelayed(_checkpointRunnable, 5_000);
        _notificationRunnable = new Java.Lang.Runnable(() =>
        {
            UpdateNotification();
            if (_notificationRunnable is not null)
                _checkpointHandler?.PostDelayed(_notificationRunnable, 1_000);
        });
        _checkpointHandler.PostDelayed(_notificationRunnable, 1_000);
        RestoreSleepTimer();
        _sleepTimerRunnable = new Java.Lang.Runnable(CheckSleepTimer);
        _checkpointHandler.PostDelayed(_sleepTimerRunnable, 1_000);
    }

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        try
        {
            switch (intent?.Action)
            {
                case ActionPlay:
                    StartPlayback(intent);
                    break;
                case ActionPlayWidevineDash:
                    StartWidevineDashPlayback(intent);
                    break;
                case ActionPause:
                    if (_player?.IsPlaying == true) _player.Pause();
                    SavePlaybackSnapshot();
                    UpdateNotification();
                    break;
                case ActionResume:
                    if (_player is not null && !_player.IsPlaying) _player.Play();
                    SavePlaybackSnapshot();
                    UpdateNotification();
                    break;
                case ActionSeek:
                    if (_player is not null)
                    {
                        var target = Math.Clamp(intent.GetLongExtra(ExtraPosition, 0), 0, int.MaxValue);
                        _player.SeekTo((int)target);
                        SavePlaybackSnapshot();
                    }
                    break;
                case ActionRewind:
                    SeekBy(-10_000);
                    break;
                case ActionForward:
                    SeekBy(10_000);
                    break;
                case ActionPreviousChapter:
                    SeekChapter(forward: false);
                    break;
                case ActionNextChapter:
                    SeekChapter(forward: true);
                    break;
                case ActionSetSpeed:
                    SetPlaybackSpeed(intent.GetFloatExtra(ExtraSpeed, 1f));
                    SavePlaybackSnapshot();
                    break;
                case ActionRestore:
                    RestoreSavedPlayback();
                    break;
                case ActionSetSleepTimer:
                    SetSleepTimer(intent);
                    break;
                case ActionCancelSleepTimer:
                    ClearSleepTimer();
                    break;
                case ActionSetArtwork:
                    SetArtwork(intent.GetStringExtra(ExtraArtworkPath));
                    SavePlaybackSnapshot();
                    UpdateNotification();
                    break;
                case ActionSetChapterPositions:
                    SetChapterPositions(intent.GetLongArrayExtra(ExtraChapterPositions));
                    UpdateNotification();
                    break;
            }
        }
        catch (Exception)
        {
            // A transient restore failure must not erase the only saved
            // position. A later app launch can retry recovery.
            // A failed command must not discard the last known-good resume point.
            // This also protects the previous audiobook when a new Play request is invalid.
            StopPlayback(clearRecovery: false);
            StopSelf(startId);
        }

        PublishPlaybackState();
        return StartCommandResult.NotSticky;
    }

    private void StartPlayback(Intent intent)
    {
        var path = intent.GetStringExtra(ExtraFilePath);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new FileNotFoundException("Audiobook audio file was not found.", path);

        StopPlayer();
        _title = intent.GetStringExtra(ExtraTitle);
        if (string.IsNullOrWhiteSpace(_title))
            _title = System.IO.Path.GetFileNameWithoutExtension(path);
        _currentFilePath = path;
        _isWidevinePlayback = false;
        SetArtwork(intent.GetStringExtra(ExtraArtworkPath));
        _playbackSpeed = 1f;

        // Android requires a service started with startForegroundService() to
        // promote itself promptly. MediaPlayer.Prepare() can block while a
        // large local audiobook is inspected, so promote before preparing it.
        StartForeground(NotificationId, BuildNotification());

        _player = new ExoPlayerBuilder(this).Build();
        _player.AddListener(this);
        var mediaUri = global::Android.Net.Uri.FromFile(new Java.IO.File(path));
        _player.SetMediaItem(MediaItem.FromUri(mediaUri));
        var position = Math.Max(0L, intent.GetLongExtra(ExtraPosition, 0));
        if (position > 0) _player.SeekTo(position);
        _player.Prepare();
        var speed = intent.GetFloatExtra(ExtraSpeed, 1f);
        if (!float.IsNaN(speed) && !float.IsInfinity(speed))
        {
            _playbackSpeed = Math.Clamp(speed, 0.5f, 3f);
            _player.PlaybackParameters = new PlaybackParameters(_playbackSpeed, 1f);
        }
        if (intent.GetBooleanExtra(ExtraAutoPlay, true))
            _player.Play();
        SavePlaybackSnapshot();
        UpdateNotification();
        PublishPlaybackState();
    }


    private void StartWidevineDashPlayback(Intent intent)
    {
        var manifestValue = intent.GetStringExtra(ExtraManifestUri);
        if (string.IsNullOrWhiteSpace(manifestValue) ||
            !Uri.TryCreate(manifestValue, UriKind.Absolute, out var manifestUri) ||
            !string.Equals(manifestUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("A valid HTTPS Audible DASH manifest is required.");

        var keySetId = intent.GetByteArrayExtra(ExtraOfflineKeySetId);
        if (keySetId is null || keySetId.Length is 0 or > 1024)
            throw new InvalidDataException("A saved Android Widevine offline key-set identifier is required.");

        StopPlayer();
        _title = intent.GetStringExtra(ExtraTitle);
        if (string.IsNullOrWhiteSpace(_title))
            _title = "Audible audiobook";
        _currentFilePath = null; // Protected remote media is never written as a local clear-audio path.
        _isWidevinePlayback = true;
        SetArtwork(intent.GetStringExtra(ExtraArtworkPath));
        _playbackSpeed = 1f;

        StartForeground(NotificationId, BuildNotification());

        var drmConfiguration = new MediaItem.DrmConfiguration.Builder(WidevineUuid)
            .SetKeySetId(keySetId)
            .Build();
        var mediaItem = new MediaItem.Builder()
            .SetUri(global::Android.Net.Uri.Parse(manifestUri.AbsoluteUri))
            .SetMimeType("application/dash+xml")
            .SetDrmConfiguration(drmConfiguration)
            .Build();

        _player = new ExoPlayerBuilder(this).Build();
        _player.AddListener(this);
        _player.SetMediaItem(mediaItem);
        var position = Math.Max(0L, intent.GetLongExtra(ExtraPosition, 0));
        if (position > 0) _player.SeekTo(position);
        _player.Prepare();

        var speed = intent.GetFloatExtra(ExtraSpeed, 1f);
        if (!float.IsNaN(speed) && !float.IsInfinity(speed))
        {
            _playbackSpeed = Math.Clamp(speed, 0.5f, 3f);
            _player.PlaybackParameters = new PlaybackParameters(_playbackSpeed, 1f);
        }
        if (intent.GetBooleanExtra(ExtraAutoPlay, true))
            _player.Play();

        // Remove the byte array from the service command as soon as Media3's
        // immutable MediaItem has been built. It is not copied to recovery state.
        intent.RemoveExtra(ExtraOfflineKeySetId);
        UpdateNotification();
        PublishPlaybackState();
    }


    // Media3 player callbacks keep the foreground notification and service
    // lifecycle synchronized with the actual decoder state.
    public void OnPlaybackStateChanged(int playbackState)
    {
        if (playbackState == 4) // Player.STATE_ENDED
        {
            StopPlayback();
            StopSelf();
            return;
        }

        UpdateNotification();
        PublishPlaybackState();
    }

    public void OnIsPlayingChanged(bool isPlaying)
    {
        SavePlaybackSnapshot();
        UpdateNotification();
        PublishPlaybackState();
    }

    public void OnPlayerError(PlaybackException? error)
    {
        // Decoder or source errors should stop playback, but must not erase the
        // last recoverable position. Capture the best available position before
        // releasing ExoPlayer so the user can retry after reopening the app.
        SavePlaybackSnapshot();
        StopPlayback(clearRecovery: false);
        StopSelf();
        PublishPlaybackState();
    }

    public void OnAudioAttributesChanged(AudioAttributes? value) { }
    public void OnAudioSessionIdChanged(int value) { }
    public void OnAvailableCommandsChanged(PlayerCommands? value) { }
    public void OnCues(AndroidX.Media3.Common.Text.CueGroup? value) { }
    public void OnDeviceInfoChanged(DeviceInfo? value) { }
    public void OnDeviceVolumeChanged(int volume, bool muted) { }
    public void OnEvents(IPlayer? player, PlayerEvents? events) { }
    public void OnIsLoadingChanged(bool isLoading) { }
    public void OnLoadingChanged(bool isLoading) { }
    public void OnMaxSeekToPreviousPositionChanged(long value) { }
    public void OnMediaItemTransition(MediaItem? mediaItem, int reason) { }
    public void OnMediaMetadataChanged(MediaMetadata? metadata) { }
    public void OnMetadata(Metadata? metadata) { }
    public void OnPlayWhenReadyChanged(bool playWhenReady, int reason) => UpdateNotification();
    public void OnPositionDiscontinuity(PlayerPositionInfo? oldPosition, PlayerPositionInfo? newPosition, int reason)
    {
        SavePlaybackSnapshot();
        PublishPlaybackState();
    }
    public void OnPlaybackParametersChanged(PlaybackParameters? parameters)
    {
        if (parameters is not null && float.IsFinite(parameters.Speed) && parameters.Speed > 0)
            _playbackSpeed = Math.Clamp(parameters.Speed, 0.5f, 3f);
        SavePlaybackSnapshot();
        UpdateNotification();
        PublishPlaybackState();
    }
    public void OnPlaybackSuppressionReasonChanged(int reason) { }
    public void OnPlayerErrorChanged(PlaybackException? error) { }
    public void OnPlaylistMetadataChanged(MediaMetadata? metadata) { }
    public void OnRenderedFirstFrame() { }
    public void OnRepeatModeChanged(int repeatMode) { }
    public void OnSeekBackIncrementChanged(long value) { }
    public void OnSeekForwardIncrementChanged(long value) { }
    public void OnShuffleModeEnabledChanged(bool enabled) { }
    public void OnSkipSilenceEnabledChanged(bool enabled) { }
    public void OnSurfaceSizeChanged(int width, int height) { }
    public void OnTimelineChanged(Timeline? timeline, int reason) { }
    public void OnTrackSelectionParametersChanged(TrackSelectionParameters? parameters) { }
    public void OnTracksChanged(Tracks? tracks) { }
    public void OnVideoSizeChanged(VideoSize? size) { }

    private void RestoreSavedPlayback()
    {
        var snapshot = PlaybackRecoveryStore.Load(this);
        if (snapshot is null) return;
        if (!File.Exists(snapshot.FilePath))
        {
            PlaybackRecoveryStore.Clear(this);
            return;
        }

        var intent = new Intent(this, typeof(AndroidLocalAudioPlaybackService));
        intent.PutExtra(ExtraFilePath, snapshot.FilePath);
        intent.PutExtra(ExtraTitle, snapshot.Title);
        intent.PutExtra(ExtraArtworkPath, snapshot.CoverImagePath);
        intent.PutExtra(ExtraPosition, snapshot.PositionMilliseconds);
        intent.PutExtra(ExtraSpeed, snapshot.PlaybackSpeed);
        intent.PutExtra(ExtraAutoPlay, snapshot.WasPlaying);
        StartPlayback(intent);
    }

    private void SetSleepTimer(Intent intent)
    {
        var deadline = intent.GetLongExtra(ExtraSleepDeadlineUtcTicks, 0);
        var chapterPosition = intent.GetLongExtra(ExtraSleepChapterPosition, -1);
        _sleepDeadlineUtcTicks = deadline > 0 ? deadline : null;
        _sleepChapterPositionMilliseconds = chapterPosition >= 0 ? chapterPosition : null;
        PersistSleepTimer();
    }

    private void RestoreSleepTimer()
    {
        var preferences = GetSharedPreferences("vorath_sleep_timer", FileCreationMode.Private);
        var deadline = preferences?.GetLong("deadline_utc_ticks", 0) ?? 0;
        var chapter = preferences?.GetLong("chapter_position_ms", -1) ?? -1;
        _sleepDeadlineUtcTicks = deadline > 0 ? deadline : null;
        _sleepChapterPositionMilliseconds = chapter >= 0 ? chapter : null;
    }

    private void PersistSleepTimer()
    {
        var editor = GetSharedPreferences("vorath_sleep_timer", FileCreationMode.Private)?.Edit();
        editor?.PutLong("deadline_utc_ticks", _sleepDeadlineUtcTicks ?? 0);
        editor?.PutLong("chapter_position_ms", _sleepChapterPositionMilliseconds ?? -1);
        editor?.Apply();
    }

    private void ClearSleepTimer()
    {
        _sleepDeadlineUtcTicks = null;
        _sleepChapterPositionMilliseconds = null;
        PersistSleepTimer();
    }

    private void CheckSleepTimer()
    {
        try
        {
            var deadlineReached = _sleepDeadlineUtcTicks is { } deadline && DateTime.UtcNow.Ticks >= deadline;
            var chapterReached = _sleepChapterPositionMilliseconds is { } chapter && _player?.IsPlaying == true && ReadPosition() >= chapter;
            if (deadlineReached || chapterReached)
            {
                if (_player?.IsPlaying == true) _player.Pause();
                SavePlaybackSnapshot();
                ClearSleepTimer();
                UpdateNotification();
            }
        }
        catch (Exception) { /* Timer checks must never interrupt playback. */ }
        finally
        {
            if (_sleepTimerRunnable is not null) _checkpointHandler?.PostDelayed(_sleepTimerRunnable, 1_000);
        }
    }
    private void SavePlaybackSnapshot()
    {
        if (_player is null || string.IsNullOrWhiteSpace(_currentFilePath)) return;
        try
        {
            PlaybackRecoveryStore.Save(this, new PlaybackRecoverySnapshot(
                _currentFilePath, _title, ReadPosition(), ReadDuration(), _playbackSpeed, _player.IsPlaying, _coverImagePath));
        }
        catch (Exception) { /* A checkpoint must never interrupt audio playback. */ }
    }

    private void SetPlaybackSpeed(float speed)
    {
        if (_player is null || float.IsNaN(speed) || float.IsInfinity(speed)) return;
        speed = Math.Clamp(speed, 0.5f, 3f);
        _player.PlaybackParameters = new PlaybackParameters(speed, 1f);
        _playbackSpeed = speed;
    }

    private void SeekChapter(bool forward)
    {
        if (_player is null || string.IsNullOrWhiteSpace(_currentFilePath)) return;
        // Chapter indexes are supplied by the imported catalogue to the
        // foreground app. Native notification actions cannot query that UI state.
        // The service instead advances to the previous/next embedded chapter
        // positions cached in its playback session.
        var chapters = _chapterPositions;
        if (chapters.Length == 0) return;
        var position = ReadPosition();
        var current = Array.FindLastIndex(chapters, start => start <= position);
        var targetIndex = forward
            ? Math.Min(chapters.Length - 1, current + 1)
            : Math.Max(0, current - (current >= 0 && position - chapters[current] > 5_000 ? 0 : 1));
        if (targetIndex >= 0) _player.SeekTo(chapters[targetIndex]);
        SavePlaybackSnapshot();
        UpdateNotification();
    }

    private long[] _chapterPositions = [];
    private void SetChapterPositions(long[]? positions)
        => _chapterPositions = positions?.Where(x => x >= 0).Distinct().OrderBy(x => x).ToArray() ?? [];

    private void SeekBy(long offsetMilliseconds)
    {
        if (_player is null) return;
        var current = Math.Max(0L, _player.CurrentPosition);
        var target = Math.Max(0L, current + offsetMilliseconds);
        var duration = _player.Duration;
        if (duration > 0) target = Math.Min(target, duration);
        _player.SeekTo((int)Math.Min(target, int.MaxValue));
    }

    private void CreateNotificationChannel()
    {
        if (Build.VERSION.SdkInt < BuildVersionCodes.O) return;
        var manager = (NotificationManager?)GetSystemService(NotificationService);
        manager?.CreateNotificationChannel(new NotificationChannel(
            ChannelId, "Audiobook playback", NotificationImportance.Low)
        {
            Description = "Controls for audiobook playback"
        });
    }

    private Notification BuildNotification()
    {
        var builder = Build.VERSION.SdkInt >= BuildVersionCodes.O
            ? new Notification.Builder(this, ChannelId)
            : new Notification.Builder(this);
        var launchIntent = new Intent(this, typeof(MainActivity));
        var contentPendingIntent = PendingIntent.GetActivity(
            this, 4102, launchIntent,
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
        var isPlaying = _player?.IsPlaying == true;
        var action = isPlaying ? ActionPause : ActionResume;
        var actionLabel = isPlaying ? "Pause" : "Play";
        var actionIcon = isPlaying
            ? global::Android.Resource.Drawable.IcMediaPause
            : global::Android.Resource.Drawable.IcMediaPlay;
        var controlIntent = new Intent(this, typeof(AndroidLocalAudioPlaybackService)).SetAction(action);
        var controlPendingIntent = PendingIntent.GetService(
            this, 4103, controlIntent,
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);

        var previousIntent = new Intent(this, typeof(AndroidLocalAudioPlaybackService)).SetAction(ActionPreviousChapter);
        var previousPendingIntent = PendingIntent.GetService(
            this, 4106, previousIntent,
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
        var nextIntent = new Intent(this, typeof(AndroidLocalAudioPlaybackService)).SetAction(ActionNextChapter);
        var nextPendingIntent = PendingIntent.GetService(
            this, 4107, nextIntent,
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);

        var rewindIntent = new Intent(this, typeof(AndroidLocalAudioPlaybackService)).SetAction(ActionRewind);
        var rewindPendingIntent = PendingIntent.GetService(
            this, 4104, rewindIntent,
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
        var forwardIntent = new Intent(this, typeof(AndroidLocalAudioPlaybackService)).SetAction(ActionForward);
        var forwardPendingIntent = PendingIntent.GetService(
            this, 4105, forwardIntent,
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);

        builder.SetSmallIcon(Resource.Drawable.vorath_notification)
            .SetContentTitle(_title)
            .SetSubText("VØRATH")
            .SetContentText(FormatChapterProgressText())
            .SetContentIntent(contentPendingIntent)
            .SetOngoing(_player?.IsPlaying == true)
            .SetOnlyAlertOnce(true)
            .AddAction(global::Android.Resource.Drawable.IcMediaPrevious, "Previous chapter", previousPendingIntent)
            .AddAction(global::Android.Resource.Drawable.IcMediaRew, "−10 sec", rewindPendingIntent)
            .AddAction(actionIcon, actionLabel, controlPendingIntent)
            .AddAction(global::Android.Resource.Drawable.IcMediaFf, "+10 sec", forwardPendingIntent)
            .AddAction(global::Android.Resource.Drawable.IcMediaNext, "Next chapter", nextPendingIntent);
        if (_coverBitmap is not null) builder.SetLargeIcon(_coverBitmap);
        // Android's media style gives the expanded notification a dedicated artwork area.
        builder.SetStyle(new Notification.MediaStyle().SetShowActionsInCompactView(0, 2, 4));
        var duration = ReadDuration();
        if (duration > 0)
            builder.SetProgress((int)Math.Min(duration, int.MaxValue),
                (int)Math.Min(ReadPosition(), int.MaxValue), false);
        return builder.Build();
    }

    private void SetArtwork(string? path)
    {
        _coverImagePath = !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? path : null;
        _coverBitmap?.Recycle();
        _coverBitmap?.Dispose();
        _coverBitmap = _coverImagePath is null ? null : BitmapFactory.DecodeFile(_coverImagePath);
    }

    private static string FormatTime(long milliseconds)
    {
        if (milliseconds <= 0) return "--:--";
        var value = TimeSpan.FromMilliseconds(milliseconds);
        return value.TotalHours >= 1 ? value.ToString(@"h\:mm\:ss") : value.ToString(@"m\:ss");
    }
    
    private string FormatChapterProgressText()
    {
        var position = ReadPosition();
        var duration = ReadDuration();
        if (_chapterPositions.Length == 0)
            return $"{FormatTime(position)} / {FormatTime(duration)}";
        var index = Array.FindLastIndex(_chapterPositions, start => start <= position);
        if (index < 0) return $"{FormatTime(position)} / {FormatTime(duration)}";
        var start = _chapterPositions[index];
        var end = index + 1 < _chapterPositions.Length ? _chapterPositions[index + 1] : duration;
        return $"{FormatTime(Math.Max(0, position - start))} / {FormatTime(Math.Max(0, end - start))}";
    }

    private void PublishPlaybackState()
    {
        try { PlaybackStateChanged?.Invoke(); }
        catch (Exception) { /* UI observers must never interrupt playback or notification updates. */ }
    }

    private void UpdateNotification()
    {
        var manager = (NotificationManager?)GetSystemService(NotificationService);
        manager?.Notify(NotificationId, BuildNotification());
    }

    private long ReadPosition()
    {
        try { return _player is null ? 0 : Math.Max(0, _player.CurrentPosition); }
        catch (Exception) { return 0; }
    }

    private long ReadDuration()
    {
        try { return _player is null ? 0 : Math.Max(0, _player.Duration); }
        catch (Exception) { return 0; }
    }

    private void StopPlayer()
    {
        if (_player is null) return;
        try { _player.Stop(); } catch (Exception) { }
        _player.Release();
        _player.Dispose();
        _player = null;
        _currentFilePath = null;
    }

    private void StopPlayback(bool clearRecovery = true)
    {
        StopPlayer();
        if (clearRecovery && !_isWidevinePlayback) PlaybackRecoveryStore.Clear(this);
        _isWidevinePlayback = false;
        StopForeground(StopForegroundFlags.Remove);
    }

    public override IBinder? OnBind(Intent? intent) => null;

    public override void OnTaskRemoved(Intent? rootIntent)
    {
        SavePlaybackSnapshot();
        base.OnTaskRemoved(rootIntent);
    }

    public override void OnDestroy()
    {
        SavePlaybackSnapshot();
        if (_checkpointRunnable is not null)
            _checkpointHandler?.RemoveCallbacks(_checkpointRunnable);
        if (_notificationRunnable is not null)
            _checkpointHandler?.RemoveCallbacks(_notificationRunnable);
        if (_sleepTimerRunnable is not null)
            _checkpointHandler?.RemoveCallbacks(_sleepTimerRunnable);
        _checkpointRunnable?.Dispose();
        _checkpointRunnable = null;
        _notificationRunnable?.Dispose();
        _notificationRunnable = null;
        _sleepTimerRunnable?.Dispose();
        _sleepTimerRunnable = null;
        _checkpointHandler = null;
        StopPlayback(clearRecovery: false);
        _coverBitmap?.Recycle();
        _coverBitmap?.Dispose();
        _coverBitmap = null;
        if (Current == this) Current = null;
        PublishPlaybackState();
        base.OnDestroy();
    }
}
