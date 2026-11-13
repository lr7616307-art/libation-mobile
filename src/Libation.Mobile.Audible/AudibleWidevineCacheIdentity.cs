using System.Security.Cryptography;
using System.Text;

namespace Libation.Mobile.Audible;

/// <summary>
/// Produces non-secret cache identities for Audible Widevine DASH resources.
/// When Audible supplies its stable ACR/file-version pair, short-lived URL
/// authorization parameters are excluded from the resource identity so a
/// refreshed signed URL can reuse the same encrypted cache. The content version
/// remains part of the identity, preventing bytes from different versions mixing.
/// </summary>
public static class AudibleWidevineCacheIdentity
{
    public const string UnversionedContentKey = "u";

    public static string CreateContentVersionKey(string? acr, long? fileVersion)
    {
        var hasAcr = !string.IsNullOrWhiteSpace(acr);
        var hasFileVersion = fileVersion.HasValue && fileVersion.Value >= 0;
        if (hasAcr != hasFileVersion || (fileVersion.HasValue && fileVersion.Value < 0))
            throw new ArgumentException("Audible cache version metadata must contain both ACR and a non-negative file version, or neither.");

        if (!hasAcr)
            return UnversionedContentKey;

        var value = acr!.Trim() + "\n" + fileVersion!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return "v-" + Convert.ToHexString(digest).ToLowerInvariant();
    }

    public static bool IsValidContentVersionKey(string? value)
    {
        if (string.Equals(value, UnversionedContentKey, StringComparison.Ordinal))
            return true;
        return value is { Length: 66 } &&
            value.StartsWith("v-", StringComparison.Ordinal) &&
            value.Skip(2).All(character =>
                character is >= '0' and <= '9' or >= 'a' and <= 'f');
    }

    /// <summary>
    /// Builds a bounded, non-secret Media3 cache scope for one Audible content
    /// version. The title prefix remains stable so every version can be removed
    /// together without confusing one title/account with another.
    /// </summary>
    public static string CreateCacheScope(string titleScope, string contentVersionKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(titleScope);
        if (titleScope.Contains(':'))
            throw new ArgumentException("The protected cache title scope is invalid.", nameof(titleScope));
        if (titleScope.Length > 128)
            throw new ArgumentOutOfRangeException(nameof(titleScope), "The protected cache title scope is too long.");
        if (!IsValidContentVersionKey(contentVersionKey))
            throw new ArgumentException("The Audible cache content-version key is invalid.", nameof(contentVersionKey));

        // ReconcileCacheIndex caps scopes at 128 characters and disallows ':'.
        // Retain 192 bits from the version digest while keeping the stable title
        // prefix available for title-wide cleanup.
        var versionSuffix = string.Equals(
            contentVersionKey, UnversionedContentKey, StringComparison.Ordinal)
            ? "u"
            : "v-" + contentVersionKey[2..50];
        var scope = titleScope + "-" + versionSuffix;
        if (scope.Length > 128)
            throw new ArgumentOutOfRangeException(nameof(titleScope), "The versioned cache scope exceeds its supported length.");
        return scope;
    }

    public static bool IsTitleCacheScope(string titleScope, string candidate)
    {
        if (string.IsNullOrWhiteSpace(titleScope) || string.IsNullOrWhiteSpace(candidate))
            return false;
        return string.Equals(candidate, titleScope, StringComparison.Ordinal)
            || candidate.StartsWith(titleScope + "-", StringComparison.Ordinal);
    }

    public static bool IsContentCacheKey(string contentScope, string cacheKey)
    {
        if (string.IsNullOrWhiteSpace(contentScope) || string.IsNullOrWhiteSpace(cacheKey))
            return false;
        return cacheKey.StartsWith(contentScope + ":", StringComparison.Ordinal);
    }

    public static string CreateResourceFingerprint(Uri resourceUri, string contentVersionKey)
    {
        ArgumentNullException.ThrowIfNull(resourceUri);
        if (!IsValidContentVersionKey(contentVersionKey))
            throw new ArgumentException("The Audible cache content-version key is invalid.", nameof(contentVersionKey));

        var canNormalizeAuthorization = !string.Equals(
            contentVersionKey, UnversionedContentKey, StringComparison.Ordinal);
        var canonicalUri = NormalizeResourceUri(resourceUri, canNormalizeAuthorization);
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(contentVersionKey + "\n" + canonicalUri));
        return Convert.ToHexString(digest).ToLowerInvariant();
    }

    public static string NormalizeResourceUri(Uri resourceUri, bool removeVolatileAuthorizationParameters)
    {
        ArgumentNullException.ThrowIfNull(resourceUri);
        if (!resourceUri.IsAbsoluteUri ||
            !string.Equals(resourceUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            resourceUri.UserInfo.Length != 0)
            throw new ArgumentException("An absolute HTTPS resource URI without user information is required.", nameof(resourceUri));

        var builder = new UriBuilder(resourceUri) { Fragment = string.Empty };
        if (!removeVolatileAuthorizationParameters || string.IsNullOrEmpty(resourceUri.Query))
        {
            builder.Query = resourceUri.Query.TrimStart('?');
            return builder.Uri.GetComponents(
                UriComponents.SchemeAndServer | UriComponents.PathAndQuery,
                UriFormat.UriEscaped);
        }

        var retained = resourceUri.Query
            .TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(raw =>
            {
                var equals = raw.IndexOf('=');
                var rawName = equals < 0 ? raw : raw[..equals];
                var decodedName = Uri.UnescapeDataString(rawName.Replace('+', ' '));
                return new { Raw = raw, Name = decodedName };
            })
            .Where(parameter => !IsVolatileAuthorizationParameter(parameter.Name))
            .OrderBy(parameter => parameter.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(parameter => parameter.Raw, StringComparer.Ordinal)
            .Select(parameter => parameter.Raw);

        builder.Query = string.Join("&", retained);
        return builder.Uri.GetComponents(
            UriComponents.SchemeAndServer | UriComponents.PathAndQuery,
            UriFormat.UriEscaped);
    }

    private static bool IsVolatileAuthorizationParameter(string name)
    {
        var key = name.Trim().ToLowerInvariant();
        return key is
            "token" or "access_token" or "auth_token" or "authorization" or "auth" or
            "signature" or "sig" or "expires" or "expiry" or "policy" or
            "key-pair-id" or "awsaccesskeyid" or "googleaccessid" or
            "hdnea" or "hdnts" or "jwt" or "x-amz-security-token" or
            "x-amz-signature" or "x-goog-signature" ||
            key.StartsWith("x-amz-", StringComparison.Ordinal) ||
            key.StartsWith("x-goog-", StringComparison.Ordinal);
    }
}
