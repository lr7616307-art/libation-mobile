using System.Net;

namespace Libation.Mobile.Core;

/// <summary>
/// Streams an authorized HTTPS media object to app-private storage. A stable .part
/// file is retained across cancellation or connection loss and resumed with HTTP
/// Range when the provider supports it. This component does not discover URLs,
/// authenticate to a store, or decrypt protected media.
/// </summary>
public sealed class AudioFileDownloader
{
    private readonly HttpClient _httpClient;
    private readonly long _maximumBytes;

    public AudioFileDownloader(HttpClient httpClient, long maximumBytes = 4L * 1024 * 1024 * 1024)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        if (maximumBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        _maximumBytes = maximumBytes;
    }

    public async Task<AudioDownloadResult> DownloadAsync(
        Uri authorizedDownloadUri,
        string destinationPath,
        IProgress<AudioDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default,
        IReadOnlyDictionary<string, string>? requestHeaders = null)
    {
        ArgumentNullException.ThrowIfNull(authorizedDownloadUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        if (!authorizedDownloadUri.IsAbsoluteUri ||
            !string.Equals(authorizedDownloadUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Only absolute HTTPS download URLs are accepted.", nameof(authorizedDownloadUri));

        var fullPath = Path.GetFullPath(destinationPath);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException("Destination must include a parent directory.", nameof(destinationPath));
        Directory.CreateDirectory(directory);
        var partialPath = fullPath + ".part";

        var existingBytes = File.Exists(partialPath) ? new FileInfo(partialPath).Length : 0L;
        if (existingBytes > _maximumBytes)
        {
            File.Delete(partialPath);
            existingBytes = 0;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, authorizedDownloadUri);
        if (requestHeaders is not null)
        {
            foreach (var header in requestHeaders)
            {
                if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value))
                    throw new ArgumentException($"The download request header '{header.Key}' is invalid.", nameof(requestHeaders));
            }
        }
        if (existingBytes > 0)
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(existingBytes, null);

        using var response = await _httpClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && existingBytes > 0)
        {
            var resourceLength = response.Content.Headers.ContentRange?.Length;
            if (resourceLength == existingBytes)
            {
                File.Move(partialPath, fullPath, overwrite: true);
                return new AudioDownloadResult(fullPath, existingBytes);
            }

            // The saved fragment is incompatible with the current URL. Preserve
            // no corrupt merge: delete the fragment and let a deliberate retry
            // request the file from byte zero.
            TryDelete(partialPath);
            throw new HttpRequestException(
                "The saved partial download no longer matches the provider resource. Retry to restart it.",
                null,
                response.StatusCode);
        }

        var isRangeResponse = response.StatusCode == HttpStatusCode.PartialContent;
        if (isRangeResponse)
        {
            if (existingBytes == 0 ||
                response.Content.Headers.ContentRange?.From != existingBytes)
            {
                TryDelete(partialPath);
                throw new InvalidDataException("The provider returned an unexpected partial-download range.");
            }
        }
        else
        {
            response.EnsureSuccessStatusCode();
            // A server may not support Range. HTTP 200 means it sent the complete
            // resource, so replace the saved fragment instead of appending twice.
            existingBytes = 0;
        }

        if (response.RequestMessage?.RequestUri is { } finalUri &&
            !string.Equals(finalUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            throw new HttpRequestException("The download redirected to a non-HTTPS address.");

        var totalBytes = isRangeResponse
            ? response.Content.Headers.ContentRange?.Length
            : response.Content.Headers.ContentLength;
        if (totalBytes is null && response.Content.Headers.ContentLength is { } responseLength)
            totalBytes = existingBytes + responseLength;
        if (totalBytes > _maximumBytes)
            throw new InvalidDataException("The audiobook exceeds the configured download size limit.");
        if (totalBytes is { } knownLength && knownLength < existingBytes)
        {
            TryDelete(partialPath);
            throw new InvalidDataException("The saved partial download is larger than the provider resource.");
        }

        long received = existingBytes;
        progress?.Report(new AudioDownloadProgress(received, totalBytes));
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var output = new FileStream(
            partialPath,
            isRangeResponse ? FileMode.Append : FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 64 * 1024,
            useAsync: true);

        var buffer = new byte[64 * 1024];
        while (true)
        {
            var read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0) break;
            if (read > _maximumBytes - received)
                throw new InvalidDataException("The audiobook exceeds the configured download size limit.");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            received += read;
            progress?.Report(new AudioDownloadProgress(received, totalBytes));
        }

        if (received == 0)
        {
            TryDelete(partialPath);
            throw new InvalidDataException("The provider returned an empty audiobook download.");
        }
        if (totalBytes is { } expectedBytes && received != expectedBytes)
            throw new InvalidDataException("The downloaded audiobook is incomplete. Its partial data has been retained for resume.");

        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        output.Close();
        File.Move(partialPath, fullPath, overwrite: true);
        return new AudioDownloadResult(fullPath, received);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

public sealed record AudioDownloadProgress(long BytesReceived, long? TotalBytes)
{
    public double? Percentage => TotalBytes is > 0
        ? Math.Clamp(BytesReceived * 100d / TotalBytes.Value, 0d, 100d)
        : null;
}

public sealed record AudioDownloadResult(string FilePath, long BytesReceived);
