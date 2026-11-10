using AudibleApi;
using AudibleApi.Authorization;
using Dinah.Core;
using Libation.Mobile.Core;

namespace Libation.Mobile.Audible;

/// <summary>
/// Implements Audible's pinned ExternalLogin and device-registration flow.
/// The final response URL is captured from the validated in-app Audible sign-in page;
/// passwords and browser cookies are never handled by this adapter.
/// </summary>
public sealed class AudibleAccountAuthenticator(
    AudibleIdentitySnapshotStore snapshotStore,
    AudiblePkceRegistrationClient registrationClient,
    AudibleAndroidDeviceProfile deviceProfile,
    ISystemDateTime systemDateTime) : IAccountAuthenticator
{
    private static readonly TimeSpan ChallengeLifetime = TimeSpan.FromMinutes(10);
    private readonly AudibleIdentityLifecycleCoordinator lifecycleCoordinator = snapshotStore.LifecycleCoordinator;
    private readonly object gate = new();
    private readonly Dictionary<string, PendingLogin> pending = new(StringComparer.Ordinal);

    public Task<ExternalSignInChallenge> BeginSignInAsync(
        AccountSignInRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        var challenge = AudibleOAuthFlow.Create(request.Marketplace);
        var id = Guid.NewGuid().ToString("N");
        var expires = systemDateTime.UtcNow.Add(ChallengeLifetime);
        lock (gate)
        {
            pending.Clear();
            pending.Add(id, new PendingLogin(challenge, expires));
        }
        return Task.FromResult(new ExternalSignInChallenge(id, challenge.AuthorizationUri, expires));
    }

    public async Task<AccountSession> CompleteSignInAsync(
        string challengeId, string responseUrl, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(challengeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(responseUrl);
        cancellationToken.ThrowIfCancellationRequested();

        var snapshot = await lifecycleCoordinator.CreateAndSaveAsync(
            async token =>
            {
                PendingLogin state;
                lock (gate)
                {
                    if (!pending.Remove(challengeId, out state!))
                        throw new InvalidOperationException("The Audible sign-in attempt is missing, expired, or already used.");
                }
                if (state.ExpiresAtUtc <= systemDateTime.UtcNow)
                    throw new InvalidOperationException("The Audible sign-in attempt expired. Start again.");
                if (!Uri.TryCreate(responseUrl, UriKind.Absolute, out var responseUri)
                    || !string.Equals(responseUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(responseUri.Host, state.Challenge.CallbackUri.Host, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(responseUri.AbsolutePath.TrimEnd('/'), "/ap/maplanding", StringComparison.OrdinalIgnoreCase)
                    || !responseUri.IsDefaultPort || !string.IsNullOrEmpty(responseUri.UserInfo))
                    throw new InvalidOperationException("The Audible sign-in response does not match the selected marketplace callback.");

                var authorizationCode = System.Web.HttpUtility.ParseQueryString(responseUri.Query)["openid.oa2.authorization_code"];
                if (string.IsNullOrWhiteSpace(authorizationCode))
                    throw new InvalidOperationException("Audible did not return an authorization code.");

                var registered = await registrationClient.RegisterAsync(
                    state.Challenge, authorizationCode, deviceProfile, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                return registered;
            },
            async (createdSnapshot, token) =>
            {
                await snapshotStore.SaveAsync(createdSnapshot, token).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);

        var identity = AudibleIdentitySnapshotMapper.Restore(snapshot);
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
        => lifecycleCoordinator.ClearAsync(
            token => snapshotStore.ClearAsync(token), cancellationToken);

    public async Task<AccountSession> RefreshAsync(
        AccountSession currentSession, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(currentSession);
        cancellationToken.ThrowIfCancellationRequested();

        var snapshot = await snapshotStore.LoadAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("No saved Audible identity is available. Sign in again.");

        cancellationToken.ThrowIfCancellationRequested();
        var identity = AudibleIdentitySnapshotMapper.Restore(snapshot);
        var maintainer = new AudibleIdentityMaintainer(
            identity,
            new Authorize(identity.Locale),
            systemDateTime,
            snapshotStore);

        // The maintainer persists the complete identity before exposing a refreshed token.
        var accessToken = await maintainer.GetAccessTokenAsync().ConfigureAwait(false)
            ?? throw new InvalidOperationException("Audible did not provide an access token.");

        cancellationToken.ThrowIfCancellationRequested();
        var refreshToken = identity.RefreshToken
            ?? throw new InvalidOperationException("Audible did not provide a refresh token.");

        var expiry = accessToken.Expires;
        if (expiry.Kind != DateTimeKind.Utc)
            expiry = DateTime.SpecifyKind(expiry, DateTimeKind.Utc);

        return new AccountSession(
            identity.AmazonAccountId ?? currentSession.AccountId,
            accessToken.Reveal(),
            refreshToken.Reveal(),
            new DateTimeOffset(expiry));
    }

    private static Uri GetExpectedCallback(Uri authorizationUri)
    {
        var returnTo = HttpUtility.ParseQueryString(authorizationUri.Query)["openid.return_to"];
        if (!Uri.TryCreate(returnTo, UriKind.Absolute, out var callbackUri)
            || !string.Equals(callbackUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(callbackUri.Host)
            || !string.IsNullOrEmpty(callbackUri.UserInfo)
            || !callbackUri.IsDefaultPort
            || !string.Equals(callbackUri.AbsolutePath.TrimEnd('/'), "/ap/maplanding", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Audible did not provide a valid marketplace callback URL.");

        return callbackUri;
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

    private sealed record PendingLogin(AudibleOAuthChallenge Challenge, DateTime ExpiresAtUtc);
}
