namespace Libation.Mobile.Core;

/// <summary>
/// Opens a provider-generated authorization page in the user's external browser.
/// Implementations must never log the URI because it may contain short-lived state.
/// </summary>
public interface IExternalBrowserLauncher
{
    void Open(Uri authorizationUri);
}
