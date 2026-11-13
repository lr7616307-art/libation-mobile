using System.IO.Compression;
using System.Net;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Libation.Mobile.Audible;

/// <summary>
/// Creates the current Audible Android registration challenge. This profile is
/// Audible's supported Android emulator recipe (CurrentAndroid), which retains
/// the Android Audible device type required for Widevine delivery.
/// </summary>
public static class AudibleOAuthFlow
{
    public const string AndroidDeviceType = "A10KISP2GWF0E4";
    public const string RegistrationProfileId = "audible-android-current-v26.34.07";
    public const string LegacyIPhoneRegistrationProfileId = "audible-iphone-4.60-ios-26.2-46000856";

    private sealed record Market(string AmazonDomain, string MarketplaceId, string CountryCode);

    private static readonly IReadOnlyDictionary<string, Market> Markets =
        new Dictionary<string, Market>(StringComparer.OrdinalIgnoreCase)
        {
            ["us"] = new("amazon.com", "AF2M0KC94RCEA", "us"),
            ["uk"] = new("amazon.co.uk", "A2I9A3Q2GNFNGQ", "uk"),
            ["de"] = new("amazon.de", "AN7V1F1VY261K", "de"),
            ["fr"] = new("amazon.fr", "A2728XDNODOQ8T", "fr"),
            ["ca"] = new("amazon.ca", "A2CQZ5RBY40XE", "ca"),
            ["au"] = new("amazon.com.au", "AN7EY7DTAW63G", "au"),
            ["it"] = new("amazon.it", "A2N7FU2W2BU2ZC", "it"),
            ["es"] = new("amazon.es", "ALMIKO4SZCSAR", "es"),
            ["in"] = new("amazon.in", "AJO3FBRUE6J4S", "in"),
            ["jp"] = new("amazon.co.jp", "A1QAP3MOU4173J", "jp"),
            ["br"] = new("amazon.com.br", "A10J1VAYUDTYRN", "br")
        };

    // Values below match AudibleApi's CurrentAndroid registration profile.
    private const string AppName = "com.audible.application";
    private const string AppVersion = "2090263407";
    private const string AppVersionName = "26.34.07";
    private const string SoftwareVersion = "130050002";
    private const string DeviceModel = "sdk_gphone64_x86_64";
    private const string OsVersion = @"google\/raven\/raven:14\/UPB5.230623.003\/10615560:user\/release-keys";
    private const string DeviceName = @"ranchu\/Google\/" + DeviceModel;
    private const string MapVersion = "MAPAndroidLib-1.3.57073.0";

    public static AudibleOAuthChallenge Create(string marketplace)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(marketplace);
        var code = marketplace.Trim().ToLowerInvariant();
        if (!Markets.TryGetValue(code, out var market))
            throw new ArgumentException("The selected Audible marketplace is not supported.", nameof(marketplace));

        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var codeChallenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var serial = Convert.ToHexString(RandomNumberGenerator.GetBytes(10)).ToLowerInvariant();
        var serialAndType = serial + "#" + AndroidDeviceType;
        var clientIdHex = Convert.ToHexString(Encoding.UTF8.GetBytes(serialAndType)).ToLowerInvariant();
        var callback = new Uri("https://www.audible." + GetTopDomain(market.AmazonDomain) + "/ap/maplanding");
        var frc = CreateAndroidFrc(serial, code);

        var query = new List<KeyValuePair<string, string>>
        {
            new("openid.oa2.response_type", "code"),
            new("openid.oa2.code_challenge_method", "S256"),
            new("openid.oa2.code_challenge", codeChallenge),
            new("openid.return_to", callback.AbsoluteUri),
            new("openid.assoc_handle", "amzn_audible_android_aui_" + market.CountryCode),
            new("openid.identity", "http://specs.openid.net/auth/2.0/identifier_select"),
            new("pageId", "amzn_audible_android_aui_v2_dark_" + market.CountryCode),
            new("accountStatusPolicy", "P1"),
            new("openid.claimed_id", "http://specs.openid.net/auth/2.0/identifier_select"),
            new("openid.mode", "checkid_setup"),
            new("openid.ns.oa2", "http://www.amazon.com/ap/ext/oauth/2"),
            new("openid.oa2.client_id", "device:" + clientIdHex),
            new("openid.ns.pape", "http://specs.openid.net/extensions/pape/1.0"),
            new("marketPlaceId", market.MarketplaceId),
            new("openid.oa2.scope", "device_auth_access"),
            new("disableLoginPrepopulate", "1"),
            new("openid.ns", "http://specs.openid.net/auth/2.0"),
            new("openid.pape.max_auth_age", "0")
        };

        var queryString = string.Join("&", query.Select(pair =>
            Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value)));
        var authorizationUri = new Uri("https://www.audible." + GetTopDomain(market.AmazonDomain)
            + "/ap/signin?" + queryString);

        return new AudibleOAuthChallenge(
            code,
            authorizationUri,
            callback,
            serial,
            verifier,
            codeChallenge,
            frc);
    }

    public static bool IsMarketplaceCallback(string marketplace, Uri callbackUri)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(marketplace);
        ArgumentNullException.ThrowIfNull(callbackUri);
        return Markets.TryGetValue(marketplace.Trim(), out var market)
            && string.Equals(callbackUri.Host,
                "www.audible." + GetTopDomain(market.AmazonDomain),
                StringComparison.OrdinalIgnoreCase)
            && string.Equals(callbackUri.AbsolutePath.TrimEnd('/'), "/ap/maplanding", StringComparison.OrdinalIgnoreCase);
    }

    public static bool HasAuthorizationResponse(Uri responseUri)
    {
        ArgumentNullException.ThrowIfNull(responseUri);
        var parameters = System.Web.HttpUtility.ParseQueryString(responseUri.Query);
        return !string.IsNullOrWhiteSpace(parameters["openid.oa2.authorization_code"])
            || !string.IsNullOrWhiteSpace(parameters["code"])
            || !string.IsNullOrWhiteSpace(parameters["error"]);
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
        if (string.IsNullOrWhiteSpace(code))
            throw new InvalidOperationException("Amazon returned the sign-in page without an authorization code.");
        return code;
    }

    private static string CreateAndroidFrc(string serial, string marketplace)
    {
        var locale = AudibleApi.Localization.Get(marketplace);
        var deviceInfo = new JObject
        {
            ["ApplicationName"] = AppName,
            ["ApplicationVersion"] = AppVersion,
            ["DeviceOSVersion"] = OsVersion,
            ["DeviceName"] = DeviceName,
            ["ScreenWidthPixels"] = "1080",
            ["ThirdPartyDeviceId"] = serial,
            ["FirstPartyDeviceId"] = serial,
            ["ScreenHeightPixels"] = "2400",
            ["DeviceLanguage"] = locale.Language,
            ["TimeZone"] = FormatOffset(DateTimeOffset.Now.Offset),
            ["Carrier"] = "T-Mobile",
            ["IpAddress"] = GetLocalIpAddress()
        };
        return EncodeFrc(serial, deviceInfo.ToString(Formatting.None));
    }

    private static string GetLocalIpAddress()
    {
        try
        {
            var addresses = NetworkInterface.GetAllNetworkInterfaces()
                .SelectMany(network => network.GetIPProperties().UnicastAddresses)
                .Select(entry => entry.Address)
                .Where(address => !IPAddress.IsLoopback(address) && !address.Equals(IPAddress.IPv6Any))
                .OrderBy(address => address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6)
                .OrderByDescending(address => !address.IsIPv6LinkLocal && !address.IsIPv6SiteLocal)
                .FirstOrDefault();
            return addresses?.ToString() ?? "0.0.0.0";
        }
        catch
        {
            return "0.0.0.0";
        }
    }

    // Audible's Android registration profile encodes the FRC cookie as
    // gzip(JSON), AES-CBC(PKCS7), followed by an HMAC-SHA256 authenticity tag.
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
        var aesKey = Rfc2898DeriveBytes.Pbkdf2(serial.AsSpan(), "AES/CBC/PKCS7Padding"u8,
            1000, HashAlgorithmName.SHA1, 16);
        byte[] encrypted;
        using (var aes = Aes.Create())
        {
            aes.Key = aesKey;
            encrypted = aes.EncryptCbc(compressed, iv, PaddingMode.PKCS7);
        }

        var hmacKey = Rfc2898DeriveBytes.Pbkdf2(serial.AsSpan(), "HmacSHA256"u8,
            1000, HashAlgorithmName.SHA1, 16);
        byte[] signature;
        using (var hmac = new HMACSHA256(hmacKey))
            signature = hmac.ComputeHash(iv.Concat(encrypted).ToArray())[..8];

        var result = new byte[1 + signature.Length + iv.Length + encrypted.Length];
        signature.CopyTo(result, 1);
        iv.CopyTo(result, 1 + signature.Length);
        encrypted.CopyTo(result, 1 + signature.Length + iv.Length);
        CryptographicOperations.ZeroMemory(aesKey);
        CryptographicOperations.ZeroMemory(hmacKey);
        CryptographicOperations.ZeroMemory(compressed);
        return Convert.ToBase64String(result);
    }

    private static string FormatOffset(TimeSpan offset)
        => (offset.Ticks < 0 ? "-" : "") + offset.Duration().ToString(@"hh\:mm", System.Globalization.CultureInfo.InvariantCulture);

    private static string GetTopDomain(string amazonDomain)
        => amazonDomain.StartsWith("amazon.", StringComparison.OrdinalIgnoreCase)
            ? amazonDomain["amazon.".Length..]
            : throw new InvalidOperationException("Unsupported Amazon marketplace domain.");

    private static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

public sealed record AudibleOAuthChallenge(
    string Marketplace,
    Uri AuthorizationUri,
    Uri CallbackUri,
    string DeviceSerial,
    string CodeVerifier,
    string CodeChallenge,
    string FrcValue);
