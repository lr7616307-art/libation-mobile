namespace Libation.Mobile.Core;

/// <summary>
/// Provider-specific authentication boundary. Implementations must use the provider's
/// supported sign-in flow and must not log credentials, tokens, cookies or auth responses.
/// No provider implementation is included yet.
/// </summary>
public interface IAccountAuthenticator
{
    Task<AccountSession> SignInAsync(CancellationToken cancellationToken = default);
    Task<AccountSession> RefreshAsync(AccountSession currentSession, CancellationToken cancellationToken = default);
}
