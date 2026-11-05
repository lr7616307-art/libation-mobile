using Android.Content;
using Libation.Mobile.Core;

namespace Libation.Mobile.Android;

/// <summary>
/// Client adapter for the foreground playback service. Disposing the UI adapter
/// deliberately does not stop playback when the screen is closed.
/// </summary>
public sealed class AndroidLocalAudioPlayer : ILocalAudioPlayer
{
    private static Context AppContext => global::Android.App.Application.Context;

    public bool IsPlaying => AndroidLocalAudioPlaybackService.IsPlaying;
    public string? CurrentFilePath => AndroidLocalAudioPlaybackService.CurrentFilePath;
    public long PositionMilliseconds => AndroidLocalAudioPlaybackService.PositionMilliseconds;
    public long DurationMilliseconds => AndroidLocalAudioPlaybackService.DurationMilliseconds;

    public void Play(string filePath, long startPositionMilliseconds = 0, string? title = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            throw new FileNotFoundException("Imported audiobook audio was not found.", filePath);

        var intent = CreateIntent(AndroidLocalAudioPlaybackService.ActionPlay);
        intent.PutExtra(AndroidLocalAudioPlaybackService.ExtraFilePath, filePath);
        if (!string.IsNullOrWhiteSpace(title))
            intent.PutExtra(AndroidLocalAudioPlaybackService.ExtraTitle, title);
        intent.PutExtra(AndroidLocalAudioPlaybackService.ExtraPosition, Math.Max(0, startPositionMilliseconds));
        if (global::Android.OS.Build.VERSION.SdkInt >= global::Android.OS.BuildVersionCodes.O)
            AppContext.StartForegroundService(intent);
        else
            AppContext.StartService(intent);
    }

    public void Pause()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        AppContext.StartService(CreateIntent(AndroidLocalAudioPlaybackService.ActionPause));
    }

    public void Resume()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        AppContext.StartService(CreateIntent(AndroidLocalAudioPlaybackService.ActionResume));
    }

    public void Seek(long positionMilliseconds)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var intent = CreateIntent(AndroidLocalAudioPlaybackService.ActionSeek);
        intent.PutExtra(AndroidLocalAudioPlaybackService.ExtraPosition, Math.Max(0, positionMilliseconds));
        AppContext.StartService(intent);
    }

    public void Dispose() => _disposed = true;

    private bool _disposed;

    private static Intent CreateIntent(string action)
        => new Intent(AppContext, typeof(AndroidLocalAudioPlaybackService)).SetAction(action);
}
