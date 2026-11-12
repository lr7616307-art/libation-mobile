using System.Security.Cryptography;
using System.Text;

namespace Libation.Mobile.Audible;

/// <summary>
/// Builds the Audible PKCE authorization request using the current LibriSync
/// iPhone registration profile. The profile is a provider registration choice;
/// it does not claim that the physical Android handset is an iPhone.
/// </summary>
public static class AudibleOAuthFlow
{
    public const string IPhoneDeviceType = "A2CZJZGLK2JJVM";
    public const string RegistrationProfileId = "audible-iphone-4.60-ios-26.2-46000856";

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

    public static AudibleOAuthChallenge Create(string marketplace)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(marketplace);
        var code = marketplace.Trim().ToLowerInvariant();
        if (!Markets.TryGetValue(code, out var market))
            throw new ArgumentException("The selected Audible marketplace is not supported.", nameof(marketplace));

        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var codeChallenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var serial = GenerateDeviceSerial();
        var serialAndType = $"{serial}#{IPhoneDeviceType}";
        var clientIdHex = Convert.ToHexString(Encoding.UTF8.GetBytes(serialAndType)).ToLowerInvariant();
        var callback = new Uri($"https://www.{market.AmazonDomain}/ap/maplanding");

        // Keep this parameter set aligned with LibriSync's current shipped iPhone
        // profile. Do not mix these values with the separate Android device profile.
        var query = new List<KeyValuePair<string, string>>
        {
            new("openid.oa2.response_type", "code"),
            new("openid.oa2.code_challenge_method", "S256"),
            new("openid.oa2.code_challenge", codeChallenge),
            new("openid.return_to", callback.AbsoluteUri),
            new("openid.assoc_handle", $"amzn_audible_ios_{market.CountryCode}"),
            new("openid.identity", "http://specs.openid.net/auth/2.0/identifier_select"),
            new("pageId", "amzn_audible_ios"),
            new("accountStatusPolicy", "P1"),
            new("openid.claimed_id", "http://specs.openid.net/auth/2.0/identifier_select"),
            new("openid.mode", "checkid_setup"),
            new("openid.ns.oa2", "http://www.amazon.com/ap/ext/oauth/2"),
            new("openid.oa2.client_id", "device:" + clientIdHex),
            new("openid.ns.pape", "http://specs.openid.net/extensions/pape/1.0"),
            new("marketPlaceId", market.MarketplaceId),
            new("openid.oa2.scope", "device_auth_access"),
            new("forceMobileLayout", "true"),
            new("openid.ns", "http://specs.openid.net/auth/2.0"),
            new("openid.pape.max_auth_age", "0")
        };

        var queryString = string.Join("&", query.Select(pair =>
            Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value)));
        var authorizationUri = new Uri($"https://www.{market.AmazonDomain}/ap/signin?{queryString}");

        return new AudibleOAuthChallenge(
            code,
            authorizationUri,
            callback,
            serial,
            verifier,
            codeChallenge);
    }

    public static bool IsMarketplaceCallback(string marketplace, Uri callbackUri)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(marketplace);
        ArgumentNullException.ThrowIfNull(callbackUri);
        return Markets.TryGetValue(marketplace.Trim(), out var market)
            && string.Equals(callbackUri.Host, "www." + market.AmazonDomain, StringComparison.OrdinalIgnoreCase)
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
            throw new InvalidOperationException($"Amazon sign-in failed: {error} — {description}");
        }

        var code = parameters["openid.oa2.authorization_code"];
        if (string.IsNullOrWhiteSpace(code)) code = parameters["code"];
        if (string.IsNullOrWhiteSpace(code))
            throw new InvalidOperationException("Amazon returned the sign-in page without an authorization code.");
        return code;
    }

    private static string GenerateDeviceSerial()
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
        return string.Concat(Enumerable.Range(0, 40)
            .Select(_ => alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)]));
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
    string CodeChallenge);
