using System.Text;

namespace Libation.Mobile.Core;

/// <summary>
/// Builds the standard Android MediaDrm provisioning URL from the URL and
/// opaque request data supplied by the Widevine plugin. The URL is validated
/// as HTTPS; request bytes are encoded only for the signedRequest query value.
/// </summary>
public static class WidevineProvisioningRequestUri
{
    public const int MaximumRequestBytes = 256 * 1024;

    public static Uri Create(string defaultUrl, byte[] requestData)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultUrl);
        ArgumentNullException.ThrowIfNull(requestData);

        if (requestData.Length is 0 or > MaximumRequestBytes)
            throw new ArgumentOutOfRangeException(
                nameof(requestData), "The Widevine provisioning request has an unsupported size.");

        if (!Uri.TryCreate(defaultUrl, UriKind.Absolute, out var baseUri) ||
            !string.Equals(baseUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                "Android Widevine returned an invalid or non-HTTPS provisioning endpoint.");

        var signedRequest = Encoding.UTF8.GetString(requestData);
        if (string.IsNullOrWhiteSpace(signedRequest))
            throw new InvalidDataException("Android Widevine returned an empty provisioning request.");

        // Android's MediaPlayer/MediaDrm provisioning flow sends the opaque
        // request as the signedRequest query parameter using HTTP POST.
        var separator = string.IsNullOrEmpty(baseUri.Query) ? "?" : "&";
        var requestUri = new Uri(
            defaultUrl + separator + "signedRequest=" + Uri.EscapeDataString(signedRequest),
            UriKind.Absolute);

        if (!string.Equals(requestUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The Widevine provisioning endpoint must remain HTTPS.");

        return requestUri;
    }
}
