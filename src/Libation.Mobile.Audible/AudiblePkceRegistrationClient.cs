using AudibleApi;
using System.Security.Cryptography;
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Libation.Mobile.Audible;

/// <summary>
/// Exchanges the PKCE authorization code for the complete Audible device identity.
/// The request follows LibriSync's current iPhone registration profile, including
/// its current serial length and app/OS/software version tuple.
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
    private const string RegistrationDeviceName =
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
        if (!challenge.AuthorizationUri.IsAbsoluteUri
            || !challenge.CallbackUri.IsAbsoluteUri
            || !string.Equals(challenge.AuthorizationUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(challenge.CallbackUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !challenge.AuthorizationUri.IsDefaultPort
            || !challenge.CallbackUri.IsDefaultPort
            || !string.IsNullOrEmpty(challenge.AuthorizationUri.UserInfo)
            || !string.IsNullOrEmpty(challenge.CallbackUri.UserInfo)
            || !string.Equals(challenge.AuthorizationUri.AbsolutePath, "/ap/signin", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The OAuth challenge must use the expected absolute HTTPS Amazon sign-in and callback URLs.");

        var amazonDomain = challenge.AuthorizationUri.Host;
        if (amazonDomain.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
            amazonDomain = amazonDomain[4..];
        if (!amazonDomain.StartsWith("amazon.", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(challenge.AuthorizationUri.Host, "www." + amazonDomain, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(challenge.AuthorizationUri.Host, challenge.CallbackUri.Host, StringComparison.OrdinalIgnoreCase)
            || !AudibleOAuthFlow.IsMarketplaceCallback(challenge.Marketplace, challenge.CallbackUri))
            throw new InvalidOperationException("The OAuth challenge does not match the selected Amazon marketplace.");

        if (challenge.DeviceSerial.Length != 40
            || challenge.DeviceSerial.Any(character => !char.IsAsciiLetterOrDigit(character)))
            throw new InvalidOperationException("The Audible iPhone-profile serial must be 40 alphanumeric characters.");

        if (challenge.CodeVerifier.Length != 43
            || challenge.CodeVerifier.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_')))
            throw new InvalidOperationException("The Audible PKCE verifier is malformed.");

        var expectedCodeChallenge = Base64Url(SHA256.HashData(
            System.Text.Encoding.ASCII.GetBytes(challenge.CodeVerifier)));
        if (!string.Equals(challenge.CodeChallenge, expectedCodeChallenge, StringComparison.Ordinal))
            throw new InvalidOperationException("The Audible PKCE verifier does not match its challenge.");

        var serialAndType = $"{challenge.DeviceSerial}#{DeviceType}";
        var clientIdHex = Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(serialAndType)).ToLowerInvariant();

        var authorizationParameters = System.Web.HttpUtility.ParseQueryString(challenge.AuthorizationUri.Query);
        if (!string.Equals(authorizationParameters["openid.oa2.response_type"], "code", StringComparison.Ordinal)
            || !string.Equals(authorizationParameters["openid.oa2.code_challenge_method"], "S256", StringComparison.Ordinal)
            || !string.Equals(authorizationParameters["openid.oa2.code_challenge"], expectedCodeChallenge, StringComparison.Ordinal)
            || !string.Equals(authorizationParameters["openid.oa2.client_id"], "device:" + clientIdHex, StringComparison.Ordinal)
            || !string.Equals(authorizationParameters["openid.return_to"], challenge.CallbackUri.AbsoluteUri, StringComparison.Ordinal))
            throw new InvalidOperationException("The Audible authorization URL does not match the PKCE verifier, device registration or marketplace callback.");

        var request = new RegistrationRequest
        {
            RequestedTokenType = ["bearer", "mac_dms", "website_cookies", "store_authentication_cookie"],
            Cookies = new RegistrationCookies
            {
                WebsiteCookies = [],
                Domain = "." + amazonDomain
            },
            RegistrationData = new RegistrationData
            {
                Domain = "Device",
                AppVersion = AppVersion,
                DeviceSerial = challenge.DeviceSerial,
                DeviceType = DeviceType,
                DeviceName = RegistrationDeviceName,
                OsVersion = OsVersion,
                SoftwareVersion = SoftwareVersion,
                DeviceModel = "iPhone",
                AppName = "Audible"
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

        using var message = new HttpRequestMessage(
            HttpMethod.Post,
            "https://api." + amazonDomain + "/auth/register")
        {
            Content = JsonContent.Create(request, options: JsonOptions)
        };
        message.Headers.TryAddWithoutValidation("Accept", "application/json");

        using var response = await httpClient.SendAsync(message, cancellationToken).ConfigureAwait(false);
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

        if (!TryReadExpiresIn(bearer.ExpiresIn, out var expiresIn))
            throw new InvalidDataException("Audible returned an invalid access-token lifetime.");

        var cookies = tokens.WebsiteCookies ?? [];
        var snapshot = new AudibleIdentitySnapshot
        {
            SchemaVersion = AudibleIdentitySnapshot.CurrentSchemaVersion,
            Provider = AudibleIdentitySnapshot.ProviderName,
            AudibleApiVersion = AudibleIdentitySnapshot.SupportedAudibleApiVersion,
            RegistrationProfileId = AudibleOAuthFlow.RegistrationProfileId,
            LocaleName = Localization.Get(challenge.Marketplace).Name,
            AccessToken = bearer.AccessToken ?? string.Empty,
            AccessTokenExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(expiresIn),
            RefreshToken = bearer.RefreshToken ?? string.Empty,
            PrivateKey = macDms.DevicePrivateKey ?? string.Empty,
            AdpToken = macDms.AdpToken ?? string.Empty,
            Cookies = cookies.Select(cookie => new AudibleCookieSnapshot
            {
                Name = cookie.Name ?? string.Empty,
                Value = cookie.Value ?? string.Empty
            }).ToArray(),
            DeviceSerialNumber = device.DeviceSerialNumber ?? challenge.DeviceSerial,
            DeviceType = device.DeviceType ?? DeviceType,
            AmazonAccountId = customer.UserId,
            DeviceName = device.DeviceName ?? "Audible for iPhone",
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
