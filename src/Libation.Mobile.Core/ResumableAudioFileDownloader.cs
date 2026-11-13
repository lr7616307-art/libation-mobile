using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Libation.Mobile.Core;

/// <summary>
/// Downloads a provider-authorized HTTPS file to a persistent .part file.
/// Interrupted transfers keep their bytes and can continue with HTTP Range.
/// A resume fingerprint prevents appending bytes encrypted under a different
/// Audible voucher to an existing partial file.
/// </summary>
public sealed class ResumableAudioFileDownloader(HttpClient httpClient)
{
    private const long MaximumDownloadBytes = 8L * 1024 * 1024 * 1024;
    private const int BufferSize = 128 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 8
    };

    public async Task<ResumableAudioFileResult> DownloadAsync(
        Uri sourceUri,
        string partialFilePath,
        string resumeFingerprint,
        IProgress<AudioDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ValidateSource(sourceUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(partialFilePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(resumeFingerprint);
        cancellationToken.ThrowIfCancellationRequested();

        var partialPath = Path.GetFullPath(partialFilePath);
        var directory = Path.GetDirectoryName(partialPath)
            ?? throw new ArgumentException("A download folder is required.", nameof(partialFilePath));
        Directory.CreateDirectory(directory);
        var metadataPath = partialPath + ".resume.json";

        var metadata = ReadMetadata(metadataPath);
        var hasPartial = File.Exists(partialPath);
        if (hasPartial && (metadata is null ||
            !string.Equals(metadata.Fingerprint, resumeFingerprint, StringComparison.Ordinal)))
        {
            // A new voucher means the previous encrypted bytes may no longer
            // correspond to the current AAXC key. Do not combine them.
            File.Delete(partialPath);
            TryDelete(metadataPath);
            metadata = null;
            hasPartial = false;
        }

        var existingLength = hasPartial ? new FileInfo(partialPath).Length : 0;
        if (existingLength > MaximumDownloadBytes)
            throw new InvalidDataException("The saved partial download exceeds VØRATH's 8 GiB safety limit.");

        using var request = new HttpRequestMessage(HttpMethod.Get, sourceUri);
        request.Headers.AcceptEncoding.Clear();
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("identity"));
        if (existingLength > 0)
        {
            request.Headers.Range = new RangeHeaderValue(existingLength, null);
            if (TryParseEntityTag(metadata?.ETag, out var etag))
                request.Headers.IfRange = new RangeConditionHeaderValue(etag);
        }

        using var response = await httpClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

        var finalUri = response.RequestMessage?.RequestUri ?? sourceUri;
        ValidateSource(finalUri);

        if (existingLength > 0 && response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            var serverLength = response.Content.Headers.ContentRange?.Length;
            if (serverLength is > 0 && serverLength.Value == existingLength)
                return new ResumableAudioFileResult(partialPath, existingLength, serverLength);

            File.Delete(partialPath);
            TryDelete(metadataPath);
            throw new IOException(
                "Audible's download server rejected the saved byte range. The partial file was preserved only until this retry; start the download again to obtain a clean copy.");
        }

        var append = existingLength > 0 && response.StatusCode == HttpStatusCode.PartialContent;
        long offset;
        if (append)
        {
            if (response.Content.Headers.ContentRange?.From != existingLength)
                throw new InvalidDataException("Audible returned an invalid HTTP Content-Range for the saved partial file.");

            var oldEtag = metadata?.ETag;
            var newEtag = response.Headers.ETag?.ToString();
            if (oldEtag is not null && newEtag is not null &&
                !string.Equals(oldEtag, newEtag, StringComparison.Ordinal))
                throw new InvalidDataException("Audible changed the media entity during a resumed transfer. The saved partial file was retained.");
            offset = existingLength;
        }
        else if (response.StatusCode == HttpStatusCode.OK)
        {
            // If-Range mismatch or a server that does not implement Range:
            // the 200 response is a new complete representation, so restart it.
            offset = 0;
        }
        else
        {
            throw new HttpRequestException(
                $"Audible's media server returned HTTP {(int)response.StatusCode} for the download.",
                null,
                response.StatusCode);
        }

        if (response.StatusCode == HttpStatusCode.PartialContent &&
            response.Content.Headers.ContentRange?.From != 0 && !append)
            throw new InvalidDataException("Audible returned an unexpected partial response for a new download.");

        var responseLength = response.Content.Headers.ContentRange?.Length;
        var contentLength = response.Content.Headers.ContentLength;
        long? totalLength = responseLength ?? (contentLength is { } length
            ? checked(offset + length)
            : null);

        if (totalLength is > MaximumDownloadBytes)
            throw new InvalidDataException("The Audible audiobook exceeds VØRATH's 8 GiB safety limit.");

        var newMetadata = new ResumeMetadata(
            resumeFingerprint,
            response.Headers.ETag?.ToString() ?? (append ? metadata?.ETag : null),
            totalLength);
        WriteMetadata(metadataPath, newMetadata);

        var mode = append ? FileMode.Append : FileMode.Create;
        long received = offset;
        var buffer = new byte[BufferSize];
        using (var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        using (var output = new FileStream(
            partialPath, mode, FileAccess.Write, FileShare.Read,
            BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            try
            {
                progress?.Report(new AudioDownloadProgress(received, totalLength));
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                        .ConfigureAwait(false);
                    if (read == 0)
                        break;

                    received = checked(received + read);
                    if (received > MaximumDownloadBytes)
                        throw new InvalidDataException("The Audible audiobook exceeded VØRATH's 8 GiB safety limit.");

                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    progress?.Report(new AudioDownloadProgress(received, totalLength));
                }

                await output.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }
            catch
            {
                // FileStream.Dispose also flushes buffered writes. The .part file
                // and sidecar deliberately remain so Pause/Retry can resume.
                try { output.Flush(flushToDisk: true); }
                catch (IOException) { }
                throw;
            }
        }

        if (totalLength is { } expected && received != expected)
            throw new EndOfStreamException(
                "Audible's download ended before all declared bytes arrived. The partial file was kept for resume.");

        return new ResumableAudioFileResult(partialPath, received, totalLength);
    }

    private static void ValidateSource(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            uri.AbsoluteUri.Length > 16 * 1024)
            throw new ArgumentException("Audible downloads require a bounded HTTPS URL.", nameof(uri));
    }

    private static ResumeMetadata? ReadMetadata(string path)
    {
        if (!File.Exists(path))
            return null;

        try
        {
            var json = File.ReadAllText(path);
            if (json.Length > 16 * 1024)
                return null;
            return JsonSerializer.Deserialize<ResumeMetadata>(json, JsonOptions);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static void WriteMetadata(string path, ResumeMetadata metadata)
    {
        var temporaryPath = path + ".tmp";
        var json = JsonSerializer.Serialize(metadata, JsonOptions);
        File.WriteAllText(temporaryPath, json);
        File.Move(temporaryPath, path, overwrite: true);
    }

    private static bool TryParseEntityTag(string? value, out EntityTagHeaderValue entityTag)
    {
        entityTag = default!;
        return value is not null && EntityTagHeaderValue.TryParse(value, out entityTag);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed record ResumeMetadata(string Fingerprint, string? ETag, long? TotalLength);
}

public sealed record ResumableAudioFileResult(
    string PartialFilePath,
    long BytesReceived,
    long? TotalBytes);
