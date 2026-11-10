namespace Libation.Mobile.Core;

/// <summary>
/// Opens the provider-generated HTTPS authorization URL in the device's normal
/// browser and returns the final marketplace callback URL. Implementations may
/// collect that URL through a secure in-app return screen, but must never handle,
/// inspect, log, or expose credentials entered on the provider sign-in page.
/// </summary>
public interface IExternalBrowserLauncher
{
    Task<string?> OpenAsync(Uri authorizationUri, CancellationToken cancellationToken = default);
}
