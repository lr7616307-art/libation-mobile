using AudibleApi;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Security.Cryptography;
using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Libation.Mobile.Audible;

/// <summary>
/// Exchanges a LibriSync-compatible iPhone-profile PKCE authorization code for a
/// complete Audible identity. This is the profile used for Audible ADRM/AAC-LC
/// downloads; this client deliberately does not register as an Android Widevine device.
/// </summary>
public sealed class AudiblePkceRegistrationClient(HttpClient httpClient)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private const string DeviceType = AudibleOAuthFlow.IPhoneDeviceType;
    private const string AppVersion = "4.60";
    private const string OsVersion = "26.2";
    private const string SoftwareVersion = "46000856";
    private const string DeviceModel = "iPhone";
    private const string AppName = "Audible";
    private const string DeviceName =
        "%FIRST_NAME%%FIRST_NAME_POSSESSIVE_STRING%%DUPE_STRATEGY_1ST%Audible for iPhone";

    public async Task<AudibleIdentitySnapshot> RegisterAsync(
        AudibleOAuthChallenge challenge,
        string authorizationCode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(challenge);
        ArgumentException.ThrowIfNullOrWhiteSpace(authorizationCode);
        cancellationToken.ThrowIfCancellationRequested();

        if (!string.Equals(challenge.Marketplace, challenge.Marketplace.Trim().ToLowerInvariant(), StringComparison.Ordinal))
            throw new InvalidOperationException("The OAuth marketplace is invalid.");

        var locale = Localization.Get(challenge.Marketplace);
        var expectedLoginHost = locale.LoginUri().Host;
        var expectedCallbackHost = locale.LoginUri().Host;
        var cookieDomain = $".{locale.LoginDomain()}.{locale.TopDomain}";
        if (!AudibleOAuthFlow.IsMarketplaceCallback(challenge.Marketplace, challenge.CallbackUri))
            throw new InvalidOperationException("The Audible callback does not match the selected marketplace.");

        if (!challenge.AuthorizationUri.IsAbsoluteUri
            || !challenge.CallbackUri.IsAbsoluteUri
            || !string.Equals(challenge.AuthorizationUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(challenge.CallbackUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !challenge.AuthorizationUri.IsDefaultPort
            || !challenge.CallbackUri.IsDefaultPort
            || !string.IsNullOrEmpty(challenge.AuthorizationUri.UserInfo)
            || !string.IsNullOrEmpty(challenge.CallbackUri.UserInfo)
            || !string.Equals(challenge.AuthorizationUri.AbsolutePath, "/ap/signin", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(challenge.AuthorizationUri.Host, expectedLoginHost, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(challenge.CallbackUri.Host, expectedCallbackHost, StringComparison.OrdinalIgnoreCase)
            || !AudibleOAuthFlow.IsMarketplaceCallback(challenge.Marketplace, challenge.CallbackUri))
            throw new InvalidOperationException("The Audible OAuth challenge does not match the selected marketplace's iPhone sign-in and callback hosts.");

        if (challenge.DeviceSerial.Length != 40
            || challenge.DeviceSerial.Any(character => !char.IsAsciiLetterOrDigit(character)))
            throw new InvalidOperationException("The Audible iPhone-profile serial must be 40 ASCII letters or digits.");

        if (challenge.CodeVerifier.Length != 43
            || challenge.CodeVerifier.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_')))
            throw new InvalidOperationException("The Audible PKCE verifier is malformed.");

        var expectedCodeChallenge = Base64Url(SHA256.HashData(
            Encoding.ASCII.GetBytes(challenge.CodeVerifier)));
        if (!string.Equals(challenge.CodeChallenge, expectedCodeChallenge, StringComparison.Ordinal))
            throw new InvalidOperationException("The Audible PKCE verifier does not match its challenge.");

        var serialAndType = challenge.DeviceSerial + "#" + DeviceType;
        var clientIdHex = Convert.ToHexString(Encoding.UTF8.GetBytes(serialAndType)).ToLowerInvariant();
        var authorizationParameters = System.Web.HttpUtility.ParseQueryString(challenge.AuthorizationUri.Query);
        if (!string.Equals(authorizationParameters["openid.oa2.response_type"], "code", StringComparison.Ordinal)
            || !string.Equals(authorizationParameters["openid.oa2.code_challenge_method"], "S256", StringComparison.Ordinal)
            || !string.Equals(authorizationParameters["openid.oa2.code_challenge"], expectedCodeChallenge, StringComparison.Ordinal)
            || !string.Equals(authorizationParameters["openid.oa2.client_id"], "device:" + clientIdHex, StringComparison.Ordinal)
            || !string.Equals(authorizationParameters["openid.return_to"], challenge.CallbackUri.AbsoluteUri, StringComparison.Ordinal)
            || !string.Equals(authorizationParameters["pageId"], "amzn_audible_ios", StringComparison.Ordinal)
            || !string.Equals(authorizationParameters["openid.assoc_handle"],
                "amzn_audible_ios_" + locale.CountryCode, StringComparison.Ordinal)
            || !string.Equals(authorizationParameters["forceMobileLayout"], "true", StringComparison.Ordinal))
            throw new InvalidOperationException("The Audible authorization URL does not match the LibriSync iPhone registration profile or PKCE challenge.");

        var body = CreateIPhoneRegistrationBody(
            challenge, authorizationCode, clientIdHex, cookieDomain);

        using var message = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri(locale.RegistrationUri(), "/auth/register"))
        {
            Content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json")
        };
        message.Headers.TryAddWithoutValidation("Accept", "application/json");

        using var response = await httpClient.SendAsync(message, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                "Audible Android device registration failed with HTTP " + (int)response.StatusCode + ".",
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

        if (!string.Equals(device.DeviceType, DeviceType, StringComparison.Ordinal))
            throw new InvalidDataException("Audible did not confirm the LibriSync iPhone device type required for ADRM downloads.");
        if (!string.Equals(device.DeviceSerialNumber, challenge.DeviceSerial, StringComparison.Ordinal))
            throw new InvalidDataException("Audible returned a different device serial than the one registered.");

        if (!TryReadExpiresIn(bearer.ExpiresIn, out var expiresIn))
            throw new InvalidDataException("Audible returned an invalid access-token lifetime.");

        var snapshot = new AudibleIdentitySnapshot
        {
            SchemaVersion = AudibleIdentitySnapshot.CurrentSchemaVersion,
            Provider = AudibleIdentitySnapshot.ProviderName,
            AudibleApiVersion = AudibleIdentitySnapshot.SupportedAudibleApiVersion,
            RegistrationProfileId = AudibleOAuthFlow.RegistrationProfileId,
            LocaleName = locale.Name,
            AccessToken = bearer.AccessToken ?? string.Empty,
            AccessTokenExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(expiresIn),
            RefreshToken = bearer.RefreshToken ?? string.Empty,
            PrivateKey = macDms.DevicePrivateKey ?? string.Empty,
            AdpToken = macDms.AdpToken ?? string.Empty,
            Cookies = (tokens.WebsiteCookies ?? []).Select(cookie => new AudibleCookieSnapshot
            {
                Name = cookie.Name ?? string.Empty,
                Value = cookie.Value ?? string.Empty
            }).ToArray(),
            DeviceSerialNumber = device.DeviceSerialNumber,
            DeviceType = device.DeviceType,
            AmazonAccountId = customer.UserId,
            DeviceName = device.DeviceName ?? "Audible for iPhone",
            StoreAuthenticationCookie = tokens.StoreAuthenticationCookie?.Cookie ?? string.Empty
        };

        // Validate the complete identity envelope before any caller persists it.
        _ = AudibleIdentitySnapshotSerializer.Serialize(snapshot);
        return snapshot;
    }

    private static JObject CreateIPhoneRegistrationBody(
        AudibleOAuthChallenge challenge,
        string authorizationCode,
        string clientIdHex,
        string cookieDomain)
    {
        var registrationData = new JObject
        {
            ["domain"] = "Device",
            ["app_version"] = AppVersion,
            ["device_serial"] = challenge.DeviceSerial,
            ["device_type"] = DeviceType,
            ["device_name"] = DeviceName,
            ["os_version"] = OsVersion,
            ["software_version"] = SoftwareVersion,
            ["device_model"] = DeviceModel,
            ["app_name"] = AppName
        };
        var authData = new JObject
        {
            ["client_id"] = clientIdHex,
            ["authorization_code"] = authorizationCode,
            ["code_verifier"] = challenge.CodeVerifier,
            ["code_algorithm"] = "SHA-256",
            ["client_domain"] = "DeviceLegacy"
        };

        // This is the exact LibriSync iPhone registration shape. In particular,
        // it intentionally omits Android-only user_context_map.frc and device_metadata.
        return new JObject
        {
            ["requested_token_type"] = new JArray("bearer", "mac_dms", "website_cookies", "store_authentication_cookie"),
            ["cookies"] = new JObject
            {
                ["domain"] = cookieDomain,
                ["website_cookies"] = new JArray()
            },
            ["registration_data"] = registrationData,
            ["auth_data"] = authData,
            ["requested_extensions"] = new JArray("device_info", "customer_info")
        };
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
        [JsonPropertyName("expires_in")] public JsonElement ExpiresIn { get; init; }
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

    private static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool TryReadExpiresIn(JsonElement value, out long seconds)
    {
        if (value.ValueKind == JsonValueKind.Number)
            return value.TryGetInt64(out seconds) && seconds > 0;
        if (value.ValueKind == JsonValueKind.String)
            return long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out seconds)
                && seconds > 0;
        seconds = 0;
        return false;
    }
}