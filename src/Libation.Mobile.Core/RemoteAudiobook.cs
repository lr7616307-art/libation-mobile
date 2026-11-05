namespace Libation.Mobile.Core;

/// <summary>Metadata returned by an authorized audiobook-catalogue provider adapter.</summary>
public sealed record RemoteAudiobook(string ProviderId, string Title, string Author, string Narrator, string Length)
{
    public string Series { get; init; } = string.Empty;
    public string Genre { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string CoverImageUrl { get; init; } = string.Empty;
}
