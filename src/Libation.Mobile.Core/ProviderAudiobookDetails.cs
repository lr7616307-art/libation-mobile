namespace Libation.Mobile.Core;

/// <summary>Non-audio metadata returned for one provider audiobook.</summary>
public sealed record ProviderAudiobookDetails(
    IReadOnlyList<AudiobookChapter> Chapters,
    long FileSizeBytes);
