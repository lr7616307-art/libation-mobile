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
/// Exchanges an Android-profile PKCE authorization code for a complete Audible
/// device identity. The registration payload follows AudibleApi's CurrentAndroid
/// profile so the saved identity retains the Android device type required by Widevine.
/// </summary>
public sealed class AudiblePkceRegistrationClient(HttpClient httpClient)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private const string DeviceType = AudibleOAuthFlow.AndroidDeviceType;
    private const string AppVersion = "2090263407";
    private const string OsVersion = @"google\/raven\/raven:14\/UPB5.230623.003\/10615560:user\/release-keys";
    private const string SoftwareVersion = "130050002";
    private const string DeviceModel = "sdk_gphone64_x86_64";
    private const string AppName = "com.audible.application";
    private const string DeviceName =
        "%FIRST_NAME%%FIRST_NAME_POSSESSIVE_STRING%%DUPE_STRATEGY_1ST%Audible for Android";

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
        var expectedCallbackHost = locale.AudibleLoginUri().Host;
        var cookieDomain = locale.LoginUri().Host;
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
            throw new InvalidOperationException("The Audible OAuth challenge does not match the selected marketplace's Android sign-in and callback hosts.");

        if (challenge.DeviceSerial.Length != 20
            || challenge.DeviceSerial.Any(character => !(character is >= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new InvalidOperationException("The Audible Android-profile serial must be 20 lowercase hexadecimal characters.");
        if (string.IsNullOrWhiteSpace(challenge.FrcValue))
            throw new InvalidOperationException("The Audible Android device-registration context is missing.");

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
            || !string.Equals(authorizationParameters["pageId"],
                locale.WithUsername
                    ? "amzn_audible_android_privatepool_aui_v2_dark_" + locale.CountryCode
                    : "amzn_audible_android_aui_v2_dark_" + locale.CountryCode,
                StringComparison.Ordinal))
            throw new InvalidOperationException("The Audible authorization URL does not match the Android device registration or PKCE challenge.");

        var body = CreateAndroidRegistrationBody(
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
            throw new InvalidDataException("Audible did not confirm the Android device type required for Widevine.");
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
            DeviceName = device.DeviceName ?? "Audible for Android",
            StoreAuthenticationCookie = tokens.StoreAuthenticationCookie?.Cookie ?? string.Empty
        };

        // Validate the complete identity envelope before any caller persists it.
        _ = AudibleIdentitySnapshotSerializer.Serialize(snapshot);
        return snapshot;
    }

    private static JObject CreateAndroidRegistrationBody(
        AudibleOAuthChallenge challenge,
        string authorizationCode,
        string clientIdHex,
        string cookieDomain)
    {
        var registrationData = new JObject
        {
            ["domain"] = "DeviceLegacy",
            ["device_type"] = DeviceType,
            ["device_serial"] = challenge.DeviceSerial,
            ["app_name"] = AppName,
            ["app_version"] = AppVersion,
            ["device_model"] = DeviceModel,
            ["os_version"] = OsVersion,
            ["software_version"] = SoftwareVersion,
            ["device_name"] = DeviceName
        };
        var authData = new JObject
        {
            ["authorization_code"] = authorizationCode,
            ["code_verifier"] = challenge.CodeVerifier,
            ["code_algorithm"] = "SHA-256",
            ["client_domain"] = "DeviceLegacy",
            ["client_id"] = clientIdHex,
            ["use_global_authentication"] = "true"
        };

        var root = new JObject
        {
            ["requested_token_type"] = new JArray("bearer", "mac_dms", "store_authentication_cookie", "website_cookies"),
            ["cookies"] = new JObject
            {
                ["domain"] = cookieDomain,
                ["website_cookies"] = new JArray()
            },
            ["user_context_map"] = new JObject { ["frc"] = challenge.FrcValue },
            ["registration_data"] = registrationData,
            ["auth_data"] = authData,
            ["requested_extensions"] = new JArray("device_info", "customer_info"),
            ["device_metadata"] = new JObject
            {
                ["device_os_family"] = "android",
                ["device_type"] = DeviceType,
                ["device_serial"] = challenge.DeviceSerial,
                ["manufacturer"] = "Google",
                ["model"] = DeviceModel,
                ["os_version"] = "34",
                ["product"] = DeviceModel
            }
        };
        return root;
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