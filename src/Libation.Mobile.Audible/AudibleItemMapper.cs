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

        return new RemoteAudiobook(
            item.ProductId?.Trim() ?? string.Empty,
            item.TitleWithSubtitle?.Trim() ?? string.Empty,
            OrUnknownAuthor(JoinNames(item.Authors?.Select(person => person?.Name))),
            JoinNames(item.Narrators?.Select(person => person?.Name)),
            FormatRuntime(item.RuntimeLengthMin))
        {
            CoverImageUrl = item.ProductImages?.The500?.AbsoluteUri ?? string.Empty
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

    private static string OrUnknownAuthor(string author)
        => string.IsNullOrWhiteSpace(author) ? "Unknown author" : author;

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
