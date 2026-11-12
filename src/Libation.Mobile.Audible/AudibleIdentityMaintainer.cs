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
    private readonly IAudibleTokenRefresher tokenRefresher;
    private readonly ISystemDateTime systemDateTime;
    private readonly AudibleIdentitySnapshotStore snapshotStore;
    private readonly AudibleIdentityLifecycleCoordinator lifecycleCoordinator;
    private readonly long identityGeneration;
    private bool disconnected;

    public AudibleIdentityMaintainer(
        Identity identity,
        IAudibleTokenRefresher tokenRefresher,
        ISystemDateTime systemDateTime,
        AudibleIdentitySnapshotStore snapshotStore,
        AudibleIdentityLifecycleCoordinator? lifecycleCoordinator = null,
        long? identityGeneration = null)
    {
        this.identity = identity ?? throw new ArgumentNullException(nameof(identity));
        this.tokenRefresher = tokenRefresher ?? throw new ArgumentNullException(nameof(tokenRefresher));
        this.systemDateTime = systemDateTime ?? throw new ArgumentNullException(nameof(systemDateTime));
        this.snapshotStore = snapshotStore ?? throw new ArgumentNullException(nameof(snapshotStore));
        this.lifecycleCoordinator = lifecycleCoordinator ?? snapshotStore.LifecycleCoordinator;
        this.identityGeneration = identityGeneration ?? this.lifecycleCoordinator.CurrentGeneration;

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

    /// <summary>
    /// Clears the persisted identity behind the same gate used by refresh. Once this
    /// completes, this maintainer instance is terminal and cannot refresh or serve credentials.
    /// </summary>
    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        if (disconnected)
            return;

        await lifecycleCoordinator.ClearAsync(async token =>
        {
            await snapshotStore.ClearAsync(token).ConfigureAwait(false);
            disconnected = true;
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task EnsureFreshAsync()
    {
        // The generation is bound to this restored identity, not read afresh for each
        // request. A logout or new login therefore permanently invalidates every API
        // session created from the previous identity.
        await lifecycleCoordinator.ExecuteCurrentAsync(
            identityGeneration,
            async cancellationToken =>
            {
                if (disconnected)
                    throw new InvalidOperationException("The Audible session has been disconnected.");

                // Several API objects can be restored from the same snapshot. Reload
                // while holding the shared lifecycle gate so a later maintainer sees
                // tokens rotated by an earlier one instead of reusing an old refresh token.
                var latestSnapshot = await snapshotStore.LoadAsync(cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("The Audible identity has been disconnected or replaced.");
                var latestIdentity = AudibleIdentitySnapshotMapper.Restore(latestSnapshot);
                if (!RepresentsSameIdentity(identity, latestIdentity))
                    throw new InvalidOperationException("This Audible API session belongs to an identity that has been replaced.");

                ApplyIdentity(identity, latestIdentity);
                if (identity.ExistingAccessToken.Expires > systemDateTime.UtcNow.AddMinutes(30))
                    return (RefreshResult: (AudibleTokenRefreshResult?)null, Refreshed: false,
                        RegistrationProfileId: latestSnapshot.RegistrationProfileId);

                var refreshToken = identity.RefreshToken
                    ?? throw new InvalidOperationException("The Audible refresh credential is unavailable.");
                var refreshed = await tokenRefresher.RefreshAsync(
                    identity.Locale, refreshToken, cancellationToken).ConfigureAwait(false);
                return (RefreshResult: (AudibleTokenRefreshResult?)refreshed, Refreshed: true,
                    RegistrationProfileId: latestSnapshot.RegistrationProfileId);
            },
            async (result, cancellationToken) =>
            {
                if (disconnected)
                    throw new InvalidOperationException("The Audible session has been disconnected.");
                if (!result.Refreshed)
                    return;

                var refreshResult = result.RefreshResult
                    ?? throw new InvalidOperationException("Audible token refresh returned no result.");
                var previousAccessToken = identity.ExistingAccessToken;
                var previousRefreshToken = identity.RefreshToken
                    ?? throw new InvalidOperationException("The Audible refresh credential is unavailable.");

                ApplyTokens(identity, refreshResult.AccessToken,
                    refreshResult.RefreshToken ?? previousRefreshToken);
                try
                {
                    await snapshotStore.SaveAsync(Capture(identity, result.RegistrationProfileId), cancellationToken).ConfigureAwait(false);
                }
                catch (Exception persistenceException)
                {
                    ApplyTokens(identity, previousAccessToken, previousRefreshToken);
                    throw new InvalidOperationException(
                        "The refreshed Audible session could not be securely saved.", persistenceException);
                }
            },
            CancellationToken.None).ConfigureAwait(false);
    }

    private static bool RepresentsSameIdentity(Identity expected, Identity actual)
        => !string.IsNullOrWhiteSpace(expected.DeviceSerialNumber)
            && !string.IsNullOrWhiteSpace(expected.DeviceType)
            && string.Equals(expected.DeviceSerialNumber, actual.DeviceSerialNumber, StringComparison.Ordinal)
            && string.Equals(expected.DeviceType, actual.DeviceType, StringComparison.Ordinal)
            && string.Equals(expected.AmazonAccountId, actual.AmazonAccountId, StringComparison.Ordinal)
            && string.Equals(expected.Locale.Name, actual.Locale.Name, StringComparison.OrdinalIgnoreCase);

    private static void ApplyIdentity(Identity target, Identity source)
        => target.Update(
            source.PrivateKey ?? throw new InvalidOperationException("The Audible private key is unavailable."),
            source.AdpToken ?? throw new InvalidOperationException("The Audible ADP token is unavailable."),
            source.ExistingAccessToken,
            source.RefreshToken ?? throw new InvalidOperationException("The Audible refresh token is unavailable."),
            source.Cookies,
            source.DeviceSerialNumber,
            source.DeviceType,
            source.AmazonAccountId,
            source.DeviceName,
            source.StoreAuthenticationCookie);

    private static void ApplyTokens(Identity target, AccessToken accessToken, RefreshToken refreshToken)
        => target.Update(
            target.PrivateKey ?? throw new InvalidOperationException("The Audible private key is unavailable."),
            target.AdpToken ?? throw new InvalidOperationException("The Audible ADP token is unavailable."),
            accessToken,
            refreshToken,
            target.Cookies,
            target.DeviceSerialNumber,
            target.DeviceType,
            target.AmazonAccountId,
            target.DeviceName,
            target.StoreAuthenticationCookie);

    private static AudibleIdentitySnapshot Capture(Identity source, string registrationProfileId)
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
            RegistrationProfileId = registrationProfileId,
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
