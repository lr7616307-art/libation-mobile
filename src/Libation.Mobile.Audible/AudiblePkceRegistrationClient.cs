using AudibleApi;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Libation.Mobile.Audible;

/// <summary>
/// Performs the PKCE device-registration exchange after the in-app Amazon
/// callback. Device details are supplied by the Android host so the request
/// does not claim that an Android handset is an iPhone.
/// </summary>
public sealed class AudiblePkceRegistrationClient(HttpClient httpClient)
{
    
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<AudibleIdentitySnapshot> RegisterAsync(
        AudibleOAuthChallenge challenge,
        string authorizationCode,
        AudibleAndroidDeviceProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(challenge);
        ArgumentException.ThrowIfNullOrWhiteSpace(authorizationCode);
        ArgumentNullException.ThrowIfNull(profile);
        profile.Validate();

        if (!string.Equals(challenge.Marketplace, challenge.Marketplace.Trim().ToLowerInvariant(), StringComparison.Ordinal))
            throw new InvalidOperationException("The OAuth marketplace is invalid.");
        if (!string.Equals(challenge.AuthorizationUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(challenge.CallbackUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The OAuth challenge must use HTTPS.");

        var amazonDomain = challenge.AuthorizationUri.Host;
        if (amazonDomain.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
            amazonDomain = amazonDomain[4..];
        if (!amazonDomain.StartsWith("amazon.", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The OAuth authorization host is not an Amazon marketplace.");
        if (!challenge.CallbackUri.Host.StartsWith("www.audible.", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The OAuth callback is not an Audible marketplace.");
        if (challenge.DeviceSerial.Length != 20 || challenge.DeviceSerial.Any(c => !Uri.IsHexDigit(c)))
            throw new InvalidOperationException("The Android device serial must be 20 hexadecimal characters.");

        var serialAndType = $"{challenge.DeviceSerial}#{AudibleOAuthFlow.AndroidDeviceType}";
        var clientIdHex = Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(serialAndType)).ToLowerInvariant();
        var request = new RegistrationRequest
        {
            RequestedTokenType = ["bearer", "mac_dms", "website_cookies", "store_authentication_cookie"],
            Cookies = new RegistrationCookies { WebsiteCookies = [], Domain = "www." + amazonDomain },
            RegistrationData = new RegistrationData
            {
                Domain = "DeviceLegacy",
                AppVersion = profile.AppVersion,
                DeviceSerial = challenge.DeviceSerial,
                DeviceType = AudibleOAuthFlow.AndroidDeviceType,
                DeviceName = profile.DeviceName,
                OsVersion = profile.OsVersion,
                SoftwareVersion = profile.SoftwareVersion,
                DeviceModel = profile.Model,
                AppName = "com.audible.application"
            },
            AuthData = new RegistrationAuthData
            {
                ClientId = clientIdHex,
                AuthorizationCode = authorizationCode,
                CodeVerifier = challenge.CodeVerifier,
                CodeAlgorithm = "SHA-256",
                ClientDomain = "DeviceLegacy"
            },
            RequestedExtensions = ["device_info", "customer_info"]
        };

        using var response = await httpClient.PostAsJsonAsync(
            $"https://api.{amazonDomain}/auth/register", request, JsonOptions, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"Audible device registration failed with HTTP {(int)response.StatusCode}.",
                null,
                response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<RegistrationEnvelope>(
            JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("Audible returned an empty registration response.");

        var success = payload.Response?.Success
            ?? throw new InvalidDataException("Audible registration response did not contain success data.");
        var tokens = success.Tokens
            ?? throw new InvalidDataException("Audible registration response did not contain tokens.");
        var extensions = success.Extensions
            ?? throw new InvalidDataException("Audible registration response did not contain extensions.");

        var bearer = tokens.Bearer
            ?? throw new InvalidDataException("Audible registration response did not contain bearer credentials.");
        var macDms = tokens.MacDms
            ?? throw new InvalidDataException("Audible registration response did not contain device credentials.");
        var device = extensions.DeviceInfo
            ?? throw new InvalidDataException("Audible registration response did not contain device information.");
        var customer = extensions.CustomerInfo
            ?? throw new InvalidDataException("Audible registration response did not contain customer information.");

        if (!long.TryParse(bearer.ExpiresIn, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var expiresIn)
            || expiresIn <= 0)
            throw new InvalidDataException("Audible returned an invalid access-token lifetime.");

        var cookies = tokens.WebsiteCookies ?? [];
        var snapshot = new AudibleIdentitySnapshot
        {
            SchemaVersion = AudibleIdentitySnapshot.CurrentSchemaVersion,
            Provider = AudibleIdentitySnapshot.ProviderName,
            AudibleApiVersion = AudibleIdentitySnapshot.SupportedAudibleApiVersion,
            LocaleName = Localization.Get(challenge.Marketplace).Name,
            AccessToken = bearer.AccessToken ?? string.Empty,
            AccessTokenExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(expiresIn),
            RefreshToken = bearer.RefreshToken ?? string.Empty,
            PrivateKey = macDms.DevicePrivateKey ?? string.Empty,
            AdpToken = macDms.AdpToken ?? string.Empty,
            Cookies = cookies.Select(c => new AudibleCookieSnapshot
            {
                Name = c.Name ?? string.Empty,
                Value = c.Value ?? string.Empty
            }).ToArray(),
            DeviceSerialNumber = device.DeviceSerialNumber ?? challenge.DeviceSerial,
            DeviceType = device.DeviceType ?? AudibleOAuthFlow.AndroidDeviceType,
            AmazonAccountId = customer.UserId,
            DeviceName = device.DeviceName ?? profile.DeviceName,
            StoreAuthenticationCookie = tokens.StoreAuthenticationCookie?.Cookie ?? string.Empty
        };

        // Validate the complete identity envelope before any caller persists it.
        _ = AudibleIdentitySnapshotSerializer.Serialize(snapshot);
        return snapshot;
    }

    private sealed class RegistrationRequest
    {
        [JsonPropertyName("requested_token_type")] public required string[] RequestedTokenType { get; init; }
        [JsonPropertyName("cookies")] public required RegistrationCookies Cookies { get; init; }
        [JsonPropertyName("registration_data")] public required RegistrationData RegistrationData { get; init; }
        [JsonPropertyName("auth_data")] public required RegistrationAuthData AuthData { get; init; }
        [JsonPropertyName("requested_extensions")] public required string[] RequestedExtensions { get; init; }
    }

    private sealed class RegistrationCookies
    {
        [JsonPropertyName("website_cookies")] public required object[] WebsiteCookies { get; init; }
        [JsonPropertyName("domain")] public required string Domain { get; init; }
    }

    private sealed class RegistrationData
    {
        [JsonPropertyName("domain")] public required string Domain { get; init; }
        [JsonPropertyName("app_version")] public required string AppVersion { get; init; }
        [JsonPropertyName("device_serial")] public required string DeviceSerial { get; init; }
        [JsonPropertyName("device_type")] public required string DeviceType { get; init; }
        [JsonPropertyName("device_name")] public required string DeviceName { get; init; }
        [JsonPropertyName("os_version")] public required string OsVersion { get; init; }
        [JsonPropertyName("software_version")] public required string SoftwareVersion { get; init; }
        [JsonPropertyName("device_model")] public required string DeviceModel { get; init; }
        [JsonPropertyName("app_name")] public required string AppName { get; init; }
    }

    private sealed class RegistrationAuthData
    {
        [JsonPropertyName("client_id")] public required string ClientId { get; init; }
        [JsonPropertyName("authorization_code")] public required string AuthorizationCode { get; init; }
        [JsonPropertyName("code_verifier")] public required string CodeVerifier { get; init; }
        [JsonPropertyName("code_algorithm")] public required string CodeAlgorithm { get; init; }
        [JsonPropertyName("client_domain")] public required string ClientDomain { get; init; }
    }

    private sealed class RegistrationEnvelope
    {
        [JsonPropertyName("response")] public RegistrationResponse? Response { get; init; }
    }
    private sealed class RegistrationResponse
    {
        [JsonPropertyName("success")] public RegistrationSuccess? Success { get; init; }
    }
    private sealed class RegistrationSuccess
    {
        [JsonPropertyName("tokens")] public RegistrationTokens? Tokens { get; init; }
        [JsonPropertyName("extensions")] public RegistrationExtensions? Extensions { get; init; }
    }
    private sealed class RegistrationTokens
    {
        [JsonPropertyName("bearer")] public BearerTokens? Bearer { get; init; }
        [JsonPropertyName("mac_dms")] public MacDmsTokens? MacDms { get; init; }
        [JsonPropertyName("website_cookies")] public RegistrationCookie[]? WebsiteCookies { get; init; }
        [JsonPropertyName("store_authentication_cookie")] public StoreCookie? StoreAuthenticationCookie { get; init; }
    }
    private sealed class BearerTokens
    {
        [JsonPropertyName("access_token")] public string? AccessToken { get; init; }
        [JsonPropertyName("refresh_token")] public string? RefreshToken { get; init; }
        [JsonPropertyName("expires_in")] public string? ExpiresIn { get; init; }
    }
    private sealed class MacDmsTokens
    {
        [JsonPropertyName("device_private_key")] public string? DevicePrivateKey { get; init; }
        [JsonPropertyName("adp_token")] public string? AdpToken { get; init; }
    }
    private sealed class RegistrationCookie
    {
        [JsonPropertyName("Name")] public string? Name { get; init; }
        [JsonPropertyName("Value")] public string? Value { get; init; }
    }
    private sealed class StoreCookie
    {
        [JsonPropertyName("cookie")] public string? Cookie { get; init; }
    }
    private sealed class RegistrationExtensions
    {
        [JsonPropertyName("device_info")] public RegisteredDevice? DeviceInfo { get; init; }
        [JsonPropertyName("customer_info")] public RegisteredCustomer? CustomerInfo { get; init; }
    }
    private sealed class RegisteredDevice
    {
        [JsonPropertyName("device_name")] public string? DeviceName { get; init; }
        [JsonPropertyName("device_serial_number")] public string? DeviceSerialNumber { get; init; }
        [JsonPropertyName("device_type")] public string? DeviceType { get; init; }
    }
    private sealed class RegisteredCustomer
    {
        [JsonPropertyName("user_id")] public string? UserId { get; init; }
    }
}

/// <summary>Actual Android device/build values used for truthful registration metadata.</summary>
public sealed record AudibleAndroidDeviceProfile(
    string Model,
    string OsVersion,
    string AppVersion,
    string SoftwareVersion,
    string DeviceName)
{
    public void Validate()
    {
        foreach (var (value, field) in new[]
        {
            (Model, nameof(Model)), (OsVersion, nameof(OsVersion)),
            (AppVersion, nameof(AppVersion)), (SoftwareVersion, nameof(SoftwareVersion)),
            (DeviceName, nameof(DeviceName))
        })
            if (string.IsNullOrWhiteSpace(value) || value.Length > 256)
                throw new ArgumentException($"Android device profile field {field} is invalid.");
    }
}
