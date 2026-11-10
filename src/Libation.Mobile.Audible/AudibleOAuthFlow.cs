using System.Security.Cryptography;
using System.Text;

namespace Libation.Mobile.Audible;

/// <summary>
/// Builds the in-app Audible authorization request using the same PKCE-based
/// device OAuth shape as LibriSync. This class only creates the authorization
/// request; token exchange and device registration must use the matching
/// registration protocol and the verifier returned here.
/// </summary>
public static class AudibleOAuthFlow
{
    public const string AndroidDeviceType = "A10KISP2GWF0E4";

    private static readonly IReadOnlyDictionary<string, (string AmazonDomain, string MarketplaceId, string Association)> Markets =
        new Dictionary<string, (string, string, string)>(StringComparer.OrdinalIgnoreCase)
        {
            ["us"] = ("amazon.com", "AF2M0KC94RCEA", "amzn_audible_android_us"),
            ["uk"] = ("amazon.co.uk", "A2I9A3Q2GNFNGQ", "amzn_audible_android_uk"),
            ["de"] = ("amazon.de", "AN7V1F1VY261K", "amzn_audible_android_de"),
            ["fr"] = ("amazon.fr", "A2728XDNODOQ8T", "amzn_audible_android_fr"),
            ["ca"] = ("amazon.ca", "A2CQZ5RBY40XE", "amzn_audible_android_ca"),
            ["au"] = ("amazon.com.au", "AN7EY7DTAW63G", "amzn_audible_android_au"),
            ["it"] = ("amazon.it", "A2N7FU2W2BU2ZC", "amzn_audible_android_it"),
            ["es"] = ("amazon.es", "ALMIKO4SZCSAR", "amzn_audible_android_es"),
            ["in"] = ("amazon.in", "AJO3FBRUE6J4S", "amzn_audible_android_in"),
            ["jp"] = ("amazon.co.jp", "A1QAP3MOU4173J", "amzn_audible_android_jp"),
            ["br"] = ("amazon.com.br", "A10J1VAYUDTYRN", "amzn_audible_android_br")
        };

    public static AudibleOAuthChallenge Create(string marketplace)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(marketplace);
        var code = marketplace.Trim().ToLowerInvariant();
        if (!Markets.TryGetValue(code, out var market))
            throw new ArgumentException("The selected Audible marketplace is not supported.", nameof(marketplace));

        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var serial = new string(RandomNumberGenerator.GetBytes(40)
            .Select(b => "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789"[b % 62])
            .ToArray());
        var serialAndType = $"{serial}#{AndroidDeviceType}";
        var clientId = "device:" + Convert.ToHexString(Encoding.UTF8.GetBytes(serialAndType)).ToLowerInvariant();
        var callback = $"https://www.{market.AmazonDomain}/ap/maplanding";

        var query = new List<KeyValuePair<string, string>>
        {
            new("openid.oa2.response_type", "code"),
            new("openid.oa2.code_challenge_method", "S256"),
            new("openid.oa2.code_challenge", challenge),
            new("openid.return_to", callback),
            new("openid.assoc_handle", market.Association),
            new("openid.identity", "http://specs.openid.net/auth/2.0/identifier_select"),
            new("pageId", "amzn_audible_android"),
            new("accountStatusPolicy", "P1"),
            new("openid.claimed_id", "http://specs.openid.net/auth/2.0/identifier_select"),
            new("openid.mode", "checkid_setup"),
            new("openid.ns.oa2", "http://www.amazon.com/ap/ext/oauth/2"),
            new("openid.oa2.client_id", clientId),
            new("openid.ns.pape", "http://specs.openid.net/extensions/pape/1.0"),
            new("marketPlaceId", market.MarketplaceId),
            new("openid.oa2.scope", "device_auth_access"),
            new("forceMobileLayout", "true"),
            new("openid.ns", "http://specs.openid.net/auth/2.0"),
            new("openid.pape.max_auth_age", "0")
        };
        var queryString = string.Join("&", query.Select(p =>
            Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value)));
        var url = new Uri($"https://www.{market.AmazonDomain}/ap/signin?{queryString}");

        return new AudibleOAuthChallenge(
            code,
            url,
            new Uri(callback),
            serial,
            verifier,
            challenge);
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
