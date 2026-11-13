using System.Globalization;

namespace Libation.Mobile.Audible;

/// <summary>
/// Widevine's reported offline time-to-live values. A missing or malformed
/// field is unknown, never interpreted as an expired licence.
/// </summary>
public sealed record AudibleWidevineLicenseDuration(
    long? LicenseRemainingSeconds,
    long? PlaybackRemainingSeconds)
{
    public const string LicenseDurationProperty = "LicenseDurationRemaining";
    public const string PlaybackDurationProperty = "PlaybackDurationRemaining";

    /// <summary>
    /// True only when Widevine explicitly reports that either remaining
    /// duration has reached zero. Unknown vendor fields do not imply expiry.
    /// </summary>
    public bool IsDefinitelyExpired
        => LicenseRemainingSeconds == 0 || PlaybackRemainingSeconds == 0;

    public static AudibleWidevineLicenseDuration FromKeyStatus(
        IEnumerable<KeyValuePair<string, string>> keyStatus)
    {
        ArgumentNullException.ThrowIfNull(keyStatus);

        var status = keyStatus.ToDictionary(
            pair => pair.Key,
            pair => pair.Value,
            StringComparer.Ordinal);

        return new AudibleWidevineLicenseDuration(
            ReadSeconds(status, LicenseDurationProperty),
            ReadSeconds(status, PlaybackDurationProperty));
    }

    private static long? ReadSeconds(
        IReadOnlyDictionary<string, string> status,
        string propertyName)
    {
        if (!status.TryGetValue(propertyName, out var value) ||
            !long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) ||
            seconds < 0)
            return null;

        return seconds;
    }
}
