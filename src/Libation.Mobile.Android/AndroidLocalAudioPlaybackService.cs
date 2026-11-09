using Android.App;
using Android.Content;
using AndroidX.Media3.Common;
using AndroidX.Media3.ExoPlayer;
using Android.OS;

namespace Libation.Mobile.Android;

/// <summary>Owns audiobook playback independently of the Avalonia view lifecycle.</summary>
[Service(Name = "org.libationmobile.app.AndroidLocalAudioPlaybackService", Exported = false)]
public sealed class AndroidLocalAudioPlaybackService : Service, IPlayerListener
{
    public const string ActionPlay = "org.libationmobile.app.action.PLAY";
    public const string ActionPause = "org.libationmobile.app.action.PAUSE";
    public const string ActionResume = "org.libationmobile.app.action.RESUME";
    public const string ActionSeek = "org.libationmobile.app.action.SEEK";
    public const string ActionRewind = "org.libationmobile.app.action.REWIND";
    public const string ActionForward = "org.libationmobile.app.action.FORWARD";
    public const string ActionSetSpeed = "org.libationmobile.app.action.SET_SPEED";
    public const string ActionRestore = "org.libationmobile.app.action.RESTORE";
    public const string ActionSetSleepTimer = "org.libationmobile.app.action.SET_SLEEP_TIMER";
    public const string ActionCancelSleepTimer = "org.libationmobile.app.action.CANCEL_SLEEP_TIMER";
    public const string ExtraFilePath = "file_path";
    public const string ExtraTitle = "title";
    public const string ExtraPosition = "position_ms";
    public const string ExtraSpeed = "playback_speed";
    public const string ExtraAutoPlay = "auto_play";
    public const string ExtraSleepDeadlineUtcTicks = "sleep_deadline_utc_ticks";
    public const string ExtraSleepChapterPosition = "sleep_chapter_position";

    private const string ChannelId = "libation_audiobook_playback";
    private const int NotificationId = 4102;
    private IExoPlayer? _player;
    private string _title = "Audiobook";
    private string? _currentFilePath;
    private float _playbackSpeed = 1f;
    private Handler? _checkpointHandler;
    private Java.Lang.Runnable? _checkpointRunnable;
    private Java.Lang.Runnable? _sleepTimerRunnable;
    private long? _sleepDeadlineUtcTicks;
    private long? _sleepChapterPositionMilliseconds;
    private long _initialPositionMilliseconds;
    private bool _hasReachedReady;

    public static AndroidLocalAudioPlaybackService? Current { get; private set; }
    public static bool IsPlaying => Current?._player?.IsPlaying ?? false;
    public static string? CurrentFilePath => Current?._currentFilePath;
    public static long PositionMilliseconds => Current?.ReadPosition() ?? 0;
    public static long DurationMilliseconds => Current?.ReadDuration() ?? 0;
    public static float PlaybackSpeed => Current?._playbackSpeed ?? 1f;

    public override void OnCreate()
    {
        base.OnCreate();
        Current = this;
        CreateNotificationChannel();
        _checkpointHandler = new Handler(Looper.MainLooper!);
        _checkpointRunnable = new Java.Lang.Runnable(() =>
        {
            SavePlaybackSnapshot();
            if (_checkpointRunnable is not null)
                _checkpointHandler?.PostDelayed(_checkpointRunnable, 5_000);
        });
        _checkpointHandler.PostDelayed(_checkpointRunnable, 5_000);
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
                case ActionPause:
                    if (_player?.PlayWhenReady == true) _player.Pause();
                    SavePlaybackSnapshot(durable: true);
                    UpdateNotification();
                    break;
                case ActionResume:
                    if (_player is not null && !_player.PlayWhenReady) _player.Play();
                    SavePlaybackSnapshot(durable: true, wasPlayingOverride: true);
                    UpdateNotification();
                    break;
                case ActionSeek:
                    if (_player is not null)
                    {
                        var target = Math.Clamp(intent.GetLongExtra(ExtraPosition, 0), 0, int.MaxValue);
                        _player.SeekTo((int)target);
                        SavePlaybackSnapshot(durable: true);
                    }
                    break;
                case ActionRewind:
                    SeekBy(-15_000);
                    break;
                case ActionForward:
                    SeekBy(15_000);
                    break;
                case ActionSetSpeed:
                    SetPlaybackSpeed(intent.GetFloatExtra(ExtraSpeed, 1f));
                    // Playback speed is an explicit user setting. Flush it with
                    // the current position so a sudden shutdown does not revert
                    // the listener's chosen speed on the next recovery.
                    SavePlaybackSnapshot(durable: true);
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
            }
        }
        catch (Exception)
        {
            // Capture the latest live position before releasing the player.
            // Keeping only the previous periodic snapshot could lose progress
            // made since that checkpoint when a command fails.
            SavePlaybackSnapshot(durable: true);
            // A failed command must not turn a transient error into permanent
            // progress loss; retain the snapshot for the next service start.
            StopPlayback(clearRecovery: false);
            StopSelf(startId);
        }

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
            _title = Path.GetFileNameWithoutExtension(path);
        _currentFilePath = path;
        _playbackSpeed = 1f;
        _initialPositionMilliseconds = Math.Max(0L, intent.GetLongExtra(ExtraPosition, 0));
        _hasReachedReady = false;

        // Android requires a service started with startForegroundService() to
        // promote itself promptly. MediaPlayer.Prepare() can block while a
        // large local audiobook is inspected, so promote before preparing it.
        StartForeground(NotificationId, BuildNotification());

        _player = new ExoPlayerBuilder(this).Build();
        _player.AddListener(this);
        var mediaUri = global::Android.Net.Uri.FromFile(new Java.IO.File(path));
        _player.SetMediaItem(MediaItem.FromUri(mediaUri));
        var position = _initialPositionMilliseconds;
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
        // ExoPlayer prepares asynchronously. Reading CurrentPosition immediately
        // after Play() can still return zero and overwrite the resume point we
        // just restored. Persist the requested seek position and intended play
        // state first; periodic checkpoints replace these values once the
        // decoder reports its real position and duration.
        PlaybackRecoveryStore.Save(this, new PlaybackRecoverySnapshot(
            path, _title, position, 0, _playbackSpeed,
            intent.GetBooleanExtra(ExtraAutoPlay, true)), flushImmediately: true);
        UpdateNotification();
    }


    // Media3 player callbacks keep the foreground notification and service
    // lifecycle synchronized with the actual decoder state.
    public void OnPlaybackStateChanged(int playbackState)
    {
        if (playbackState == 3) // Player.STATE_READY
        {
            // Until the first READY callback, CurrentPosition can still report
            // zero even though the requested resume seek has not been applied.
            // From this point onward ExoPlayer owns the authoritative position.
            _hasReachedReady = true;
            SavePlaybackSnapshot();
        }

        if (playbackState == 4) // Player.STATE_ENDED
        {
            StopPlayback();
            StopSelf();
            return;
        }

        UpdateNotification();
    }

    public void OnIsPlayingChanged(bool isPlaying) => UpdateNotification();

    public void OnPlayerError(PlaybackException? error)
    {
        // Decoder and I/O errors can be transient (for example, storage becoming
        // temporarily unavailable). Capture the last known position before
        // releasing ExoPlayer, but retain the recovery snapshot so the next app
        // launch can retry instead of silently losing the listening session.
        SavePlaybackSnapshot(durable: true);
        StopPlayback(clearRecovery: false);
        StopSelf();
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
        // Seek and media-position discontinuities can be followed by an abrupt
        // process death. Persist the new position immediately instead of waiting
        // for the next five-second periodic checkpoint.
        SavePlaybackSnapshot(durable: true);
    }
    public void OnPlaybackParametersChanged(PlaybackParameters? parameters) { }
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
        // Setting, cancelling, or expiring a sleep timer is an explicit state
        // transition. Flush it before returning so process death cannot restore
        // a timer the listener already cancelled or allow a cancelled timer to
        // unexpectedly pause playback later.
        editor?.Commit();
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
            var chapterReached = _sleepChapterPositionMilliseconds is { } chapter && _player?.PlayWhenReady == true && ReadPosition() >= chapter;
            if (deadlineReached || chapterReached)
            {
                if (_player?.PlayWhenReady == true) _player.Pause();
                SavePlaybackSnapshot(durable: true);
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
    private void SavePlaybackSnapshot(bool durable = false, bool? wasPlayingOverride = null)
    {
        if (_player is null || string.IsNullOrWhiteSpace(_currentFilePath)) return;
        try
        {
            var position = _hasReachedReady ? ReadPosition() : _initialPositionMilliseconds;
            PlaybackRecoveryStore.Save(this, new PlaybackRecoverySnapshot(
                _currentFilePath, _title, position, ReadDuration(), _playbackSpeed,
                // IsPlaying is false while buffering or temporarily suppressed.
                // PlayWhenReady records the listener's intent, so recovery can
                // resume a session that was meant to be playing before shutdown.
                wasPlayingOverride ?? _player.PlayWhenReady), durable);
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
        var wantsPlayback = _player?.PlayWhenReady == true;
        var action = wantsPlayback ? ActionPause : ActionResume;
        var actionLabel = wantsPlayback ? "Pause" : "Resume";
        var actionIcon = _player?.IsPlaying == true
            ? global::Android.Resource.Drawable.StatSysHeadset
            : global::Android.Resource.Drawable.StatSysHeadset;
        var controlIntent = new Intent(this, typeof(AndroidLocalAudioPlaybackService)).SetAction(action);
        var controlPendingIntent = PendingIntent.GetService(
            this, 4103, controlIntent,
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);

        var rewindIntent = new Intent(this, typeof(AndroidLocalAudioPlaybackService)).SetAction(ActionRewind);
        var rewindPendingIntent = PendingIntent.GetService(
            this, 4104, rewindIntent,
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
        var forwardIntent = new Intent(this, typeof(AndroidLocalAudioPlaybackService)).SetAction(ActionForward);
        var forwardPendingIntent = PendingIntent.GetService(
            this, 4105, forwardIntent,
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);

        builder.SetSmallIcon(global::Android.Resource.Drawable.StatSysHeadset)
            .SetContentTitle("VØRATH")
            .SetContentText(_title)
            .SetContentIntent(contentPendingIntent)
            .SetOngoing(wantsPlayback)
            .SetOnlyAlertOnce(true)
            .AddAction(global::Android.Resource.Drawable.IcMediaRew, "−15 sec", rewindPendingIntent)
            .AddAction(actionIcon, actionLabel, controlPendingIntent)
            .AddAction(global::Android.Resource.Drawable.IcMediaFf, "+15 sec", forwardPendingIntent);
        return builder.Build();
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
        if (clearRecovery) PlaybackRecoveryStore.Clear(this);
        StopForeground(StopForegroundFlags.Remove);
    }

    public override IBinder? OnBind(Intent? intent) => null;

    public override void OnTaskRemoved(Intent? rootIntent)
    {
        SavePlaybackSnapshot(durable: true);
        base.OnTaskRemoved(rootIntent);
    }

    public override void OnDestroy()
    {
        SavePlaybackSnapshot(durable: true);
        if (_checkpointRunnable is not null)
            _checkpointHandler?.RemoveCallbacks(_checkpointRunnable);
        if (_sleepTimerRunnable is not null)
            _checkpointHandler?.RemoveCallbacks(_sleepTimerRunnable);
        _checkpointRunnable?.Dispose();
        _checkpointRunnable = null;
        _sleepTimerRunnable?.Dispose();
        _sleepTimerRunnable = null;
        _checkpointHandler = null;
        StopPlayback(clearRecovery: false);
        if (Current == this) Current = null;
        base.OnDestroy();
    }
}
