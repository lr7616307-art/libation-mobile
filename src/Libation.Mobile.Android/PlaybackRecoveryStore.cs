namespace Libation.Mobile.Android;

internal sealed record PlaybackRecoverySnapshot(
    string FilePath, string Title, long PositionMilliseconds, long DurationMilliseconds,
    float PlaybackSpeed, bool WasPlaying, string? CoverImagePath = null);

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
    private const string CoverImagePathKey = "cover_image_path";

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
            Math.Clamp(speed, 0.5f, 3f), preferences?.GetBoolean(WasPlayingKey, false) ?? false,
            preferences?.GetString(CoverImagePathKey, null));
    }

    public static void Save(global::Android.Content.Context context, PlaybackRecoverySnapshot snapshot)
    {
        var editor = context.GetSharedPreferences(PreferencesName, global::Android.Content.FileCreationMode.Private)?.Edit();
        editor?.PutString(FilePathKey, snapshot.FilePath);
        editor?.PutString(TitleKey, snapshot.Title);
        editor?.PutLong(PositionKey, Math.Max(0, snapshot.PositionMilliseconds));
        editor?.PutLong(DurationKey, Math.Max(0, snapshot.DurationMilliseconds));
        editor?.PutFloat(SpeedKey, Math.Clamp(snapshot.PlaybackSpeed, 0.5f, 3f));
        editor?.PutBoolean(WasPlayingKey, snapshot.WasPlaying);
        editor?.PutString(CoverImagePathKey, snapshot.CoverImagePath);
        // Apply persists asynchronously so the five-second checkpoint never blocks
        // the main looper or causes audible playback stutter.
        editor?.Apply();
    }

    public static void Clear(global::Android.Content.Context context)
        => context.GetSharedPreferences(PreferencesName, global::Android.Content.FileCreationMode.Private)?.Edit()?.Clear()?.Apply();
}
