namespace Libation.Mobile.UI.ViewModels;

/// <summary>
/// Optional platform UI for choosing an Audible marketplace before creating
/// the provider's one-time sign-in challenge.
/// </summary>
public interface IAudibleMarketplaceSelector
{
    Task<string?> SelectMarketplaceAsync(
        string defaultMarketplace,
        CancellationToken cancellationToken = default);
}
