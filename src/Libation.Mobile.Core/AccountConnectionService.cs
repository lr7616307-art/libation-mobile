namespace Libation.Mobile.Core;

/// <summary>
/// Coordinates external-browser sign-in with encrypted session persistence.
/// Only one in-memory challenge may be active; completion is one-time and serialized.
/// No provider-specific network or authentication implementation is included.
/// </summary>
public sealed class AccountConnectionService(
    IAccountAuthenticator authenticator,
    AccountSessionStore sessions)
{
    private readonly SemaphoreSlim _signInGate = new(1, 1);
    private ExternalSignInChallenge? _pendingChallenge;

    public async Task<ExternalSignInChallenge> BeginSignInAsync(
        AccountSignInRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();

        await _signInGate.WaitAsync(cancellationToken);
        try
        {
            // Starting a new attempt invalidates any earlier unfinished browser handoff.
            _pendingChallenge = null;

            var challenge = await authenticator.BeginSignInAsync(request, cancellationToken);
            try
            {
                ExternalSignInChallengeValidator.Validate(challenge);
            }
            catch (ArgumentException ex)
            {
                throw new InvalidOperationException("The provider returned an invalid sign-in challenge.", ex);
            }

            _pendingChallenge = challenge;
            return challenge;
        }
        finally
        {
            _signInGate.Release();
        }
    }

    public async Task<AccountConnectionState> CompleteSignInAsync(
        string challengeId, string responseUrl, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(challengeId);
        ValidateResponseUrl(responseUrl);

        await _signInGate.WaitAsync(cancellationToken);
        try
        {
            var pending = _pendingChallenge;
            if (pending is null
                || !string.Equals(pending.ChallengeId, challengeId, StringComparison.Ordinal))
                throw new InvalidOperationException("There is no matching active sign-in attempt.");

            if (pending.ExpiresAtUtc <= DateTimeOffset.UtcNow)
            {
                _pendingChallenge = null;
                throw new InvalidOperationException("The sign-in attempt has expired. Start again.");
            }

            // Consume before calling provider code. Failed or cancelled submissions cannot be replayed.
            _pendingChallenge = null;

            var session = await authenticator.CompleteSignInAsync(
                challengeId, responseUrl, cancellationToken);
            ValidateSession(session);
            await sessions.SaveAsync(session, cancellationToken);
            return ToState(session);
        }
        finally
        {
            _signInGate.Release();
        }
    }

    public async Task CancelSignInAsync(string challengeId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(challengeId);
        await _signInGate.WaitAsync(cancellationToken);
        try
        {
            if (_pendingChallenge is null
                || !string.Equals(_pendingChallenge.ChallengeId, challengeId, StringComparison.Ordinal))
                return;

            _pendingChallenge = null;
            await authenticator.CancelSignInAsync(challengeId, cancellationToken);
        }
        finally
        {
            _signInGate.Release();
        }
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
        await _signInGate.WaitAsync(cancellationToken);
        try
        {
            var current = await sessions.LoadAsync(cancellationToken);
            if (current is null)
                return new(AccountConnectionStatus.Disconnected, null, null);

            var refreshed = await authenticator.RefreshAsync(current, cancellationToken);
            ValidateSession(refreshed);
            await sessions.SaveAsync(refreshed, cancellationToken);
            return ToState(refreshed);
        }
        finally
        {
            _signInGate.Release();
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        await _signInGate.WaitAsync(cancellationToken);
        try
        {
            _pendingChallenge = null;
            // Clear the provider's complete identity before clearing the generic session marker.
            // This prevents a saved Audible identity from silently reconnecting after sign-out.
            await authenticator.DisconnectAsync(cancellationToken);
            await sessions.ClearAsync(cancellationToken);
        }
        finally
        {
            _signInGate.Release();
        }
    }

    private const int MaximumResponseUrlBytes = 16 * 1024;

    private static void ValidateResponseUrl(string responseUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(responseUrl);
        if (System.Text.Encoding.UTF8.GetByteCount(responseUrl) > MaximumResponseUrlBytes)
            throw new ArgumentException("The sign-in response URL exceeds the supported size limit.", nameof(responseUrl));

        if (!Uri.TryCreate(responseUrl, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo))
            throw new ArgumentException("The sign-in response must be an absolute HTTPS URL without embedded credentials.", nameof(responseUrl));
    }

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
