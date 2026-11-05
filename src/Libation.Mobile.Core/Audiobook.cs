using System.Text.Json.Serialization;

namespace Libation.Mobile.Core;

public sealed record Audiobook(
    string Id,
    string Title,
    string Author,
    string Narrator,
    string Length,
    string Status)
{
    /// <summary>Optional catalogue metadata; added as init-only fields for backward-compatible local JSON data.</summary>
    public string Series { get; init; } = string.Empty;
    public string Genre { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;

    /// <summary>Chapter markers in playback order. Existing catalogues without this field load as an empty list.</summary>
    public IReadOnlyList<AudiobookChapter> Chapters { get; init; } = Array.Empty<AudiobookChapter>();

    /// <summary>Absolute path to app-managed local cover artwork, if present.</summary>
    public string? CoverImagePath { get; init; }

    [JsonIgnore]
    public bool HasSeries => !string.IsNullOrWhiteSpace(Series);

    [JsonIgnore]
    public bool HasGenre => !string.IsNullOrWhiteSpace(Genre);

    /// <summary>Absolute path to an imported, app-managed local audio file, if present.</summary>
    public string? AudioFilePath { get; init; }

    /// <summary>Last known playback position in milliseconds for resuming local audio.</summary>
    public long PlaybackPositionMilliseconds { get; init; }

    [JsonIgnore]
    public bool CanDelete => !string.Equals(Status, "Demo", StringComparison.Ordinal);
}
