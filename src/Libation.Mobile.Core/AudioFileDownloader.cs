namespace Libation.Mobile.Core;

/// <summary>
/// Streams an audio file from a provider-issued HTTPS URL to local storage.
/// This component does not discover URLs, authenticate to a store, or decrypt media.
/// Incomplete transfers are retained as a .part file and resumed with HTTP Range
/// when the provider supports byte-range requests.
/// </summary>
public sealed class AudioFileDownloader
{
    private const long DurableCheckpointInterval = 1024 * 1024;
    private const int MaximumRedirects = 5;
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
            !string.Equals(authorizedDownloadUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(authorizedDownloadUri.UserInfo))
            throw new ArgumentException("Only absolute HTTPS download URLs without embedded credentials are accepted.", nameof(authorizedDownloadUri));

        var fullPath = Path.GetFullPath(destinationPath);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException("Destination must include a parent directory.", nameof(destinationPath));
        Directory.CreateDirectory(directory);

        // Resume only when a strong validator is available. Without one, a refreshed
        // provider URL could point at a different representation and silently splice
        // two versions of an audiobook together.
        var partialPath = fullPath + ".part";
        var validatorPath = partialPath + ".validator";
        var existingBytes = File.Exists(partialPath) ? new FileInfo(partialPath).Length : 0;
        var savedValidator = existingBytes > 0
            ? await ReadValidatorAsync(validatorPath, cancellationToken)
            : null;
        if (existingBytes > _maximumBytes || (existingBytes > 0 && savedValidator is null))
        {
            if (File.Exists(partialPath)) File.Delete(partialPath);
            if (File.Exists(validatorPath)) File.Delete(validatorPath);
            existingBytes = 0;
            savedValidator = null;
        }

        using var response = await SendFollowingSafeRedirectsAsync(
            authorizedDownloadUri, existingBytes, savedValidator, cancellationToken);

        // Validate the final transport before handling any status code, including 416.
        // Otherwise an insecure redirect could reach the completed-partial recovery path.
        if (response.RequestMessage?.RequestUri is { } finalUri &&
            !string.Equals(finalUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            throw new HttpRequestException("The download redirected to a non-HTTPS address.");

        // Recover the narrow crash window where the complete partial file was flushed
        // but the process stopped before the final atomic rename. A 416 is only proof
        // of completion when its total length and representation validator both match.
        if (response.StatusCode == System.Net.HttpStatusCode.RequestedRangeNotSatisfiable &&
            existingBytes > 0 && savedValidator is { } completedValidator &&
            response.Content.Headers.ContentRange is { HasLength: true, Length: long unsatisfiedRangeTotal } unsatisfiedRange &&
            string.Equals(unsatisfiedRange.Unit, "bytes", StringComparison.OrdinalIgnoreCase) &&
            unsatisfiedRangeTotal == existingBytes &&
            ResponseMatchesValidator(response, completedValidator))
        {
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(partialPath, fullPath, overwrite: true);
            if (File.Exists(validatorPath))
                File.Delete(validatorPath);
            return new AudioDownloadResult(fullPath, existingBytes);
        }

        response.EnsureSuccessStatusCode();


        var isPartialResponse = response.StatusCode == System.Net.HttpStatusCode.PartialContent;
        var append = existingBytes > 0 && isPartialResponse;
        if (append && savedValidator is { } priorValidator &&
            !ResponseMatchesValidator(response, priorValidator))
        {
            // The retained bytes belong to a different representation. Keeping
            // them would make every later retry send the same stale If-Range
            // validator and fail again. Discard both pieces of resume state so
            // the next attempt starts from byte zero with a fresh response.
            if (File.Exists(partialPath)) File.Delete(partialPath);
            if (File.Exists(validatorPath)) File.Delete(validatorPath);
            throw new HttpRequestException(
                "The server resumed a different audiobook representation. The stale partial download was discarded; retry to start again.");
        }

        var responseValidator = TryGetValidator(response);
        if (responseValidator is not null)
            await WriteValidatorAsync(validatorPath, responseValidator.Value, cancellationToken);
        else if (!append && File.Exists(validatorPath))
            File.Delete(validatorPath);
        var startingBytes = append ? existingBytes : 0;
        var responseLength = response.Content.Headers.ContentLength;
        long? expectedFinalLength = responseLength is long length
            ? startingBytes + length
            : null;

        if (isPartialResponse)
        {
            var range = response.Content.Headers.ContentRange;
            if (range is null ||
                !string.Equals(range.Unit, "bytes", StringComparison.OrdinalIgnoreCase) ||
                range.From != startingBytes || range.To is not long rangeEnd || rangeEnd < startingBytes)
                throw new HttpRequestException("The server returned an invalid byte range for this download.");

            if (range.Length is not long partialTotalLength || partialTotalLength <= rangeEnd)
                throw new HttpRequestException("The server did not provide a verifiable total length for the partial download.");

            var segmentLength = rangeEnd - startingBytes + 1;
            if (responseLength is long declaredSegmentLength && declaredSegmentLength != segmentLength)
                throw new HttpRequestException("The server's Content-Length does not match its byte range.");

            expectedFinalLength = partialTotalLength;
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
        var nextDurableCheckpoint = (received / DurableCheckpointInterval + 1) * DurableCheckpointInterval;
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

                // Flush every MiB through the OS file buffers to stable storage. This
                // bounds data loss after an abrupt process/device stop without forcing
                // a disk sync for every 64 KiB network read.
                if (received >= nextDurableCheckpoint)
                {
                    await FlushDurablyAsync(output);
                    nextDurableCheckpoint = (received / DurableCheckpointInterval + 1) * DurableCheckpointInterval;
                }
            }
        }
        catch
        {
            // Preserve all bytes accepted by the file stream when a transfer is
            // cancelled or the response stream fails. Dispose alone only guarantees
            // a normal close; force a durable checkpoint before the queue retries.
            await FlushDurablyAsync(output);
            throw;
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
        output.Close();
        File.Move(partialPath, fullPath, overwrite: true);
        if (File.Exists(validatorPath))
            File.Delete(validatorPath);
        return new AudioDownloadResult(fullPath, received);
    }

    private async Task<HttpResponseMessage> SendFollowingSafeRedirectsAsync(
        Uri initialUri,
        long existingBytes,
        ResumeValidator? savedValidator,
        CancellationToken cancellationToken)
    {
        var currentUri = initialUri;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var redirectCount = 0; ; redirectCount++)
        {
            if (!visited.Add(currentUri.AbsoluteUri))
                throw new HttpRequestException("The download provider returned a redirect loop.");

            using var request = new HttpRequestMessage(HttpMethod.Get, currentUri);
            if (existingBytes > 0)
            {
                request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(existingBytes, null);
                request.Headers.IfRange = savedValidator!.Value.Condition;
            }

            var response = await _httpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!IsRedirect(response.StatusCode))
                return response;

            var location = response.Headers.Location;
            if (location is null)
            {
                response.Dispose();
                throw new HttpRequestException("The download provider returned a redirect without a Location header.");
            }

            if (redirectCount >= MaximumRedirects)
            {
                response.Dispose();
                throw new HttpRequestException("The download provider exceeded the maximum number of redirects.");
            }

            var nextUri = location.IsAbsoluteUri ? location : new Uri(currentUri, location);
            if (!nextUri.IsAbsoluteUri ||
                !string.Equals(nextUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
                !string.IsNullOrEmpty(nextUri.UserInfo))
            {
                response.Dispose();
                throw new HttpRequestException("The download redirect target must be HTTPS and must not contain embedded credentials.");
            }

            response.Dispose();
            currentUri = nextUri;
        }
    }

    private static bool IsRedirect(System.Net.HttpStatusCode statusCode)
        => statusCode is System.Net.HttpStatusCode.MovedPermanently
            or System.Net.HttpStatusCode.Redirect
            or System.Net.HttpStatusCode.RedirectMethod
            or System.Net.HttpStatusCode.TemporaryRedirect
            or System.Net.HttpStatusCode.PermanentRedirect;

    private static async Task FlushDurablyAsync(FileStream output)
    {
        await output.FlushAsync(CancellationToken.None);
        output.Flush(flushToDisk: true);
    }

    private readonly record struct ResumeValidator(
        System.Net.Http.Headers.RangeConditionHeaderValue Condition,
        string SerializedValue);

    private static async Task<ResumeValidator?> ReadValidatorAsync(
        string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return null;
        var value = (await File.ReadAllTextAsync(path, cancellationToken)).Trim();
        if (value.StartsWith("etag:", StringComparison.Ordinal) &&
            System.Net.Http.Headers.EntityTagHeaderValue.TryParse(value[5..], out var entityTag) &&
            entityTag is not null && !entityTag.IsWeak)
            return new ResumeValidator(
                new System.Net.Http.Headers.RangeConditionHeaderValue(entityTag), value);
        if (value.StartsWith("modified:", StringComparison.Ordinal) &&
            DateTimeOffset.TryParse(value[9..], System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal, out var modified))
            return new ResumeValidator(
                new System.Net.Http.Headers.RangeConditionHeaderValue(modified), value);
        return null;
    }

    private static ResumeValidator? TryGetValidator(HttpResponseMessage response)
    {
        var entityTag = response.Headers.ETag;
        if (entityTag is not null && !entityTag.IsWeak)
            return new ResumeValidator(
                new System.Net.Http.Headers.RangeConditionHeaderValue(entityTag),
                "etag:" + entityTag);
        var modified = response.Content.Headers.LastModified;
        return modified is null ? null : new ResumeValidator(
            new System.Net.Http.Headers.RangeConditionHeaderValue(modified.Value),
            "modified:" + modified.Value.ToUniversalTime().ToString("R", System.Globalization.CultureInfo.InvariantCulture));
    }

    private static bool ResponseMatchesValidator(
        HttpResponseMessage response, ResumeValidator saved)
    {
        if (saved.SerializedValue.StartsWith("etag:", StringComparison.Ordinal))
            return response.Headers.ETag is { } entityTag &&
                string.Equals("etag:" + entityTag, saved.SerializedValue, StringComparison.Ordinal);
        if (saved.SerializedValue.StartsWith("modified:", StringComparison.Ordinal))
            return response.Content.Headers.LastModified is { } modified &&
                string.Equals(
                    "modified:" + modified.ToUniversalTime().ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                    saved.SerializedValue, StringComparison.Ordinal);
        return false;
    }

    private static async Task WriteValidatorAsync(
        string path, ResumeValidator validator, CancellationToken cancellationToken)
    {
        var temporaryPath = path + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporaryPath, validator.SerializedValue, cancellationToken);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }
}

public sealed record AudioDownloadProgress(long BytesReceived, long? TotalBytes)
{
    public double? Percentage => TotalBytes is > 0
        ? Math.Clamp(BytesReceived * 100d / TotalBytes.Value, 0d, 100d)
        : null;
}

public sealed record AudioDownloadResult(string FilePath, long BytesReceived);
