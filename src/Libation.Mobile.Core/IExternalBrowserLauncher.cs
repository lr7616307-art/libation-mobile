namespace Libation.Mobile.Core;

/// <summary>
/// Opens the provider-generated HTTPS authorization page in an in-app browser
/// and returns the final marketplace callback URL. Implementations must never
/// inspect, log, or expose credentials entered into the provider page.
/// </summary>
public interface IExternalBrowserLauncher
{
    Task<string?> OpenAsync(Uri authorizationUri, CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens a provider sign-in page with its short-lived registration cookies.
    /// Platforms that cannot install cookies may use the basic overload; Android
    /// installs the supplied cookies before loading the Audible sign-in page.
    /// </summary>
    Task<string?> OpenAsync(
        Uri authorizationUri,
        IReadOnlyList<ExternalSignInCookie> signInCookies,
        CancellationToken cancellationToken = default)
        => OpenAsync(authorizationUri, cancellationToken);
}
