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
    /// <summary>Absolute path to an imported, app-managed local audio file, if present.</summary>
    /// <summary>Optional catalogue metadata; added as init-only fields for backward-compatible local JSON data.</summary>
    public string Series { get; init; } = string.Empty;
    public string Genre { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;

    /// <summary>Absolute path to app-managed local cover artwork, if present.</summary>
    public string? CoverImagePath { get; init; }

    [JsonIgnore]
    public bool HasSeries => !string.IsNullOrWhiteSpace(Series);

    [JsonIgnore]
    public bool HasGenre => !string.IsNullOrWhiteSpace(Genre);

    public string? AudioFilePath { get; init; }

    /// <summary>Last known playback position in milliseconds for resuming local audio.</summary>
    public long PlaybackPositionMilliseconds { get; init; }

    [JsonIgnore]
    public bool CanDelete => !string.Equals(Status, "Demo", StringComparison.Ordinal);
}
