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
    public const string ExtraFilePath = "file_path";
    public const string ExtraTitle = "title";
    public const string ExtraPosition = "position_ms";
    public const string ExtraSpeed = "playback_speed";
    public const string ExtraAutoPlay = "auto_play";

    private const string ChannelId = "libation_audiobook_playback";
    private const int NotificationId = 4102;
    private IExoPlayer? _player;
    private string _title = "Audiobook";
    private string? _currentFilePath;
    private float _playbackSpeed = 1f;
    private Handler? _checkpointHandler;
    private Java.Lang.Runnable? _checkpointRunnable;

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
    }

    public void OnIsPlayingChanged(bool isPlaying) => UpdateNotification();

    public void OnPlayerError(PlaybackException? error)
    {
        StopPlayback();
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
    public void OnPositionDiscontinuity(PlayerPositionInfo? oldPosition, PlayerPositionInfo? newPosition, int reason) { }
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

    private void SavePlaybackSnapshot()
    {
        if (_player is null || string.IsNullOrWhiteSpace(_currentFilePath)) return;
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
        SavePlaybackSnapshot();
        base.OnTaskRemoved(rootIntent);
    }

    public override void OnDestroy()
    {
        SavePlaybackSnapshot();
        if (_checkpointRunnable is not null)
            _checkpointHandler?.RemoveCallbacks(_checkpointRunnable);
        _checkpointRunnable?.Dispose();
        _checkpointRunnable = null;
        _checkpointHandler = null;
        StopPlayback(clearRecovery: false);
        if (Current == this) Current = null;
        base.OnDestroy();
    }
}
