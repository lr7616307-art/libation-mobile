namespace Libation.Mobile.Core;

/// <summary>
/// Persists the minimum session state using the injected secure secret store.
/// This class does not perform sign-in, refresh tokens, or contact an account provider.
/// </summary>
public sealed class AccountSessionStore(ISecretStore secrets)
{
    private const string AccountIdKey = "account_id";
    private const string AccessTokenKey = "access_token";
    private const string RefreshTokenKey = "refresh_token";
    private const string ExpiryKey = "expires_at_utc";

    public async Task SaveAsync(AccountSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(session.AccountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(session.AccessToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(session.RefreshToken);

        await secrets.SetAsync(AccountIdKey, session.AccountId, cancellationToken);
        await secrets.SetAsync(AccessTokenKey, session.AccessToken, cancellationToken);
        await secrets.SetAsync(RefreshTokenKey, session.RefreshToken, cancellationToken);
        await secrets.SetAsync(ExpiryKey, session.ExpiresAtUtc.ToUniversalTime().ToString("O"), cancellationToken);
    }

    public async Task<AccountSession?> LoadAsync(CancellationToken cancellationToken = default)
    {
        var accountId = await secrets.GetAsync(AccountIdKey, cancellationToken);
        var accessToken = await secrets.GetAsync(AccessTokenKey, cancellationToken);
        var refreshToken = await secrets.GetAsync(RefreshTokenKey, cancellationToken);
        var expiryText = await secrets.GetAsync(ExpiryKey, cancellationToken);

        if (accountId is null || accessToken is null || refreshToken is null || expiryText is null)
            return null;

        if (!DateTimeOffset.TryParse(
                expiryText,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind,
                out var expiresAt))
            return null;

        return new AccountSession(accountId, accessToken, refreshToken, expiresAt);
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await secrets.RemoveAsync(AccountIdKey, cancellationToken);
        await secrets.RemoveAsync(AccessTokenKey, cancellationToken);
        await secrets.RemoveAsync(RefreshTokenKey, cancellationToken);
        await secrets.RemoveAsync(ExpiryKey, cancellationToken);
    }
}
