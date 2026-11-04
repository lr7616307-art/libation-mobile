namespace Libation.Mobile.Core;

/// <summary>
/// Streams an audio file from a provider-issued HTTPS URL to local storage.
/// This component does not discover URLs, authenticate to a store, or decrypt media.
/// </summary>
public sealed class AudioFileDownloader
{
    private readonly HttpClient _httpClient;

    public AudioFileDownloader(HttpClient httpClient)
        => _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));

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
        var temporaryPath = fullPath + "." + Guid.NewGuid().ToString("N") + ".part";

        try
        {
            using var response = await _httpClient.GetAsync(
                authorizedDownloadUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            // Do not follow a provider redirect from HTTPS to an insecure transport.
            if (response.RequestMessage?.RequestUri is { } finalUri &&
                !string.Equals(finalUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                throw new HttpRequestException("The download redirected to a non-HTTPS address.");

            var totalBytes = response.Content.Headers.ContentLength;
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var output = new FileStream(
                temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 64 * 1024, useAsync: true);

            var buffer = new byte[64 * 1024];
            long received = 0;
            while (true)
            {
                var read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                if (read == 0) break;
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                received += read;
                progress?.Report(new AudioDownloadProgress(received, totalBytes));
            }

            await output.FlushAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, fullPath, overwrite: true);
            return new AudioDownloadResult(fullPath, received);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
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
