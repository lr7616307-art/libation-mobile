using System.Globalization;
using AudibleApi.Common;
using Libation.Mobile.Core;

namespace Libation.Mobile.Audible;

/// <summary>Maps Audible library DTO metadata without guessing contributor roles.</summary>
public static class AudibleItemMapper
{
    public static RemoteAudiobook Map(Item item)
    {
        ArgumentNullException.ThrowIfNull(item);

        var providerId = item.ProductId?.Trim() ?? string.Empty;
        var title = BuildTitle(item.Title, item.Subtitle, providerId);

        return new RemoteAudiobook(
            providerId,
            title,
            OrUnknownAuthor(JoinNames(item.Authors?.Select(person => person?.Name))),
            JoinNames(item.Narrators?.Select(person => person?.Name)),
            FormatRuntime(item.RuntimeLengthMin))
        {
            Description = FirstNonEmpty(
                item.Description,
                item.AudibleEditorsSummary,
                item.EditorialReviews?.FirstOrDefault()),
            CoverImageUrl = item.ProductImages?.The500?.AbsoluteUri ?? string.Empty,
            IsPlayableOnAudible = item.IsPlayable,
            IsDownloadedOnAudible = item.IsDownloaded
        };
    }

    /// <summary>Formats provider runtime minutes as a compact, human-readable duration.</summary>
    public static string FormatRuntime(object? runtimeMinutes)
    {
        if (!double.TryParse(
                Convert.ToString(runtimeMinutes, CultureInfo.InvariantCulture),
                NumberStyles.Number,
                CultureInfo.InvariantCulture,
                out var minutes)
            || !double.IsFinite(minutes)
            || minutes <= 0)
            return string.Empty;

        var totalMinutes = (long)Math.Round(minutes, MidpointRounding.AwayFromZero);
        if (totalMinutes <= 0)
            return string.Empty;

        var hours = totalMinutes / 60;
        var remainingMinutes = totalMinutes % 60;
        if (hours == 0)
            return $"{remainingMinutes}m";
        return remainingMinutes == 0 ? $"{hours}h" : $"{hours}h {remainingMinutes}m";
    }

    private static string BuildTitle(string? rawTitle, string? rawSubtitle, string providerId)
    {
        static string? Clean(string? value)
        {
            var cleaned = value?.Trim();
            return string.IsNullOrWhiteSpace(cleaned)
                || string.Equals(cleaned, "[null]", StringComparison.OrdinalIgnoreCase)
                || string.Equals(cleaned, "null", StringComparison.OrdinalIgnoreCase)
                ? null
                : cleaned;
        }

        var title = Clean(rawTitle);
        var subtitle = Clean(rawSubtitle);
        if (title is null)
            return subtitle ?? (string.IsNullOrWhiteSpace(providerId)
                ? "Untitled Audible audiobook"
                : $"Untitled Audible audiobook ({providerId})");

        return subtitle is null || string.Equals(title, subtitle, StringComparison.OrdinalIgnoreCase)
            ? title
            : $"{title}: {subtitle}";
    }

    private static string OrUnknownAuthor(string author)
        => string.IsNullOrWhiteSpace(author) ? "Unknown author" : author;

    private static string FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;

    /// <summary>Joins usable names in source order; null, blank and whitespace-only values are ignored.</summary>
    public static string JoinNames(IEnumerable<string?>? names)
    {
        if (names is null)
            return string.Empty;

        return string.Join(", ", names
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!.Trim()));
    }
}
