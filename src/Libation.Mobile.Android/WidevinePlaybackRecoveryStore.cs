using Android.Content;
using System.Text.RegularExpressions;

namespace Libation.Mobile.Android;

/// <summary>
/// Crash-recovery metadata for protected playback. It intentionally stores no
/// account ID, signed manifest URL, licence response, challenge or key-set ID.
/// </summary>
internal sealed record WidevinePlaybackRecoverySnapshot(
    string Asin,
    string Title,
    long PositionMilliseconds,
    bool WasPlaying);

internal static class WidevinePlaybackRecoveryStore
{
    private const string PreferencesName = "vorath_widevine_playback_recovery_v1";
    private static readonly Regex AsinPattern = new("^[A-Za-z0-9]{10}$", RegexOptions.CultureInvariant);

    public static WidevinePlaybackRecoverySnapshot? Load(Context context)
    {
        var preferences = context.GetSharedPreferences(PreferencesName, FileCreationMode.Private);
        if (preferences is null || !preferences.Contains("asin"))
            return null;

        var asin = preferences.GetString("asin", null);
        var title = preferences.GetString("title", null);
        var position = preferences.GetLong("position_ms", 0);
        var wasPlaying = preferences.GetBoolean("was_playing", false);

        if (string.IsNullOrWhiteSpace(asin) || !AsinPattern.IsMatch(asin) ||
            string.IsNullOrWhiteSpace(title) || title.Length > 512 || position < 0)
        {
            Clear(context);
            return null;
        }

        return new WidevinePlaybackRecoverySnapshot(asin, title, position, wasPlaying);
    }

    public static void Save(Context context, WidevinePlaybackRecoverySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!AsinPattern.IsMatch(snapshot.Asin) ||
            string.IsNullOrWhiteSpace(snapshot.Title) || snapshot.Title.Length > 512 ||
            snapshot.PositionMilliseconds < 0)
            throw new ArgumentException("The protected playback recovery snapshot is invalid.", nameof(snapshot));

        var editor = context.GetSharedPreferences(PreferencesName, FileCreationMode.Private)?.Edit()
            ?? throw new InvalidOperationException("Protected playback recovery storage is unavailable.");
        editor.PutString("asin", snapshot.Asin);
        editor.PutString("title", snapshot.Title);
        editor.PutLong("position_ms", snapshot.PositionMilliseconds);
        editor.PutBoolean("was_playing", snapshot.WasPlaying);
        editor.Apply();
    }

    public static void Clear(Context context)
        => context.GetSharedPreferences(PreferencesName, FileCreationMode.Private)?.Edit()?.Clear()?.Apply();
}
