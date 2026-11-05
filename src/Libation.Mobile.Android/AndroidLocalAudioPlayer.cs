using Android.Content;
using Libation.Mobile.Core;

namespace Libation.Mobile.Android;

/// <summary>UI adapter for the playback service, including crash-safe session recovery.</summary>
public sealed class AndroidLocalAudioPlayer : ILocalAudioPlayer
{
    private static Context AppContext => global::Android.App.Application.Context;

    public AndroidLocalAudioPlayer()
    {
        // Rehydrate the last session when the app is opened after a process death
        // or device restart. The saved play/pause state is respected.
        var snapshot = PlaybackRecoveryStore.Load(AppContext);
        if (snapshot is not null)
        {
            var intent = CreateIntent(AndroidLocalAudioPlaybackService.ActionRestore);
            if (global::Android.OS.Build.VERSION.SdkInt >= global::Android.OS.BuildVersionCodes.O)
                AppContext.StartForegroundService(intent);
            else
                AppContext.StartService(intent);
        }
    }

    private PlaybackRecoverySnapshot? Recovery => PlaybackRecoveryStore.Load(AppContext);
    // While ExoPlayer is preparing after process recovery, the service may
    // exist before IsPlaying becomes true. Keep the persisted intent visible
    // until the player reports its actual state (a user pause writes false).
    public bool IsPlaying => AndroidLocalAudioPlaybackService.IsPlaying || Recovery?.WasPlaying == true;
    public string? CurrentFilePath => AndroidLocalAudioPlaybackService.CurrentFilePath ?? Recovery?.FilePath;
    public long PositionMilliseconds
    {
        get
        {
            var current = AndroidLocalAudioPlaybackService.PositionMilliseconds;
            return current > 0 ? current : Recovery?.PositionMilliseconds ?? 0;
        }
    }
    public long DurationMilliseconds
    {
        get
        {
            var current = AndroidLocalAudioPlaybackService.DurationMilliseconds;
            return current > 0 ? current : Recovery?.DurationMilliseconds ?? 0;
        }
    }
    public float PlaybackSpeed => AndroidLocalAudioPlaybackService.Current is not null
        ? AndroidLocalAudioPlaybackService.PlaybackSpeed : Recovery?.PlaybackSpeed ?? 1f;

    public void Play(string filePath, long startPositionMilliseconds = 0, string? title = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            throw new FileNotFoundException("Imported audiobook audio was not found.", filePath);
        var intent = CreateIntent(AndroidLocalAudioPlaybackService.ActionPlay);
        intent.PutExtra(AndroidLocalAudioPlaybackService.ExtraFilePath, filePath);
        if (!string.IsNullOrWhiteSpace(title)) intent.PutExtra(AndroidLocalAudioPlaybackService.ExtraTitle, title);
        intent.PutExtra(AndroidLocalAudioPlaybackService.ExtraPosition, Math.Max(0, startPositionMilliseconds));
        StartService(intent, foreground: true);
    }

    public void Pause() { ObjectDisposedException.ThrowIf(_disposed, this); AppContext.StartService(CreateIntent(AndroidLocalAudioPlaybackService.ActionPause)); }
    public void Resume() { ObjectDisposedException.ThrowIf(_disposed, this); AppContext.StartService(CreateIntent(AndroidLocalAudioPlaybackService.ActionResume)); }

    public void Seek(long positionMilliseconds)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var intent = CreateIntent(AndroidLocalAudioPlaybackService.ActionSeek);
        intent.PutExtra(AndroidLocalAudioPlaybackService.ExtraPosition, Math.Max(0, positionMilliseconds));
        AppContext.StartService(intent);
    }

    public void SetPlaybackSpeed(float speed)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (float.IsNaN(speed) || float.IsInfinity(speed)) throw new ArgumentOutOfRangeException(nameof(speed));
        var intent = CreateIntent(AndroidLocalAudioPlaybackService.ActionSetSpeed);
        intent.PutExtra(AndroidLocalAudioPlaybackService.ExtraSpeed, Math.Clamp(speed, 0.5f, 3f));
        AppContext.StartService(intent);
    }

    public void Dispose() => _disposed = true;
    private bool _disposed;

    private static Intent CreateIntent(string action) => new Intent(AppContext, typeof(AndroidLocalAudioPlaybackService)).SetAction(action);
    private static void StartService(Intent intent, bool foreground = false)
    {
        if (foreground && global::Android.OS.Build.VERSION.SdkInt >= global::Android.OS.BuildVersionCodes.O)
            AppContext.StartForegroundService(intent);
        else
            AppContext.StartService(intent);
    }
}
