using System.Text;

namespace Libation.Mobile.Audible;

/// <summary>
/// Fetches the provider-issued DASH MPD over HTTPS and parses its Widevine
/// initialization data. The manifest URL may contain short-lived credentials;
/// neither the URL nor response contents are written to diagnostics.
/// </summary>
public sealed class AudibleWidevineManifestClient(HttpClient httpClient)
{
    public const int MaximumManifestBytes = 4 * 1024 * 1024;

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public async Task<AudibleWidevineManifestInfo> FetchAsync(
        Uri manifestUri,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifestUri);
        ArgumentNullException.ThrowIfNull(httpClient);

        if (!manifestUri.IsAbsoluteUri ||
            !string.Equals(manifestUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(
                "The Audible Widevine manifest URI must be an absolute HTTPS URI.",
                nameof(manifestUri));

        cancellationToken.ThrowIfCancellationRequested();

        using var request = new HttpRequestMessage(HttpMethod.Get, manifestUri);
        request.Headers.Accept.ParseAdd("application/dash+xml");
        request.Headers.Accept.ParseAdd("application/xml");
        request.Headers.Accept.ParseAdd("text/xml");

        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"Audible DASH manifest request failed with HTTP status {(int)response.StatusCode}.",
                inner: null,
                response.StatusCode);

        var finalUri = response.RequestMessage?.RequestUri ?? manifestUri;
        if (!finalUri.IsAbsoluteUri ||
            !string.Equals(finalUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                "The Audible DASH manifest response did not remain on HTTPS.");

        if (response.Content.Headers.ContentLength is long contentLength &&
            contentLength > MaximumManifestBytes)
            throw new InvalidDataException("The Audible DASH manifest exceeds the supported size.");

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];

        while (true)
        {
            var read = await stream.ReadAsync(chunk.AsMemory(), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
                break;

            if (buffer.Length + read > MaximumManifestBytes)
                throw new InvalidDataException("The Audible DASH manifest exceeds the supported size.");

            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken)
                .ConfigureAwait(false);
        }

        string manifestXml;
        try
        {
            manifestXml = StrictUtf8.GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length));
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("The Audible DASH manifest is not valid UTF-8.", exception);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return AudibleWidevineManifestParser.Parse(manifestXml);
    }
}
