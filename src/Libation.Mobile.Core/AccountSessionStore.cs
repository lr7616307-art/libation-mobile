using System.Text.Json;

namespace Libation.Mobile.Core;

/// <summary>
/// Persists the minimum session state as one encrypted record.
/// This class does not perform sign-in, refresh tokens, or contact an account provider.
/// </summary>
public sealed class AccountSessionStore(ISecretStore secrets)
{
    private const string SessionKey = "account_session_v1";

    public async Task SaveAsync(AccountSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(session.AccountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(session.AccessToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(session.RefreshToken);

        var normalized = session with { ExpiresAtUtc = session.ExpiresAtUtc.ToUniversalTime() };
        var serialized = JsonSerializer.Serialize(normalized);
        await secrets.SetAsync(SessionKey, serialized, cancellationToken);
    }

    public async Task<AccountSession?> LoadAsync(CancellationToken cancellationToken = default)
    {
        var serialized = await secrets.GetAsync(SessionKey, cancellationToken);
        if (string.IsNullOrWhiteSpace(serialized))
            return null;

        try
        {
            var session = JsonSerializer.Deserialize<AccountSession>(serialized);
            if (session is null
                || string.IsNullOrWhiteSpace(session.AccountId)
                || string.IsNullOrWhiteSpace(session.AccessToken)
                || string.IsNullOrWhiteSpace(session.RefreshToken))
                return null;

            return session with { ExpiresAtUtc = session.ExpiresAtUtc.ToUniversalTime() };
        }
        catch (JsonException)
        {
            // Treat corrupt or incompatible persisted data as no session.
            return null;
        }
    }

    public Task ClearAsync(CancellationToken cancellationToken = default) =>
        secrets.RemoveAsync(SessionKey, cancellationToken);
}
