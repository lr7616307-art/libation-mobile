namespace Libation.Mobile.Core;

/// <summary>Optional platform capability for timers that must outlive the UI.</summary>
public interface ISleepTimerAudioPlayer
{
    void SetSleepTimer(DateTimeOffset? deadline, long? chapterPositionMilliseconds);
    void CancelSleepTimer();
}
