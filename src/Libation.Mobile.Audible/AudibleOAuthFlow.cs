using System.Security.Cryptography;
using System.Text;

namespace Libation.Mobile.Audible;

/// <summary>
/// Builds the Android Audible PKCE authorization request. The callback is the
/// matching Amazon marketplace maplanding endpoint used by Audible OAuth.
/// </summary>
public static class AudibleOAuthFlow
{
    public const string AndroidDeviceType = "A10KISP2GWF0E4";

    private sealed record Market(
        string AmazonDomain,
        string AudibleDomain,
        string MarketplaceId,
        string CountryCode);

    private static readonly IReadOnlyDictionary<string, Market> Markets =
        new Dictionary<string, Market>(StringComparer.OrdinalIgnoreCase)
        {
            ["us"] = new("amazon.com", "audible.com", "AF2M0KC94RCEA", "us"),
            ["uk"] = new("amazon.co.uk", "audible.co.uk", "A2I9A3Q2GNFNGQ", "uk"),
            ["de"] = new("amazon.de", "audible.de", "AN7V1F1VY261K", "de"),
            ["fr"] = new("amazon.fr", "audible.fr", "A2728XDNODOQ8T", "fr"),
            ["ca"] = new("amazon.ca", "audible.ca", "A2CQZ5RBY40XE", "ca"),
            ["au"] = new("amazon.com.au", "audible.com.au", "AN7EY7DTAW63G", "au"),
            ["it"] = new("amazon.it", "audible.it", "A2N7FU2W2BU2ZC", "it"),
            ["es"] = new("amazon.es", "audible.es", "ALMIKO4SZCSAR", "es"),
            ["in"] = new("amazon.in", "audible.in", "AJO3FBRUE6J4S", "in"),
            ["jp"] = new("amazon.co.jp", "audible.co.jp", "A1QAP3MOU4173J", "jp"),
            ["br"] = new("amazon.com.br", "audible.com.br", "A10J1VAYUDTYRN", "br")
        };

    public static AudibleOAuthChallenge Create(string marketplace)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(marketplace);
        var code = marketplace.Trim().ToLowerInvariant();
        if (!Markets.TryGetValue(code, out var market))
            throw new ArgumentException("The selected Audible marketplace is not supported.", nameof(marketplace));

        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

        // Android's registration profile uses a 10-byte random serial encoded as
        // 20 lowercase hexadecimal characters, not the iOS 40-character serial.
        var serial = Convert.ToHexString(RandomNumberGenerator.GetBytes(10)).ToLowerInvariant();
        var serialAndType = $"{serial}#{AndroidDeviceType}";
        var clientId = Convert.ToHexString(Encoding.UTF8.GetBytes(serialAndType)).ToLowerInvariant();
        var callback = $"https://www.{market.AmazonDomain}/ap/maplanding";

        var query = new List<KeyValuePair<string, string>>
        {
            new("openid.oa2.response_type", "code"),
            new("openid.oa2.code_challenge_method", "S256"),
            new("openid.oa2.code_challenge", challenge),
            new("openid.return_to", callback),
            new("openid.assoc_handle", $"amzn_audible_android_aui_{market.CountryCode}"),
            new("openid.identity", "http://specs.openid.net/auth/2.0/identifier_select"),
            new("openid.claimed_id", "http://specs.openid.net/auth/2.0/identifier_select"),
            new("openid.mode", "checkid_setup"),
            new("openid.ns", "http://specs.openid.net/auth/2.0"),
            new("openid.ns.oa2", "http://www.amazon.com/ap/ext/oauth/2"),
            new("openid.oa2.client_id", "device:" + clientId),
            new("openid.oa2.scope", "device_auth_access"),
            new("openid.ns.pape", "http://specs.openid.net/extensions/pape/1.0"),
            new("openid.pape.max_auth_age", "0"),
            new("accountStatusPolicy", "P1"),
            new("pageId", $"amzn_audible_android_aui_v2_dark_{market.CountryCode}"),
            new("marketPlaceId", market.MarketplaceId),
            new("disableLoginPrepopulate", "1")
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
