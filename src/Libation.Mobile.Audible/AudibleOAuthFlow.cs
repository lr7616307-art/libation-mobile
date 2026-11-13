using AudibleApi;
using Libation.Mobile.Core;
using System.IO.Compression;
using System.Net;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Libation.Mobile.Audible;

public static class AudibleOAuthFlow
{
    public const string AndroidDeviceType = "A10KISP2GWF0E4";
    public const string RegistrationProfileId = "audible-android-current-v26.34.07";
    public const string LegacyIPhoneRegistrationProfileId = "audible-iphone-4.60-ios-26.2-46000856";

    // Keep this virtual device profile byte-for-byte aligned with the supported
    // AudibleApi CurrentAndroid registration profile. Audible expects the
    // official Android app/emulator identity here, not the physical host phone.
    public const string AndroidDeviceModel = "sdk_gphone64_x86_64";
    public const string AndroidOsVersion = @"google\\/raven\\/raven:14\\/UPB5.230623.003\\/10615560:user\\/release-keys";
    public const string AndroidOsVersionNumber = "34";
    public const string AndroidManufacturer = "Google";
    public const string AndroidDeviceProduct = "sdk_gphone64_x86_64";
    public const string AndroidFrcDeviceName = @"ranchu\\/Google\\/sdk_gphone64_x86_64";
    public const string AndroidUserAgent = "Mozilla/5.0 (Linux; Android 14; sdk_gphone64_x86_64 Build/UPB5.230623.003; wv) AppleWebKit/537.36 (KHTML, like Gecko) Version/4.0 Chrome/113.0.5672.136 Mobile Safari/537.36";
    private const string AndroidDeviceNameTemplate = "%FIRST_NAME%%FIRST_NAME_POSSESSIVE_STRING%%DUPE_STRATEGY_1ST%Audible for Android";

    private sealed record Market(string AmazonDomain, string MarketplaceId, string CountryCode);
    private static readonly IReadOnlyDictionary<string, Market> Markets = new Dictionary<string, Market>(StringComparer.OrdinalIgnoreCase)
    {
        ["us"] = new("amazon.com", "AF2M0KC94RCEA", "us"), ["uk"] = new("amazon.co.uk", "A2I9A3Q2GNFNGQ", "uk"),
        ["de"] = new("amazon.de", "AN7V1F1VY261K", "de"), ["fr"] = new("amazon.fr", "A2728XDNODOQ8T", "fr"),
        ["ca"] = new("amazon.ca", "A2CQZ5RBY40XE", "ca"), ["au"] = new("amazon.com.au", "AN7EY7DTAW63G", "au"),
        ["it"] = new("amazon.it", "A2N7FU2W2BU2ZC", "it"), ["es"] = new("amazon.es", "ALMIKO4SZCSAR", "es"),
        ["in"] = new("amazon.in", "AJO3FBRUE6J4S", "in"), ["jp"] = new("amazon.co.jp", "A1QAP3MOU4173J", "jp"),
        ["br"] = new("amazon.com.br", "A10J1VAYUDTYRN", "br")
    };

    private const string AppName = "com.audible.application";
    private const string AppVersion = "2090263407";
    private const string AppVersionName = "26.34.07";
    private const string SoftwareVersion = "130050002";
    private const string MapVersion = "MAPAndroidLib-1.3.57073.0";

    public static AudibleOAuthChallenge Create(string marketplace, AudibleAndroidDeviceInfo deviceInfo, string? stableDeviceSerial = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(marketplace);
        ArgumentNullException.ThrowIfNull(deviceInfo);
        deviceInfo.Validate();
        var code = marketplace.Trim().ToLowerInvariant();
        if (!Markets.ContainsKey(code))
            throw new ArgumentException("The selected Audible marketplace is not supported.", nameof(marketplace));

        var serial = stableDeviceSerial ?? Convert.ToHexString(RandomNumberGenerator.GetBytes(10)).ToLowerInvariant();
        if (serial.Length != 20 || serial.Any(character => !(character is >= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new ArgumentException("The stable Android device serial must be 20 lowercase hexadecimal characters.", nameof(stableDeviceSerial));

        var locale = Localization.Get(code);
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var codeChallenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var serialAndType = serial + "#" + AndroidDeviceType;
        var clientIdHex = Convert.ToHexString(Encoding.UTF8.GetBytes(serialAndType)).ToLowerInvariant();
        var callback = new Uri(locale.AudibleLoginUri(), "/ap/maplanding");
        var frc = CreateAndroidFrc(serial, locale);
        var loginHost = locale.LoginUri().Host;
        var cookieDomain = "." + (loginHost.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? loginHost[4..] : loginHost);
        var cookies = new[]
        {
            new ExternalSignInCookie("frc", frc, cookieDomain, "/ap"),
            new ExternalSignInCookie("map-md", CreateAndroidMapMd(), cookieDomain, "/ap"),
            new ExternalSignInCookie("sid", string.Empty, cookieDomain, "/")
        };
        var association = locale.WithUsername ? $"amzn_audible_android_aui_lap_{locale.CountryCode}" : $"amzn_audible_android_aui_{locale.CountryCode}";
        var pageId = locale.WithUsername ? $"amzn_audible_android_privatepool_aui_v2_dark_{locale.CountryCode}" : $"amzn_audible_android_aui_v2_dark_{locale.CountryCode}";
        var query = new List<KeyValuePair<string, string>>
        {
            new("openid.pape.max_auth_age", "0"), new("openid.identity", "http://specs.openid.net/auth/2.0/identifier_select"),
            new("accountStatusPolicy", "P1"), new("marketPlaceId", locale.MarketPlaceId), new("pageId", pageId),
            new("openid.return_to", callback.AbsoluteUri), new("openid.assoc_handle", association), new("openid.oa2.response_type", "code"),
            new("openid.mode", "checkid_setup"), new("openid.ns.pape", "http://specs.openid.net/extensions/pape/1.0"),
            new("openid.oa2.code_challenge_method", "S256"), new("openid.ns.oa2", "http://www.amazon.com/ap/ext/oauth/2"),
            new("openid.oa2.code_challenge", codeChallenge), new("openid.oa2.scope", "device_auth_access"),
            new("openid.claimed_id", "http://specs.openid.net/auth/2.0/identifier_select"),
            new("openid.oa2.client_id", "device:" + clientIdHex), new("openid.ns", "http://specs.openid.net/auth/2.0"),
            new("disableLoginPrepopulate", "1")
        };
        var queryString = string.Join("&", query.Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value)));
        var authorizationUri = new Uri(locale.LoginUri(), "/ap/signin?" + queryString);
        return new AudibleOAuthChallenge(code, authorizationUri, callback, serial, verifier, codeChallenge, frc, cookies, deviceInfo);
    }

    public static bool IsMarketplaceCallback(string marketplace, Uri callbackUri)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(marketplace);
        ArgumentNullException.ThrowIfNull(callbackUri);
        if (!Markets.ContainsKey(marketplace.Trim())) return false;
        var expected = new Uri(Localization.Get(marketplace.Trim()).AudibleLoginUri(), "/ap/maplanding");
        return string.Equals(callbackUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && string.Equals(callbackUri.Host, expected.Host, StringComparison.OrdinalIgnoreCase)
            && string.Equals(callbackUri.AbsolutePath.TrimEnd('/'), expected.AbsolutePath, StringComparison.OrdinalIgnoreCase)
            && callbackUri.IsDefaultPort && string.IsNullOrEmpty(callbackUri.UserInfo);
    }

    public static bool HasAuthorizationResponse(Uri responseUri)
    {
        ArgumentNullException.ThrowIfNull(responseUri);
        var parameters = System.Web.HttpUtility.ParseQueryString(responseUri.Query);
        return !string.IsNullOrWhiteSpace(parameters["openid.oa2.authorization_code"]) || !string.IsNullOrWhiteSpace(parameters["code"]) || !string.IsNullOrWhiteSpace(parameters["error"]);
    }

    public static string ReadAuthorizationCode(Uri responseUri)
    {
        ArgumentNullException.ThrowIfNull(responseUri);
        var parameters = System.Web.HttpUtility.ParseQueryString(responseUri.Query);
        var error = parameters["error"];
        if (!string.IsNullOrWhiteSpace(error))
        {
            var description = parameters["error_description"];
            if (string.IsNullOrWhiteSpace(description)) description = "Amazon rejected the sign-in request.";
            description = description.Length > 200 ? description[..200] : description;
            throw new InvalidOperationException("Amazon sign-in failed: " + error + " — " + description);
        }
        var code = parameters["openid.oa2.authorization_code"];
        if (string.IsNullOrWhiteSpace(code)) code = parameters["code"];
        if (string.IsNullOrWhiteSpace(code)) throw new InvalidOperationException("Amazon returned the sign-in page without an authorization code.");
        return code;
    }

    private static string CreateAndroidMapMd()
    {
        var mapMd = new JObject
        {
            ["device_registration_data"] = new JObject { ["software_version"] = SoftwareVersion },
            ["app_identifier"] = new JObject { ["package"] = AppName, ["SHA-256"] = JValue.CreateNull(), ["app_version"] = AppVersion,
                ["app_version_name"] = AppVersionName, ["app_sms_hash"] = JValue.CreateNull(), ["map_version"] = MapVersion },
            ["app_info"] = new JObject { ["auto_pv"] = 0, ["auto_pv_with_smsretriever"] = 1, ["smartlock_supported"] = 0, ["permission_runtime_grant"] = 2 }
        };
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(mapMd.ToString(Formatting.None)));
    }

    private static string CreateAndroidFrc(string serial, Locale locale)
    {
        // This matches AudibleApi's CurrentAndroid profile. Do not substitute
        // Redmi/Xiaomi hardware fields here: registration is for Audible's
        // supported Android app profile, not a physical-device attestation.
        var registrationContext = new JObject
        {
            ["ApplicationName"] = AppName,
            ["ApplicationVersion"] = AppVersion,
            ["DeviceOSVersion"] = AndroidOsVersion,
            ["DeviceName"] = AndroidFrcDeviceName,
            ["ScreenWidthPixels"] = "1080",
            ["ThirdPartyDeviceId"] = serial,
            ["FirstPartyDeviceId"] = serial,
            ["ScreenHeightPixels"] = "2400",
            ["DeviceLanguage"] = locale.Language,
            ["TimeZone"] = FormatOffset(DateTimeOffset.Now.Offset),
            ["Carrier"] = "T-Mobile",
            ["IpAddress"] = GetLocalIpAddress()
        };
        return EncodeFrc(serial, registrationContext.ToString(Formatting.None));
    }

    private static string GetLocalIpAddress()
    {
        try
        {
            var addresses = NetworkInterface.GetAllNetworkInterfaces().SelectMany(network => network.GetIPProperties().UnicastAddresses)
                .Select(entry => entry.Address).Where(address => !IPAddress.IsLoopback(address) && !address.Equals(IPAddress.IPv6Any))
                .OrderBy(address => address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6)
                .OrderByDescending(address => !address.IsIPv6LinkLocal && !address.IsIPv6SiteLocal).FirstOrDefault();
            return addresses?.ToString() ?? "0.0.0.0";
        }
        catch { return "0.0.0.0"; }
    }

    private static string EncodeFrc(string serial, string json)
    {
        byte[] compressed;
        using (var output = new MemoryStream())
        {
            using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
            {
                var jsonBytes = Encoding.UTF8.GetBytes(json);
                gzip.Write(jsonBytes, 0, jsonBytes.Length);
            }
            compressed = output.ToArray();
        }
        var iv = RandomNumberGenerator.GetBytes(16);
        var aesKey = Rfc2898DeriveBytes.Pbkdf2(serial.AsSpan(), "AES/CBC/PKCS7Padding"u8, 1000, HashAlgorithmName.SHA1, 16);
        byte[] encrypted;
        using (var aes = Aes.Create()) { aes.Key = aesKey; encrypted = aes.EncryptCbc(compressed, iv, PaddingMode.PKCS7); }
        var hmacKey = Rfc2898DeriveBytes.Pbkdf2(serial.AsSpan(), "HmacSHA256"u8, 1000, HashAlgorithmName.SHA1, 16);
        byte[] signature;
        using (var hmac = new HMACSHA256(hmacKey)) signature = hmac.ComputeHash(iv.Concat(encrypted).ToArray())[..8];
        var result = new byte[1 + signature.Length + iv.Length + encrypted.Length];
        signature.CopyTo(result, 1); iv.CopyTo(result, 1 + signature.Length); encrypted.CopyTo(result, 1 + signature.Length + iv.Length);
        CryptographicOperations.ZeroMemory(aesKey); CryptographicOperations.ZeroMemory(hmacKey); CryptographicOperations.ZeroMemory(compressed);
        return Convert.ToBase64String(result);
    }

    private static string FormatOffset(TimeSpan offset) => (offset.Ticks < 0 ? "-" : "") + offset.Duration().ToString(@"hh\:mm", System.Globalization.CultureInfo.InvariantCulture);
    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

public sealed record AudibleOAuthChallenge(string Marketplace, Uri AuthorizationUri, Uri CallbackUri, string DeviceSerial,
    string CodeVerifier, string CodeChallenge, string FrcValue, IReadOnlyList<ExternalSignInCookie> SignInCookies, AudibleAndroidDeviceInfo DeviceInfo);
