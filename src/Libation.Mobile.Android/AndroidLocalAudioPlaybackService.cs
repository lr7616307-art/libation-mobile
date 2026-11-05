using Android.App;
using Android.Content;
using AndroidX.Media3.Common;
using AndroidX.Media3.ExoPlayer;
using Android.OS;
using Android.Media;
using Android.Media.Session;

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
    private bool _isRestoringPlayback;
    private MediaSession? _mediaSession;
    private Handler? _checkpointHandler;
    private Java.Lang.Runnable? _checkpointRunnable;
    private Java.Lang.Runnable? _sleepTimerRunnable;
    private long? _sleepDeadlineUtcTicks;
    private long? _sleepChapterPositionMilliseconds;

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
        CreateMediaSession();
        _checkpointHandler = new Handler(Looper.MainLooper!);
        _checkpointRunnable = new Java.Lang.Runnable(() =>
        {
            SavePlaybackSnapshot();
            if (_checkpointRunnable is not null)
                _checkpointHandler?.PostDelayed(_checkpointRunnable, 2_000);
        });
        _checkpointHandler.PostDelayed(_checkpointRunnable, 2_000);
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
                    SeekBy(-15_000);
                    break;
                case ActionForward:
                    SeekBy(15_000);
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
            }
        }
        catch (Exception)
        {
            // A transient restore failure must not erase the only saved
            // position. A later app launch can retry recovery.
            StopPlayback(clearRecovery: intent?.Action != ActionRestore);
            StopSelf(startId);
        }

        return StartCommandResult.NotSticky;
    }

    private void StartPlayback(Intent intent, bool restoring = false)
    {
        var path = intent.GetStringExtra(ExtraFilePath);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new FileNotFoundException("Audiobook audio file was not found.", path);

        StopPlayer();
        _isRestoringPlayback = restoring;
        _title = intent.GetStringExtra(ExtraTitle);
        if (string.IsNullOrWhiteSpace(_title))
            _title = Path.GetFileNameWithoutExtension(path);
        _currentFilePath = path;
        _playbackSpeed = 1f;
        _mediaSession?.SetActive(true);
        UpdateMediaSessionState();

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
        // During recovery, the player can still report position zero while it
        // prepares and applies the restored seek. Wait for STATE_READY before
        // allowing any checkpoint to replace the durable saved position.
        if (!_isRestoringPlayback) SavePlaybackSnapshot();
        UpdateNotification();
    }


    // Media3 player callbacks keep the foreground notification and service
    // lifecycle synchronized with the actual decoder state.
    public void OnPlaybackStateChanged(int playbackState)
    {
        if (playbackState == 3 && _isRestoringPlayback) // Player.STATE_READY
        {
            _isRestoringPlayback = false;
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
        // Keep the recovery snapshot if decoding failed before a restored
        // session became ready; the next launch can retry from the same point.
        StopPlayback(clearRecovery: !_isRestoringPlayback);
        StopSelf();
    }

    public void OnAudioAttributesChanged(AndroidX.Media3.Common.AudioAttributes? value) { }
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
    public void OnMediaMetadataChanged(AndroidX.Media3.Common.MediaMetadata? metadata) { }
    public void OnMetadata(Metadata? metadata) { }
    public void OnPlayWhenReadyChanged(bool playWhenReady, int reason) => UpdateNotification();
    public void OnPositionDiscontinuity(PlayerPositionInfo? oldPosition, PlayerPositionInfo? newPosition, int reason) { }
    public void OnPlaybackParametersChanged(PlaybackParameters? parameters) { }
    public void OnPlaybackSuppressionReasonChanged(int reason) { }
    public void OnPlayerErrorChanged(PlaybackException? error) { }
    public void OnPlaylistMetadataChanged(AndroidX.Media3.Common.MediaMetadata? metadata) { }
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
        // A recreated UI adapter may send RESTORE while this service is already
        // playing. Never tear down a live session or rewind it to an older snapshot.
        if (_player is not null) return;

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
        StartPlayback(intent, restoring: true);
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
        if (_isRestoringPlayback || _player is null || string.IsNullOrWhiteSpace(_currentFilePath)) return;
        try
        {
            PlaybackRecoveryStore.Save(this, new PlaybackRecoverySnapshot(
                _currentFilePath, _title, ReadPosition(), ReadDuration(), _playbackSpeed, _player.IsPlaying));
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
        var action = _player?.IsPlaying == true ? ActionPause : ActionResume;
        var actionLabel = _player?.IsPlaying == true ? "Pause" : "Resume";
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
            .SetOngoing(_player?.IsPlaying == true)
            .SetOnlyAlertOnce(true)
            .SetStyle(new Notification.MediaStyle()
                .SetMediaSession(_mediaSession?.SessionToken)
                .SetShowActionsInCompactView(0, 1, 2))
            .AddAction(global::Android.Resource.Drawable.IcMediaRew, "−15 sec", rewindPendingIntent)
            .AddAction(actionIcon, actionLabel, controlPendingIntent)
            .AddAction(global::Android.Resource.Drawable.IcMediaFf, "+15 sec", forwardPendingIntent);
        return builder.Build();
    }

    private void UpdateNotification()
    {
        UpdateMediaSessionState();
        var manager = (NotificationManager?)GetSystemService(NotificationService);
        manager?.Notify(NotificationId, BuildNotification());
    }

    private void CreateMediaSession()
    {
        _mediaSession = new MediaSession(this, "VORATHPlayback");
        _mediaSession.SetFlags(MediaSessionFlags.HandlesMediaButtons | MediaSessionFlags.HandlesTransportControls);
        _mediaSession.SetCallback(new PlaybackSessionCallback(this), new Handler(Looper.MainLooper!));
        _mediaSession.SetMetadata(new global::Android.Media.MediaMetadata.Builder()
            .PutString(global::Android.Media.MediaMetadata.MetadataKeyTitle, _title)
            .PutString(global::Android.Media.MediaMetadata.MetadataKeyArtist, "VØRATH")
            .Build());
    }

    private void UpdateMediaSessionState()
    {
        if (_mediaSession is null) return;
        try
        {
            _mediaSession.SetMetadata(new global::Android.Media.MediaMetadata.Builder()
                .PutString(global::Android.Media.MediaMetadata.MetadataKeyTitle, _title)
                .PutString(global::Android.Media.MediaMetadata.MetadataKeyArtist, "VØRATH")
                .Build());

            var actions = PlaybackState.ActionPlay | PlaybackState.ActionPause |
                PlaybackState.ActionPlayPause | PlaybackState.ActionSeekTo;
            var state = _player?.IsPlaying == true
                ? PlaybackState.StatePlaying
                : _player is null ? PlaybackState.StateNone : PlaybackState.StatePaused;
            _mediaSession.SetPlaybackState(new PlaybackState.Builder()
                .SetActions(actions)
                .SetState(state, ReadPosition(), _playbackSpeed)
                .Build());
        }
        catch (Exception) { /* System media controls must never interrupt playback. */ }
    }

    private void HandleSessionPlay()
    {
        if (_player is null) return;
        _player.Play();
        SavePlaybackSnapshot();
        UpdateNotification();
    }

    private void HandleSessionPause()
    {
        if (_player is null) return;
        _player.Pause();
        SavePlaybackSnapshot();
        UpdateNotification();
    }

    private void HandleSessionSeek(long position)
    {
        if (_player is null) return;
        var target = Math.Max(0L, position);
        var duration = ReadDuration();
        if (duration > 0) target = Math.Min(target, duration);
        _player.SeekTo((int)Math.Min(target, int.MaxValue));
        SavePlaybackSnapshot();
        UpdateNotification();
    }

    private sealed class PlaybackSessionCallback : MediaSession.Callback
    {
        private readonly AndroidLocalAudioPlaybackService _service;
        public PlaybackSessionCallback(AndroidLocalAudioPlaybackService service) => _service = service;
        public override void OnPlay() => _service.HandleSessionPlay();
        public override void OnPause() => _service.HandleSessionPause();
        public override void OnSeekTo(long position) => _service.HandleSessionSeek(position);
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
        _isRestoringPlayback = false;
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
        if (_sleepTimerRunnable is not null)
            _checkpointHandler?.RemoveCallbacks(_sleepTimerRunnable);
        _checkpointRunnable?.Dispose();
        _checkpointRunnable = null;
        _sleepTimerRunnable?.Dispose();
        _sleepTimerRunnable = null;
        _checkpointHandler = null;
        StopPlayback(clearRecovery: false);
        _mediaSession?.SetActive(false);
        _mediaSession?.Release();
        _mediaSession = null;
        if (Current == this) Current = null;
        base.OnDestroy();
    }
}
