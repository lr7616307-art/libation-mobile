using System.Security.Cryptography;
using AudibleApi;
using AudibleApi.Authorization;
using AudibleApi.Cryptography;

namespace Libation.Mobile.Audible;

/// <summary>
/// Reconstructs AudibleApi's strongly typed identity values from a validated
/// app snapshot. No network requests or token refreshes are performed here.
/// </summary>
public static class AudibleIdentitySnapshotMapper
{
    public static Identity Restore(AudibleIdentitySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        // Run the strict envelope validation first, then let AudibleApi validate
        // the cryptographic/token value objects themselves.
        _ = AudibleIdentitySnapshotSerializer.Serialize(snapshot);

        var locale = Localization.Get(snapshot.LocaleName);
        if (locale == Locale.Empty || string.IsNullOrWhiteSpace(locale.Name))
            throw new InvalidDataException("The saved Audible locale is not supported.");

        try
        {
            var privateKey = new PrivateKey(snapshot.PrivateKey);
            var adpToken = new AdpToken(snapshot.AdpToken);
            var accessToken = new AccessToken(snapshot.AccessToken, snapshot.AccessTokenExpiresAtUtc.UtcDateTime);
            var refreshToken = new RefreshToken(snapshot.RefreshToken);
            var cookies = snapshot.Cookies.Select(cookie =>
                new KeyValuePair<string, Dinah.Core.Security.SecretString>(cookie.Name, cookie.Value));

            var identity = new Identity(locale);
            identity.Update(
                privateKey,
                adpToken,
                accessToken,
                refreshToken,
                cookies,
                snapshot.DeviceSerialNumber,
                snapshot.DeviceType,
                snapshot.AmazonAccountId,
                snapshot.DeviceName,
                snapshot.StoreAuthenticationCookie);
            return identity;
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or CryptographicException)
        {
            throw new InvalidDataException("The saved Audible credentials are not valid AudibleApi values.", ex);
        }
    }
}
