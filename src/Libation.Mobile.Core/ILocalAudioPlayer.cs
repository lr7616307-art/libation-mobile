namespace Libation.Mobile.Core;

/// <summary>Platform adapter for playing an app-managed local audiobook file.</summary>
public interface ILocalAudioPlayer : IDisposable
{
    bool IsPlaying { get; }
    long PositionMilliseconds { get; }
    long DurationMilliseconds { get; }
    void Play(string filePath, long startPositionMilliseconds = 0);
    void Pause();
    void Seek(long positionMilliseconds);
}
