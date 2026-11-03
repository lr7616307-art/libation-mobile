namespace Libation.Mobile.Core;

/// <summary>
/// Coordinates external-browser sign-in with encrypted session persistence.
/// Lifecycle operations share one coordinator so stale results cannot resurrect
/// a disconnected or replaced account session.
/// </summary>
public sealed class AccountConnectionService
{
    private readonly IAccountAuthenticator authenticator;
    private readonly AccountSessionStore sessions;
    private readonly AccountSessionLifecycleCoordinator lifecycle;

    public AccountConnectionService(
        IAccountAuthenticator authenticator,
        AccountSessionStore sessions,
        AccountSessionLifecycleCoordinator? lifecycle = null)
    {
        this.authenticator = authenticator ?? throw new ArgumentNullException(nameof(authenticator));
        this.sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        this.lifecycle = lifecycle ?? new AccountSessionLifecycleCoordinator();
    }

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

        var session = await lifecycle.CreateAndSaveAsync(
            token => authenticator.CompleteSignInAsync(challengeId, responseUrl, token),
            async (created, token) =>
            {
                ValidateSession(created);
                await sessions.SaveAsync(created, token);
            },
            cancellationToken);
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
        var session = await lifecycle.ExecuteCurrentAsync(
            async token =>
            {
                var current = await sessions.LoadAsync(token);
                if (current is null) return null;
                var refreshed = await authenticator.RefreshAsync(current, token);
                ValidateSession(refreshed);
                return refreshed;
            },
            async (refreshed, token) =>
            {
                if (refreshed is not null)
                    await sessions.SaveAsync(refreshed, token);
            },
            cancellationToken);

        return session is null
            ? new(AccountConnectionStatus.Disconnected, null, null)
            : ToState(session);
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
        => lifecycle.ClearAsync(sessions.ClearAsync, cancellationToken);

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
