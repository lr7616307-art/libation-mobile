using AudibleApi;
using Libation.Mobile.Core;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Libation.Mobile.Audible;

/// <summary>
/// Creates AudibleApi's CurrentAndroid registration challenge and its required
/// Android sign-in cookies. Login and callback hosts intentionally differ for
/// most marketplaces: the sign-in host follows Locale.LoginUri while the return
/// host follows Locale.AudibleLoginUri.
/// </summary>
public static class AudibleOAuthFlow
{
    public const string IPhoneDeviceType = "A2CZJZGLK2JJVM";
    public const string RegistrationProfileId = "audible-iphone-4.60-ios-26.2-46000856";
    public const string LegacyAndroidRegistrationProfileId = "audible-android-current-v26.34.07";

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

    // LibriSync's currently working ADRM/AAC-LC iPhone registration profile.
    private const string AppName = "Audible";
    private const string AppVersion = "4.60";
    private const string OsVersion = "26.2";
    private const string SoftwareVersion = "46000856";
    private const string DeviceModel = "iPhone";
    private const string DeviceName =
        "%FIRST_NAME%%FIRST_NAME_POSSESSIVE_STRING%%DUPE_STRATEGY_1ST%Audible for iPhone";

    public static AudibleOAuthChallenge Create(string marketplace)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(marketplace);
        var code = marketplace.Trim().ToLowerInvariant();
        if (!Markets.TryGetValue(code, out _))
            throw new ArgumentException("The selected Audible marketplace is not supported.", nameof(marketplace));

        var locale = Localization.Get(code);
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var codeChallenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var serial = GenerateIPhoneDeviceSerial();
        var serialAndType = serial + "#" + IPhoneDeviceType;
        var clientIdHex = Convert.ToHexString(Encoding.UTF8.GetBytes(serialAndType)).ToLowerInvariant();
        var callback = new Uri(locale.LoginUri(), "/ap/maplanding");

        // LibriSync's current ADRM-capable profile uses the iOS sign-in surface.
        // It does not install Android frc/map-md cookies or send Android device_metadata.
        var query = new List<KeyValuePair<string, string>>
        {
            new("openid.oa2.response_type", "code"),
            new("openid.oa2.code_challenge_method", "S256"),
            new("openid.oa2.code_challenge", codeChallenge),
            new("openid.return_to", callback.AbsoluteUri),
            new("openid.assoc_handle", $"amzn_audible_ios_{locale.CountryCode}"),
            new("openid.identity", "http://specs.openid.net/auth/2.0/identifier_select"),
            new("pageId", "amzn_audible_ios"),
            new("accountStatusPolicy", "P1"),
            new("openid.claimed_id", "http://specs.openid.net/auth/2.0/identifier_select"),
            new("openid.mode", "checkid_setup"),
            new("openid.ns.oa2", "http://www.amazon.com/ap/ext/oauth/2"),
            new("openid.oa2.client_id", "device:" + clientIdHex),
            new("openid.ns.pape", "http://specs.openid.net/extensions/pape/1.0"),
            new("marketPlaceId", locale.MarketPlaceId),
            new("openid.oa2.scope", "device_auth_access"),
            new("forceMobileLayout", "true"),
            new("openid.ns", "http://specs.openid.net/auth/2.0"),
            new("openid.pape.max_auth_age", "0")
        };

        var queryString = string.Join("&", query.Select(pair =>
            Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value)));
        var authorizationUri = new Uri(locale.LoginUri(), "/ap/signin?" + queryString);

        return new AudibleOAuthChallenge(
            code,
            authorizationUri,
            callback,
            serial,
            verifier,
            codeChallenge,
            string.Empty,
            Array.Empty<ExternalSignInCookie>());
    }

    private static string GenerateIPhoneDeviceSerial()
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
        return new string(Enumerable.Range(0, 40)
            .Select(_ => alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)])
            .ToArray());
    }

    public static bool IsMarketplaceCallback(string marketplace, Uri callbackUri)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(marketplace);
        ArgumentNullException.ThrowIfNull(callbackUri);
        if (!Markets.ContainsKey(marketplace.Trim()))
            return false;

        var expected = new Uri(Localization.Get(marketplace.Trim()).LoginUri(), "/ap/maplanding");
        return string.Equals(callbackUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && string.Equals(callbackUri.Host, expected.Host, StringComparison.OrdinalIgnoreCase)
            && string.Equals(callbackUri.AbsolutePath.TrimEnd('/'), expected.AbsolutePath, StringComparison.OrdinalIgnoreCase)
            && callbackUri.IsDefaultPort
            && string.IsNullOrEmpty(callbackUri.UserInfo);
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
    string FrcValue,
    IReadOnlyList<ExternalSignInCookie> SignInCookies);
