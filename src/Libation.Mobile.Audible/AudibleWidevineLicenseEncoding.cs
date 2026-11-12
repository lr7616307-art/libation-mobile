namespace Libation.Mobile.Audible;

/// <summary>
/// Serializes opaque platform DRM bytes to/from Audible's JSON Base64 string
/// contract. It does not inspect Widevine messages or expose content keys.
/// </summary>
public static class AudibleWidevineLicenseEncoding
{
    public const int MaximumMessageBytes = 1024 * 1024;

    public static string EncodeChallenge(ReadOnlySpan<byte> platformChallenge)
    {
        if (platformChallenge.IsEmpty || platformChallenge.Length > MaximumMessageBytes)
            throw new ArgumentOutOfRangeException(
                nameof(platformChallenge), "The Widevine challenge has an unsupported size.");

        return Convert.ToBase64String(platformChallenge);
    }

    public static byte[] DecodeLicenseResponse(string encodedLicenseResponse)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(encodedLicenseResponse);

        // Bound input before allocating the decoded response. Base64 expands by
        // approximately one third; this ceiling permits at most 1 MiB of bytes.
        var maximumEncodedLength = ((MaximumMessageBytes + 2) / 3) * 4;
        if (encodedLicenseResponse.Length > maximumEncodedLength)
            throw new InvalidDataException(
                "The Audible Widevine licence response exceeds the supported size.");

        // Do not silently accept whitespace or alternate formatting in this
        // security-sensitive transport value.
        if (encodedLicenseResponse.Any(char.IsWhiteSpace))
            throw new InvalidDataException(
                "The Audible Widevine licence response is not valid Base64.");

        byte[] response;
        try
        {
            response = Convert.FromBase64String(encodedLicenseResponse);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException(
                "The Audible Widevine licence response is not valid Base64.", exception);
        }

        if (response.Length is 0 or > MaximumMessageBytes)
            throw new InvalidDataException(
                "The Audible Widevine licence response has an unsupported size.");

        return response;
    }
}
