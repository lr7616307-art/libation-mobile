namespace Libation.Mobile.Core;

/// <summary>
/// Reads supplementary metadata for one already-owned provider catalogue entry.
/// Implementations must not return audio or a download licence.
/// </summary>
public interface IAudiobookDetailsProvider
{
    Task<ProviderAudiobookDetails?> GetDetailsAsync(
        string providerId,
        CancellationToken cancellationToken = default);
}
