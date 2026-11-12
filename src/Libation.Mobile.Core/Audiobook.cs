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

    /// <summary>HTTPS cover-art URL supplied by the catalogue provider; local imported artwork remains separate.</summary>
    public string CoverImageUrl { get; init; } = string.Empty;

    /// <summary>Provider-reported playability/download state. Null means the provider did not report it.</summary>
    public bool? IsPlayableOnAudible { get; init; }
    public bool? IsDownloadedOnAudible { get; init; }

    [JsonIgnore]
    public string AudibleAvailabilityLabel => !IsProviderCatalogueEntry
        ? string.Empty
        : !string.IsNullOrWhiteSpace(ProtectedAudioFileName)
            ? "Downloaded to VØRATH as a DRM-protected file"
            : !string.IsNullOrWhiteSpace(AudioFilePath)
                ? "Downloaded to VØRATH and ready to play"
                : IsPlayableOnAudible == true
                ? (IsDownloadedOnAudible == true
                    ? "Audible reports this title downloaded"
                    : "Available to download in VØRATH")
                : IsPlayableOnAudible == false
                    ? "Not currently playable in Audible"
                    : "Availability not reported by Audible";

    [JsonIgnore]
    public string AudibleAvailabilityShortLabel => !IsProviderCatalogueEntry
        ? string.Empty
        : !string.IsNullOrWhiteSpace(ProtectedAudioFileName) ? "SAVED IN VØRATH"
        : !string.IsNullOrWhiteSpace(AudioFilePath) ? "READY TO PLAY"
        : IsDownloadedOnAudible == true ? "DOWNLOADED ON AUDIBLE"
        : IsPlayableOnAudible == true ? "AVAILABLE"
        : IsPlayableOnAudible == false ? "UNAVAILABLE"
        : "STATUS UNKNOWN";

    [JsonIgnore]
    public bool HasNoCoverArt => string.IsNullOrWhiteSpace(CoverImagePath);

    [JsonIgnore]
    public string DownloadActionLabel => IsProviderCatalogueEntry
        ? !string.IsNullOrWhiteSpace(AudioFilePath) ? "▶  Play"
            : !string.IsNullOrWhiteSpace(ProtectedAudioFileName) ? "✓  Saved"
            : "⇩  Download"
        : "Add to queue";

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

    /// <summary>Local listening state: Not started, In progress, or Finished.</summary>
    public string ListeningStatus { get; init; } = "Not started";

    /// <summary>Original imported or downloaded file size in bytes, when available.</summary>
    public long FileSizeBytes { get; init; }

    /// <summary>
    /// Name and destination folder of an Audible file saved with provider DRM.
    /// These fields describe an encrypted backup only; they are never used as
    /// the local player's AudioFilePath.
    /// </summary>
    public string? ProtectedAudioFileName { get; init; }
    public string? ProtectedAudioFolderName { get; init; }
    public string? ProtectedAudioProtection { get; init; }

    /// <summary>Transient bulk-selection state; never persisted in the catalogue backup.</summary>
    [JsonIgnore]
    public bool IsSelectedForBulkAction { get; init; }

    /// <summary>Provider catalogue entries are metadata-only in VØRATH; protected Audible audio is not downloaded here.</summary>
    [JsonIgnore]
    public bool IsProviderCatalogueEntry => Id.StartsWith("provider:", StringComparison.Ordinal);

    [JsonIgnore]
    public bool CanQueueDownload => !IsProviderCatalogueEntry;

    [JsonIgnore]
    public bool CanDelete => !IsProviderCatalogueEntry && !string.Equals(Status, "Demo", StringComparison.Ordinal);
}
