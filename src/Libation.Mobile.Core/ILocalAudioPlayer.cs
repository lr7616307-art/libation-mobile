namespace Libation.Mobile.Core;

/// <summary>Platform adapter for playing an app-managed local audiobook file.</summary>
public interface ILocalAudioPlayer : IDisposable
{
    bool IsPlaying { get; }
    string? CurrentFilePath { get; }
    long PositionMilliseconds { get; }
    long DurationMilliseconds { get; }
    float PlaybackSpeed { get; }
    void Play(string filePath, long startPositionMilliseconds = 0, string? title = null);
    void Pause();
    void Resume();
    void Seek(long positionMilliseconds);
    void SetPlaybackSpeed(float speed);
    /// <summary>Optional artwork path for the platform media notification.</summary>
    void SetArtwork(string? coverImagePath) { }
}

/// <summary>
/// Optional platform hook for restoring a persisted audio session after the
/// shared UI has initialized its library and first screen.
/// </summary>
public interface IDeferredAudioSessionRecovery
{
    void RestorePendingSession();
}
