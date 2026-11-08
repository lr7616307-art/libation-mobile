using AudibleApi;
using AudibleApi.Authorization;
using AudibleApi.Cryptography;
using Dinah.Core;

namespace Libation.Mobile.Audible;

/// <summary>
/// App-owned, serialized access-token maintenance for an already restored Audible identity.
/// Unlike AudibleApi.IdentityMaintainer, this adapter never deregisters/re-registers the device
/// after a refresh failure and never reports a refreshed token as usable before it is persisted.
/// </summary>
public sealed class AudibleIdentityMaintainer : IIdentityMaintainer
{
    private readonly Identity identity;
    private readonly IAuthorize authorize;
    private readonly ISystemDateTime systemDateTime;
    private readonly AudibleIdentitySnapshotStore snapshotStore;

    // Catalogue sync, playback downloads, and account refresh can each create
    // their own API/maintainer from the same saved identity. Serialize refresh
    // across all those instances so rotating credentials cannot race.
    private static readonly SemaphoreSlim RefreshGate = new(1, 1);

    public AudibleIdentityMaintainer(
        Identity identity,
        IAuthorize authorize,
        ISystemDateTime systemDateTime,
        AudibleIdentitySnapshotStore snapshotStore)
    {
        this.identity = identity ?? throw new ArgumentNullException(nameof(identity));
        this.authorize = authorize ?? throw new ArgumentNullException(nameof(authorize));
        this.systemDateTime = systemDateTime ?? throw new ArgumentNullException(nameof(systemDateTime));
        this.snapshotStore = snapshotStore ?? throw new ArgumentNullException(nameof(snapshotStore));

        if (!identity.IsValid || identity.PrivateKey is null || identity.AdpToken is null || identity.RefreshToken is null)
            throw new ArgumentException("A complete restored Audible identity is required.", nameof(identity));
    }

    public ISystemDateTime SystemDateTime => systemDateTime;
    public Locale Locale => identity.Locale;
    public string? DeviceSerialNumber => identity.DeviceSerialNumber;
    public string? DeviceType => identity.DeviceType;
    public string? AmazonAccountId => identity.AmazonAccountId;

    public async Task<AccessToken?> GetAccessTokenAsync()
    {
        await EnsureFreshAsync().ConfigureAwait(false);
        return identity.ExistingAccessToken;
    }

    public async Task<AdpToken?> GetAdpTokenAsync()
    {
        await EnsureFreshAsync().ConfigureAwait(false);
        return identity.AdpToken;
    }

    public async Task<PrivateKey?> GetPrivateKeyAsync()
    {
        await EnsureFreshAsync().ConfigureAwait(false);
        return identity.PrivateKey;
    }

    private async Task EnsureFreshAsync()
    {
        if (identity.ExistingAccessToken.Expires > systemDateTime.UtcNow)
            return;

        await RefreshGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (identity.ExistingAccessToken.Expires > systemDateTime.UtcNow)
                return;

            // Another API/maintainer instance may have refreshed the same account
            // while this instance was waiting. Reload the encrypted snapshot before
            // using a refresh token; never submit a stale credential after a peer
            // has already committed a newer identity.
            var latestSnapshot = await snapshotStore.LoadAsync().ConfigureAwait(false)
                ?? throw new InvalidOperationException("The saved Audible identity is unavailable. Sign in again.");
            var latestIdentity = AudibleIdentitySnapshotMapper.Restore(latestSnapshot);
            if (!string.Equals(identity.AmazonAccountId, latestIdentity.AmazonAccountId, StringComparison.Ordinal) ||
                !string.Equals(identity.Locale.Name, latestIdentity.Locale.Name, StringComparison.Ordinal))
                throw new InvalidOperationException("The saved Audible account changed during token refresh. Reconnect the account.");

            identity.Update(
                latestIdentity.PrivateKey,
                latestIdentity.AdpToken,
                latestIdentity.ExistingAccessToken,
                latestIdentity.RefreshToken,
                latestIdentity.Cookies,
                latestIdentity.DeviceSerialNumber,
                latestIdentity.DeviceType,
                latestIdentity.AmazonAccountId,
                latestIdentity.DeviceName,
                latestIdentity.StoreAuthenticationCookie);

            if (identity.ExistingAccessToken.Expires > systemDateTime.UtcNow)
                return;

            var refreshToken = identity.RefreshToken
                ?? throw new InvalidOperationException("The Audible refresh credential is unavailable.");
            var previousToken = identity.ExistingAccessToken;

            // Keep the current identity untouched until the remote refresh has succeeded.
            var refreshedToken = await authorize.RefreshAccessTokenAsync(refreshToken).ConfigureAwait(false);
            identity.Update(refreshedToken);

            try
            {
                await snapshotStore.SaveAsync(Capture(identity)).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                identity.Update(previousToken);
                throw;
            }
            catch (Exception persistenceException)
            {
                identity.Update(previousToken);
                throw new InvalidOperationException(
                    "The refreshed Audible session could not be securely saved.", persistenceException);
            }
        }
        finally
        {
            RefreshGate.Release();
        }
    }

    private static AudibleIdentitySnapshot Capture(Identity source)
    {
        var privateKey = source.PrivateKey
            ?? throw new InvalidOperationException("The Audible private key is unavailable.");
        var adpToken = source.AdpToken
            ?? throw new InvalidOperationException("The Audible ADP token is unavailable.");
        var refreshToken = source.RefreshToken
            ?? throw new InvalidOperationException("The Audible refresh token is unavailable.");

        var expiry = source.ExistingAccessToken.Expires;
        if (expiry.Kind != DateTimeKind.Utc)
            expiry = DateTime.SpecifyKind(expiry, DateTimeKind.Utc);

        return new AudibleIdentitySnapshot
        {
            SchemaVersion = AudibleIdentitySnapshot.CurrentSchemaVersion,
            Provider = AudibleIdentitySnapshot.ProviderName,
            AudibleApiVersion = AudibleIdentitySnapshot.SupportedAudibleApiVersion,
            LocaleName = source.Locale.Name,
            AccessToken = source.ExistingAccessToken.Reveal(),
            AccessTokenExpiresAtUtc = new DateTimeOffset(expiry),
            RefreshToken = refreshToken.Reveal(),
            PrivateKey = privateKey.Reveal(),
            AdpToken = adpToken.Reveal(),
            Cookies = source.Cookies.Select(cookie => new AudibleCookieSnapshot
            {
                Name = cookie.Key,
                Value = cookie.Value.Reveal() ?? string.Empty
            }).ToArray(),
            DeviceSerialNumber = source.DeviceSerialNumber,
            DeviceType = source.DeviceType,
            AmazonAccountId = source.AmazonAccountId,
            DeviceName = source.DeviceName,
            StoreAuthenticationCookie = source.StoreAuthenticationCookie.Reveal() ?? string.Empty
        };
    }

}
