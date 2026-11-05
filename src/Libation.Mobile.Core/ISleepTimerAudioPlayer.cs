namespace Libation.Mobile.Core;

/// <summary>Optional platform capability for timers that must outlive the UI.</summary>
public interface ISleepTimerAudioPlayer
{
    SleepTimerSnapshot? GetSleepTimer();
    void SetSleepTimer(DateTimeOffset? deadline, long? chapterPositionMilliseconds);
    void CancelSleepTimer();
}

/// <summary>Persisted sleep timer state exposed to the player UI.</summary>
public sealed record SleepTimerSnapshot(DateTimeOffset? Deadline, long? ChapterPositionMilliseconds);
