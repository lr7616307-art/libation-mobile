namespace Libation.Mobile.Core;

/// <summary>
/// Coordinates authentication with encrypted session persistence.
/// It does not implement provider-specific login, network transport or token refresh policy.
/// </summary>
public sealed class AccountConnectionService(
    IAccountAuthenticator authenticator,
    AccountSessionStore sessions)
{
    public async Task<AccountConnectionState> ConnectAsync(CancellationToken cancellationToken = default)
    {
        var session = await authenticator.SignInAsync(cancellationToken);
        ValidateSession(session);
        await sessions.SaveAsync(session, cancellationToken);
        return ToState(session);
    }

    public async Task<AccountConnectionState> GetStateAsync(CancellationToken cancellationToken = default)
    {
        var session = await sessions.LoadAsync(cancellationToken);
        if (session is null)
            return new(AccountConnectionStatus.Disconnected, null, null);

        return ToState(session);
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

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
        => await sessions.ClearAsync(cancellationToken);

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
