using System.Security.Cryptography;
using System.Text;
using AudibleApi;
using AudibleApi.Authorization;
using Libation.Mobile.Core;
using Dinah.Core;

namespace Libation.Mobile.Audible;

/// <summary>
/// Performs Audible's external-browser authorization and device registration.
/// The final browser URL is supplied by the user; no password or browser cookies
/// are collected by this application.
/// </summary>
public sealed class AudibleExternalSignInService(AudibleIdentitySnapshotStore snapshots)
{
    private static readonly TimeSpan ChallengeLifetime = TimeSpan.FromMinutes(10);
    private const int MaximumResponseUrlBytes = 16 * 1024;
    private readonly SemaphoreSlim gate = new(1, 1);
    private PendingSignIn? pending;

    public async Task<ExternalSignInChallenge> BeginSignInAsync(
        AccountSignInRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();

        var locale = Localization.Get(request.Marketplace);
        if (locale == Locale.Empty || string.IsNullOrWhiteSpace(locale.Name))
            throw new ArgumentException("The requested Audible marketplace is not supported.", nameof(request));

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            pending = null;
            var externalLogin = new ExternalLogin(locale);
            var loginUrl = externalLogin.GetLoginUrl();
            if (!Uri.TryCreate(loginUrl, UriKind.Absolute, out var authorizationUri)
                || authorizationUri.Scheme != Uri.UriSchemeHttps
                || string.IsNullOrWhiteSpace(authorizationUri.Host))
                throw new InvalidOperationException("Audible returned an invalid authorization URL.");

            var challenge = new ExternalSignInChallenge(
                Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
                authorizationUri,
                DateTimeOffset.UtcNow.Add(ChallengeLifetime));

            pending = new PendingSignIn(challenge, externalLogin);
            return challenge;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<AccountConnectionState> CompleteSignInAsync(
        string challengeId,
        string responseUrl,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(challengeId);
        ValidateResponseUrl(responseUrl);

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var active = pending;
            if (active is null
                || !CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(active.Challenge.ChallengeId),
                    Encoding.UTF8.GetBytes(challengeId)))
                throw new InvalidOperationException("There is no matching active Audible sign-in attempt.");

            if (active.Challenge.ExpiresAtUtc <= DateTimeOffset.UtcNow)
            {
                pending = null;
                throw new InvalidOperationException("The Audible sign-in attempt expired. Start again.");
            }

            pending = null;
            cancellationToken.ThrowIfCancellationRequested();

            var identity = active.ExternalLogin.Login(responseUrl);
            var authorization = identity.Authorization
                ?? throw new InvalidOperationException("Audible did not return an authorization grant.");

            var systemDateTime = new SystemDateTime();
            var authorize = new Authorize(identity.Locale);
            var registration = await authorize.RegisterAsync(authorization).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            RegistrationParser.ParseRegistrationIntoIdentity(registration, identity, systemDateTime);
            if (!identity.IsValid)
                throw new InvalidOperationException("Audible device registration did not produce a valid identity.");

            await snapshots.SaveAsync(Capture(identity), cancellationToken).ConfigureAwait(false);

            var expiry = identity.ExistingAccessToken.Expires;
            if (expiry.Kind != DateTimeKind.Utc)
                expiry = DateTime.SpecifyKind(expiry, DateTimeKind.Utc);

            return new AccountConnectionState(
                AccountConnectionStatus.Connected,
                identity.AmazonAccountId,
                new DateTimeOffset(expiry));
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task CancelSignInAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { pending = null; }
        finally { gate.Release(); }
    }

    private static AudibleIdentitySnapshot Capture(Identity identity)
    {
        var privateKey = identity.PrivateKey
            ?? throw new InvalidOperationException("Audible did not provide a device private key.");
        var adpToken = identity.AdpToken
            ?? throw new InvalidOperationException("Audible did not provide an ADP token.");
        var refreshToken = identity.RefreshToken
            ?? throw new InvalidOperationException("Audible did not provide a refresh token.");

        var expiry = identity.ExistingAccessToken.Expires;
        if (expiry.Kind != DateTimeKind.Utc)
            expiry = DateTime.SpecifyKind(expiry, DateTimeKind.Utc);

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
            Cookies = identity.Cookies.Select(cookie => new AudibleCookieSnapshot
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

    private static void ValidateResponseUrl(string responseUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(responseUrl);
        if (Encoding.UTF8.GetByteCount(responseUrl) > MaximumResponseUrlBytes)
            throw new ArgumentException("The Audible response URL exceeds the supported size limit.", nameof(responseUrl));

        if (!Uri.TryCreate(responseUrl, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo))
            throw new ArgumentException("The Audible response must be an absolute HTTPS URL without embedded credentials.", nameof(responseUrl));
    }

    private sealed record PendingSignIn(ExternalSignInChallenge Challenge, ExternalLogin ExternalLogin);
}
