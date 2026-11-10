namespace Libation.Mobile.Core;

/// <summary>
/// Opens the provider-generated HTTPS authorization page in an in-app browser
/// and returns the final marketplace callback URL. Implementations may ask the
/// user to paste that URL, but must never inspect, log, or expose provider credentials.
/// </summary>
public interface IExternalBrowserLauncher
{
    Task<string?> OpenAsync(Uri authorizationUri, CancellationToken cancellationToken = default);
}
