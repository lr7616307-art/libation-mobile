namespace Libation.Mobile.Core;

/// <summary>
/// Opens the provider-generated HTTPS authorization page in an in-app browser
/// and returns the final marketplace callback URL. Implementations must never
/// inspect, log, or expose credentials entered into the provider page.
/// </summary>
public interface IExternalBrowserLauncher
{
    Task<string?> OpenAsync(Uri authorizationUri, CancellationToken cancellationToken = default);
}
