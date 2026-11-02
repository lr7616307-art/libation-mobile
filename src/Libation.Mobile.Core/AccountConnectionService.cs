namespace Libation.Mobile.Core;

/// <summary>
/// Coordinates external-browser sign-in with encrypted session persistence.
/// No provider-specific network or authentication implementation is included.
/// </summary>
public sealed class AccountConnectionService(
    IAccountAuthenticator authenticator,
    AccountSessionStore sessions)
{
    public async Task<ExternalSignInChallenge> BeginSignInAsync(
        AccountSignInRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        var challenge = await authenticator.BeginSignInAsync(request, cancellationToken);
        try
        {
            ExternalSignInChallengeValidator.Validate(challenge);
            return challenge;
        }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException("The provider returned an invalid sign-in challenge.", ex);
        }
    }

    public async Task<AccountConnectionState> CompleteSignInAsync(
        string challengeId, string responseUrl, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(challengeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(responseUrl);

        var session = await authenticator.CompleteSignInAsync(challengeId, responseUrl, cancellationToken);
        ValidateSession(session);
        await sessions.SaveAsync(session, cancellationToken);
        return ToState(session);
    }

    public async Task<AccountConnectionState> GetStateAsync(CancellationToken cancellationToken = default)
    {
        var session = await sessions.LoadAsync(cancellationToken);
        return session is null
            ? new(AccountConnectionStatus.Disconnected, null, null)
            : ToState(session);
    }

    public async Task<AccountConnectionState> RefreshAsync(CancellationToken cancellationToken = default)
    {
        var current = await sessions.LoadAsync(cancellationToken);
        if (current is null)
            return new(AccountConnectionStatus.Disconnected, null, null);

        var refreshed = await authenticator.RefreshAsync(current, cancellationToken);
        ValidateSession(refreshed);
        await sessions.SaveAsync(refreshed, cancellationToken);
        return ToState(refreshed);
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
        => sessions.ClearAsync(cancellationToken);

    private static AccountConnectionState ToState(AccountSession session)
    {
        var expiry = session.ExpiresAtUtc.ToUniversalTime();
        var status = expiry <= DateTimeOffset.UtcNow
            ? AccountConnectionStatus.Expired
            : AccountConnectionStatus.Connected;
        return new(status, session.AccountId, expiry);
    }

    private static void ValidateSession(AccountSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(session.AccountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(session.AccessToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(session.RefreshToken);
        if (session.ExpiresAtUtc == default)
            throw new ArgumentException("The authenticated session must include an expiry.", nameof(session));
    }
}
