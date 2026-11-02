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
}
