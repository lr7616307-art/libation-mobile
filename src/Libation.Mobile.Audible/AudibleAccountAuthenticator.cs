using AudibleApi;
using AudibleApi.Authorization;
using Dinah.Core;
using Libation.Mobile.Core;

namespace Libation.Mobile.Audible;

/// <summary>
/// Implements Audible's external-browser OAuth/device-registration flow.
/// The user returns the final HTTPS URL from the address bar; passwords and
/// browser cookies are never handled by this adapter.
/// </summary>
public sealed class AudibleAccountAuthenticator(
    AudibleIdentitySnapshotStore snapshotStore,
    ISystemDateTime systemDateTime) : IAccountAuthenticator
{
    private static readonly TimeSpan ChallengeLifetime = TimeSpan.FromMinutes(10);
    private readonly object gate = new();
    private readonly Dictionary<string, PendingLogin> pending = new(StringComparer.Ordinal);

    public Task<ExternalSignInChallenge> BeginSignInAsync(
        AccountSignInRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        var locale = Localization.Get(request.Marketplace);
        if (locale == Locale.Empty || string.IsNullOrWhiteSpace(locale.Name))
            throw new ArgumentException("The selected Audible marketplace is not supported.", nameof(request));
        var login = new ExternalLogin(locale);
        var loginUrl = login.GetLoginUrl();
        if (!Uri.TryCreate(loginUrl, UriKind.Absolute, out var authorizationUri)
            || authorizationUri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("Audible returned an invalid sign-in URL.");
        var id = Guid.NewGuid().ToString("N");
        var expires = systemDateTime.UtcNow.Add(ChallengeLifetime);
        lock (gate)
        {
            pending.Clear();
            pending.Add(id, new PendingLogin(login, expires));
        }
        return Task.FromResult(new ExternalSignInChallenge(id, authorizationUri, expires));
    }

    public async Task<AccountSession> CompleteSignInAsync(
        string challengeId, string responseUrl, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(challengeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(responseUrl);
        cancellationToken.ThrowIfCancellationRequested();
        PendingLogin state;
        lock (gate)
        {
            if (!pending.Remove(challengeId, out state!))
                throw new InvalidOperationException("The Audible sign-in attempt is missing, expired, or already used.");
        }
        if (state.ExpiresAtUtc <= systemDateTime.UtcNow)
            throw new InvalidOperationException("The Audible sign-in attempt expired. Start again.");
        var identity = state.Login.Login(responseUrl);
        var authorization = identity.Authorization
            ?? throw new InvalidOperationException("Audible did not return an authorization code.");
        cancellationToken.ThrowIfCancellationRequested();
        var authorize = new Authorize(identity.Locale);
        var registration = await authorize.RegisterAsync(authorization).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        RegistrationParser.ParseRegistrationIntoIdentity(registration, identity, systemDateTime);
        if (!identity.IsValid)
            throw new InvalidOperationException("Audible device registration did not produce a complete identity.");
        await snapshotStore.SaveAsync(Capture(identity), cancellationToken).ConfigureAwait(false);
        var refreshToken = identity.RefreshToken
            ?? throw new InvalidOperationException("Audible did not return a refresh token.");
        var expiry = identity.ExistingAccessToken.Expires;
        if (expiry.Kind != DateTimeKind.Utc) expiry = DateTime.SpecifyKind(expiry, DateTimeKind.Utc);
        return new AccountSession(
            identity.AmazonAccountId ?? identity.DeviceSerialNumber ?? "audible-account",
            identity.ExistingAccessToken.Reveal(), refreshToken.Reveal(), new DateTimeOffset(expiry));
    }

    public async Task<bool> HasRestorableIdentityAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await snapshotStore.LoadAsync(cancellationToken).ConfigureAwait(false) is not null;
        }
        catch (AudibleIdentityRecoveryRequiredException)
        {
            return false;
        }
    }

    public Task CancelSignInAsync(string challengeId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(challengeId);
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate) pending.Remove(challengeId);
        return Task.CompletedTask;
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
        => snapshotStore.ClearAsync(cancellationToken);

    public Task<AccountSession> RefreshAsync(
        AccountSession currentSession, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(currentSession);
        cancellationToken.ThrowIfCancellationRequested();
        throw new NotSupportedException(
            "Use AudibleIdentityMaintainer for Audible token refresh so the complete identity remains consistent.");
    }

    private static AudibleIdentitySnapshot Capture(Identity identity)
    {
        var privateKey = identity.PrivateKey
            ?? throw new InvalidOperationException("Audible did not return a device private key.");
        var adpToken = identity.AdpToken
            ?? throw new InvalidOperationException("Audible did not return an ADP token.");
        var refreshToken = identity.RefreshToken
            ?? throw new InvalidOperationException("Audible did not return a refresh token.");
        var expiry = identity.ExistingAccessToken.Expires;
        if (expiry.Kind != DateTimeKind.Utc) expiry = DateTime.SpecifyKind(expiry, DateTimeKind.Utc);
        return new AudibleIdentitySnapshot
        {
            SchemaVersion = AudibleIdentitySnapshot.CurrentSchemaVersion,
            Provider = AudibleIdentitySnapshot.ProviderName,
            AudibleApiVersion = AudibleIdentitySnapshot.SupportedAudibleApiVersion,
            LocaleName = identity.Locale.Name,
            AccessToken = identity.ExistingAccessToken.Reveal(),
            AccessTokenExpiresAtUtc = new DateTimeOffset(expiry),
            RefreshToken = refreshToken.Reveal(),
            PrivateKey = privateKey.Reveal(),
            AdpToken = adpToken.Reveal(),
            Cookies = (identity.Cookies ?? [])
                .Select(cookie => new AudibleCookieSnapshot
                {
                    Name = cookie.Key,
                    Value = cookie.Value.Reveal() ?? string.Empty
                }).ToArray(),
            DeviceSerialNumber = identity.DeviceSerialNumber,
            DeviceType = identity.DeviceType,
            AmazonAccountId = identity.AmazonAccountId,
            DeviceName = identity.DeviceName,
            StoreAuthenticationCookie = identity.StoreAuthenticationCookie.Reveal() ?? string.Empty
        };
    }

    private sealed record PendingLogin(ExternalLogin Login, DateTime ExpiresAtUtc);
}
