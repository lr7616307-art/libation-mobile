using Android.App;
using Android.Content;
using Android.Media;
using Android.OS;

namespace Libation.Mobile.Android;

/// <summary>Owns audiobook playback independently of the Avalonia view lifecycle.</summary>
[Service(Name = "org.libationmobile.app.AndroidLocalAudioPlaybackService", Exported = false)]
public sealed class AndroidLocalAudioPlaybackService : Service
{
    public const string ActionPlay = "org.libationmobile.app.action.PLAY";
    public const string ActionPause = "org.libationmobile.app.action.PAUSE";
    public const string ActionResume = "org.libationmobile.app.action.RESUME";
    public const string ActionSeek = "org.libationmobile.app.action.SEEK";
    public const string ExtraFilePath = "file_path";
    public const string ExtraPosition = "position_ms";

    private const string ChannelId = "libation_audiobook_playback";
    private const int NotificationId = 4102;
    private MediaPlayer? _player;
    private string _title = "Audiobook";

    public static AndroidLocalAudioPlaybackService? Current { get; private set; }
    public static bool IsPlaying => Current?._player?.IsPlaying ?? false;
    public static long PositionMilliseconds => Current?.ReadPosition() ?? 0;
    public static long DurationMilliseconds => Current?.ReadDuration() ?? 0;

    public override void OnCreate()
    {
        base.OnCreate();
        Current = this;
        CreateNotificationChannel();
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
                    UpdateNotification();
                    break;
                case ActionResume:
                    if (_player is not null && !_player.IsPlaying) _player.Start();
                    UpdateNotification();
                    break;
                case ActionSeek:
                    if (_player is not null)
                    {
                        var target = Math.Clamp(intent.GetLongExtra(ExtraPosition, 0), 0, int.MaxValue);
                        _player.SeekTo((int)target);
                    }
                    break;
            }
        }
        catch (Exception)
        {
            StopPlayback();
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
        _title = Path.GetFileNameWithoutExtension(path);
        _player = new MediaPlayer();
        _player.SetDataSource(path);
        _player.Prepare();
        var position = Math.Clamp(intent.GetLongExtra(ExtraPosition, 0), 0, int.MaxValue);
        if (position > 0) _player.SeekTo((int)position);
        _player.Completion += (_, _) =>
        {
            StopPlayback();
            StopSelf();
        };
        StartForeground(NotificationId, BuildNotification());
        _player.Start();
        UpdateNotification();
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

        builder.SetSmallIcon(global::Android.Resource.Drawable.StatSysHeadset)
            .SetContentTitle("Libation Mobile")
            .SetContentText(_title)
            .SetContentIntent(contentPendingIntent)
            .SetOngoing(_player?.IsPlaying == true)
            .SetOnlyAlertOnce(true)
            .AddAction(actionIcon, actionLabel, controlPendingIntent);
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
    }

    private void StopPlayback()
    {
        StopPlayer();
        StopForeground(StopForegroundFlags.Remove);
    }

    public override IBinder? OnBind(Intent? intent) => null;

    public override void OnDestroy()
    {
        StopPlayback();
        if (Current == this) Current = null;
        base.OnDestroy();
    }
}
