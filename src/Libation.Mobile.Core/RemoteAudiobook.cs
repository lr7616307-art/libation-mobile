namespace Libation.Mobile.Core;

/// <summary>Metadata returned by an authorized audiobook-catalogue provider adapter.</summary>
public sealed record RemoteAudiobook(
    string ProviderId,
    string Title,
    string Author,
    string Narrator,
    string Length);

/// <summary>
/// Narrow boundary for retrieving the signed-in user's catalogue. Implementations must use
/// supported provider authentication and APIs and must not return audio or DRM material here.
/// </summary>
public interface ILibraryCatalogProvider
{
    Task<IReadOnlyList<RemoteAudiobook>> GetLibraryAsync(CancellationToken cancellationToken = default);
}
