namespace Libation.Mobile.Core;

/// <summary>
/// Provider-specific browser-based authentication boundary. The provider adapter must use
/// the supported sign-in flow; it must not log passwords, response URLs, tokens or cookies.
/// </summary>
public interface IAccountAuthenticator
{
    Task<ExternalSignInChallenge> BeginSignInAsync(
        AccountSignInRequest request, CancellationToken cancellationToken = default);

    Task<AccountSession> CompleteSignInAsync(
        string challengeId, string responseUrl, CancellationToken cancellationToken = default);

    Task<AccountSession> RefreshAsync(
        AccountSession currentSession, CancellationToken cancellationToken = default);

    /// <summary>
    /// Confirms that provider-specific credentials needed for the account are still restorable.
    /// Generic authenticators without a separate identity record are healthy by default.
    /// </summary>
    Task<bool> HasRestorableIdentityAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(true);

    /// <summary>Discards an unfinished provider-side browser challenge without disconnecting the account.</summary>
    Task CancelSignInAsync(string challengeId, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    /// <summary>Removes provider-specific credentials from secure storage.</summary>
    Task DisconnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
