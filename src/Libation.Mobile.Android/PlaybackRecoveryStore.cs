namespace Libation.Mobile.Android;

internal sealed record PlaybackRecoverySnapshot(
    string FilePath, string Title, long PositionMilliseconds, long DurationMilliseconds,
    float PlaybackSpeed, bool WasPlaying);

/// <summary>Small durable checkpoint stored in app-private Android preferences.</summary>
internal static class PlaybackRecoveryStore
{
    private const string PreferencesName = "vorath_playback_recovery";
    private const string FilePathKey = "file_path";
    private const string TitleKey = "title";
    private const string PositionKey = "position_ms";
    private const string DurationKey = "duration_ms";
    private const string SpeedKey = "speed";
    private const string WasPlayingKey = "was_playing";

    public static PlaybackRecoverySnapshot? Load(global::Android.Content.Context context)
    {
        var preferences = context.GetSharedPreferences(PreferencesName, global::Android.Content.FileCreationMode.Private);
        var path = preferences?.GetString(FilePathKey, null);
        if (string.IsNullOrWhiteSpace(path)) return null;
        var speed = preferences?.GetFloat(SpeedKey, 1f) ?? 1f;
        if (float.IsNaN(speed) || float.IsInfinity(speed)) speed = 1f;
        return new PlaybackRecoverySnapshot(path,
            preferences?.GetString(TitleKey, "Audiobook") ?? "Audiobook",
            Math.Max(0, preferences?.GetLong(PositionKey, 0) ?? 0),
            Math.Max(0, preferences?.GetLong(DurationKey, 0) ?? 0),
            Math.Clamp(speed, 0.5f, 3f), preferences?.GetBoolean(WasPlayingKey, false) ?? false);
    }

    public static void Save(global::Android.Content.Context context, PlaybackRecoverySnapshot snapshot, bool flushImmediately = false)
    {
        var editor = context.GetSharedPreferences(PreferencesName, global::Android.Content.FileCreationMode.Private)?.Edit();
        editor?.PutString(FilePathKey, snapshot.FilePath);
        editor?.PutString(TitleKey, snapshot.Title);
        editor?.PutLong(PositionKey, Math.Max(0, snapshot.PositionMilliseconds));
        editor?.PutLong(DurationKey, Math.Max(0, snapshot.DurationMilliseconds));
        editor?.PutFloat(SpeedKey, Math.Clamp(snapshot.PlaybackSpeed, 0.5f, 3f));
        editor?.PutBoolean(WasPlayingKey, snapshot.WasPlaying);
        // Periodic checkpoints stay asynchronous to avoid playback stutter. Explicit
        // user actions and lifecycle shutdowns can request a synchronous disk flush.
        if (flushImmediately) editor?.Commit();
        else editor?.Apply();
    }

    public static void Clear(global::Android.Content.Context context)
    {
        // Clearing a completed or invalid session is a state transition just
        // as important as saving a pause. Flush it synchronously so a process
        // death cannot resurrect an audiobook that was already cleared.
        context.GetSharedPreferences(PreferencesName, global::Android.Content.FileCreationMode.Private)
            ?.Edit()?.Clear()?.Commit();
    }
}
