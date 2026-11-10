namespace Libation.Mobile.Core;

/// <summary>Optional capability for forwarding chapter boundaries to native media controls.</summary>
public interface IChapterPositionAudioPlayer
{
    void SetChapterPositions(IEnumerable<long> chapterStartPositionsMilliseconds);
}
