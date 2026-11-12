using System.Globalization;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Libation.Mobile.Audible;

/// <summary>
/// Builds and reads the reverse-engineered Audible content-license-refresh
/// envelope. This protocol helper performs no network calls and makes no
/// assumption that a provider status string represents a successful grant.
/// </summary>
public static class AudibleWidevineLicenseRefreshProtocol
{
    private static readonly Regex AsinPattern = new(
        "^[A-Za-z0-9]{10}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Creates the documented single-title Widevine refresh request body.
    /// The returned JObject contains a Base64 DRM challenge and is sensitive:
    /// callers must not log it or persist it.
    /// </summary>
    public static JObject CreateRequestBody(
        string asin,
        AudibleWidevineProviderLicenseReference reference,
        byte[] drmChallenge)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(asin);
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(drmChallenge);

        if (!AsinPattern.IsMatch(asin))
            throw new ArgumentException("The Audible audiobook identifier is invalid.", nameof(asin));
        if (string.IsNullOrWhiteSpace(reference.Acr) || reference.Acr.Length > 8192)
            throw new ArgumentException("The Audible content reference is invalid.", nameof(reference));
        if (reference.FileVersion < 0)
            throw new ArgumentOutOfRangeException(nameof(reference), "The Audible file version is invalid.");
        if (drmChallenge.Length is 0 or > AudibleWidevineLicenseEncoding.MaximumMessageBytes)
            throw new ArgumentOutOfRangeException(nameof(drmChallenge), "The Widevine challenge size is unsupported.");

        return new JObject
        {
            ["license_refresh_requests"] = new JArray
            {
                new JObject
                {
                    ["asin"] = asin,
                    ["acr"] = reference.Acr,
                    ["file_version"] = reference.FileVersion.ToString(CultureInfo.InvariantCulture),
                    ["drm_type"] = "Widevine",
                    ["drm_challenge"] = Convert.ToBase64String(drmChallenge)
                }
            },
            ["tenant_id"] = "Audible"
        };
    }

    /// <summary>
    /// Parses one title's response from the batch response. Provider status is
    /// returned verbatim; this method deliberately does not infer success from
    /// HTTP 200 or from the presence of a licence field.
    /// </summary>
    public static AudibleWidevineLicenseRefreshResult ParseResponse(
        string responseJson,
        string expectedAsin)
    {
        if (string.IsNullOrWhiteSpace(responseJson))
            throw new InvalidDataException("Audible returned an empty licence-refresh response.");
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedAsin);
        if (!AsinPattern.IsMatch(expectedAsin))
            throw new ArgumentException("The Audible audiobook identifier is invalid.", nameof(expectedAsin));
        if (System.Text.Encoding.UTF8.GetByteCount(responseJson) > 1024 * 1024)
            throw new InvalidDataException("The Audible licence-refresh response is oversized.");

        JObject payload;
        try
        {
            using var stringReader = new StringReader(responseJson);
            using var jsonReader = new JsonTextReader(stringReader)
            {
                DateParseHandling = DateParseHandling.None,
                MaxDepth = 32
            };
            payload = JObject.Load(jsonReader);
            if (jsonReader.Read())
                throw new InvalidDataException("Audible returned trailing data after the licence-refresh response.");
        }
        catch (Newtonsoft.Json.JsonException exception)
        {
            throw new InvalidDataException("Audible returned an invalid licence-refresh response.", exception);
        }

        if (payload["license_refresh_responses"] is not JArray responses)
            throw new InvalidDataException("Audible returned no licence-refresh result collection.");

        var matches = responses
            .OfType<JObject>()
            .Where(item => string.Equals(
                item["asin"]?.Value<string>(), expectedAsin, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (matches.Length != 1)
            throw new InvalidDataException(
                matches.Length == 0
                    ? "Audible returned no licence-refresh result for this audiobook."
                    : "Audible returned duplicate licence-refresh results for this audiobook.");

        var result = matches[0];
        var status = result["status"] as JObject;
        var metadata = result["license_metadata"] as JObject;
        var denialReasons = status?["license_denial_reasons"] is JArray denialArray
            ? denialArray
                .OfType<JValue>()
                .Select(value => value.Value<string>())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!.Trim())
                .Take(8)
                .ToArray()
            : Array.Empty<string>();

        return new AudibleWidevineLicenseRefreshResult(
            expectedAsin,
            status?["status_code"]?.Value<string>(),
            status?["error_message"]?.Value<string>(),
            result["license"]?.Value<string>(),
            metadata?["license_id"]?.Value<string>(),
            metadata?["license_refresh_date"]?.Value<string>(),
            metadata?["license_removal_date"]?.Value<string>(),
            metadata?["license_expiration_date"]?.Value<string>())
        {
            GrantedRight = result["granted_right"]?.Value<string>(),
            AccessExpiryDate = result["access_expiry_date"]?.Value<string>(),
            LicenseDenialReasons = denialReasons
        };
    }
}

/// <summary>
/// Raw per-title result returned by Audible's documented community-observed
/// refresh response. StatusCode is intentionally uninterpreted.
/// </summary>
public sealed record AudibleWidevineLicenseRefreshResult(
    string Asin,
    string? StatusCode,
    string? ErrorMessage,
    string? License,
    string? LicenseId,
    string? LicenseRefreshDate,
    string? LicenseRemovalDate,
    string? LicenseExpirationDate)
{
    public string? GrantedRight { get; init; }
    public string? AccessExpiryDate { get; init; }
    public IReadOnlyList<string> LicenseDenialReasons { get; init; } = Array.Empty<string>();
}
