namespace Libation.Mobile.Core;

/// <summary>
/// Streams an audio file from a provider-issued HTTPS URL to local storage.
/// This component does not discover URLs, authenticate to a store, or decrypt media.
/// Incomplete transfers are retained as a .part file and resumed with HTTP Range
/// when the provider supports byte-range requests.
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
        CancellationToken cancellationToken = default)
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

        // A stable partial-file name allows a later worker run to continue the same transfer.
        var partialPath = fullPath + ".part";
        var existingBytes = File.Exists(partialPath) ? new FileInfo(partialPath).Length : 0;
        if (existingBytes > _maximumBytes)
        {
            File.Delete(partialPath);
            existingBytes = 0;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, authorizedDownloadUri);
        if (existingBytes > 0)
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(existingBytes, null);

        using var response = await _httpClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        // Never accept a provider redirect from HTTPS to an insecure transport.
        if (response.RequestMessage?.RequestUri is { } finalUri &&
            !string.Equals(finalUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            throw new HttpRequestException("The download redirected to a non-HTTPS address.");

        var isPartialResponse = response.StatusCode == System.Net.HttpStatusCode.PartialContent;
        var append = existingBytes > 0 && isPartialResponse;
        var startingBytes = append ? existingBytes : 0;
        var responseLength = response.Content.Headers.ContentLength;
        long? expectedFinalLength = responseLength is long length
            ? startingBytes + length
            : null;

        if (isPartialResponse)
        {
            var range = response.Content.Headers.ContentRange;
            if (range?.From != startingBytes || range.To is not long rangeEnd || rangeEnd < startingBytes)
                throw new HttpRequestException("The server returned an invalid byte range for this download.");

            if (range.Length is not long totalLength || totalLength <= rangeEnd)
                throw new HttpRequestException("The server did not provide a verifiable total length for the partial download.");

            var segmentLength = rangeEnd - startingBytes + 1;
            if (responseLength is long declaredSegmentLength && declaredSegmentLength != segmentLength)
                throw new HttpRequestException("The server's Content-Length does not match its byte range.");

            expectedFinalLength = totalLength;
        }

        if (expectedFinalLength is long expected && expected > _maximumBytes)
            throw new InvalidDataException("The audiobook exceeds the configured download size limit.");

        // A server that ignores Range returns 200; safely restart rather than append duplicate bytes.
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new FileStream(
            partialPath,
            append ? FileMode.Open : FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 64 * 1024,
            useAsync: true);
        if (append)
            output.Seek(0, SeekOrigin.End);

        var buffer = new byte[64 * 1024];
        long received = startingBytes;
        try
        {
            while (true)
            {
                var read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                if (read == 0) break;
                if (read > _maximumBytes - received)
                    throw new InvalidDataException("The audiobook exceeds the configured download size limit.");
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                received += read;
                progress?.Report(new AudioDownloadProgress(received, expectedFinalLength));
            }

            if (expectedFinalLength is long expectedBytes && received != expectedBytes)
                throw new InvalidDataException(
                    $"The download ended early or contained extra data. Expected {expectedBytes} bytes but received {received}.");

            // A successful HTTP response is not necessarily a usable audiobook.
            // Never publish a zero-byte file or mark an empty transfer as complete.
            if (received == 0)
                throw new InvalidDataException("The audio provider returned an empty download.");

            await output.FlushAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Cancellation preserves accepted bytes as a durable .part checkpoint for the next worker run.
            await output.FlushAsync(CancellationToken.None);
            output.Flush(flushToDisk: true);
            throw;
        }
        output.Close();
        File.Move(partialPath, fullPath, overwrite: true);
        return new AudioDownloadResult(fullPath, received);
    }
}

public sealed record AudioDownloadProgress(long BytesReceived, long? TotalBytes)
{
    public double? Percentage => TotalBytes is > 0
        ? Math.Clamp(BytesReceived * 100d / TotalBytes.Value, 0d, 100d)
        : null;
}

public sealed record AudioDownloadResult(string FilePath, long BytesReceived);
