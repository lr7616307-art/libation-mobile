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
            JoinNames(item.Authors?.Select(person => person?.Name)),
            JoinNames(item.Narrators?.Select(person => person?.Name)),
            Convert.ToString(item.LengthInMinutes, CultureInfo.InvariantCulture) ?? string.Empty);
    }

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
