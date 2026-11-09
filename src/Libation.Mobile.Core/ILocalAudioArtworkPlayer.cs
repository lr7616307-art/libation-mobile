namespace Libation.Mobile.Core;

/// <summary>Optional artwork-aware playback capability for platform players.</summary>
public interface ILocalAudioArtworkPlayer : ILocalAudioPlayer
{
    void Play(string filePath, long startPositionMilliseconds, string? title, string? artworkPath);
}
