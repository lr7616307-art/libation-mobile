using System.Security.Cryptography;
using System.Text;

namespace Libation.Mobile.Core;

/// <summary>Downloads trusted Audible cover thumbnails into app-private, offline cache storage.</summary>
public sealed class CoverArtDownloadService
{
    public const long DefaultMaximumBytes = 5L * 1024 * 1024;
    private const int BufferSize = 16 * 1024;
    private static readonly string[] Extensions = [".jpg", ".png", ".webp"];
    private readonly HttpClient _httpClient;
    private readonly string _storageDirectory;
    private readonly long _maximumBytes;

    public CoverArtDownloadService(HttpClient httpClient, string storageDirectory, long maximumBytes = DefaultMaximumBytes)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        ArgumentException.ThrowIfNullOrWhiteSpace(storageDirectory);
        if (maximumBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        _storageDirectory = Path.GetFullPath(storageDirectory);
        _maximumBytes = maximumBytes;
    }

    public async Task<string?> DownloadAsync(string audiobookId, string? coverUrl, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(audiobookId);
        if (!TryGetTrustedUri(coverUrl, out var uri)) return null;

        var cacheIdentity = audiobookId + "\\n" + uri.AbsoluteUri;
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(cacheIdentity))).ToLowerInvariant();
        Directory.CreateDirectory(_storageDirectory);
        foreach (var extension in Extensions)
        {
            var cached = Path.Combine(_storageDirectory, key + extension);
            if (File.Exists(cached) && new FileInfo(cached).Length > 0) return cached;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        using var response = await _httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        if (response.RequestMessage?.RequestUri is not { } finalUri ||
            !string.Equals(finalUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(finalUri.Host, "m.media-amazon.com", StringComparison.OrdinalIgnoreCase))
            throw new HttpRequestException("The cover image redirected outside the approved Audible image host.");

        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (mediaType is not ("image/jpeg" or "image/png" or "image/webp"))
            throw new InvalidDataException("The cover response is not a supported image type.");
        if (response.Content.Headers.ContentLength is long length && length > _maximumBytes)
            throw new InvalidDataException("The cover image exceeds the configured size limit.");

        await using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
        var temporary = Path.Combine(_storageDirectory, key + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            var header = new byte[12];
            var headerLength = 0;
            long total = 0;
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, useAsync: true))
            {
                var buffer = new byte[BufferSize];
                while (true)
                {
                    var read = await input.ReadAsync(buffer.AsMemory(), timeout.Token);
                    if (read == 0) break;
                    if (read > _maximumBytes - total) throw new InvalidDataException("The cover image exceeds the configured size limit.");
                    if (headerLength < header.Length)
                    {
                        var copy = Math.Min(read, header.Length - headerLength);
                        buffer.AsSpan(0, copy).CopyTo(header.AsSpan(headerLength));
                        headerLength += copy;
                    }
                    await output.WriteAsync(buffer.AsMemory(0, read), timeout.Token);
                    total += read;
                }
                if (total == 0) throw new InvalidDataException("The cover image is empty.");
                await output.FlushAsync(timeout.Token);
            }

            var extension = GetImageExtension(header.AsSpan(0, headerLength));
            if (extension is null || !string.Equals(mediaType, GetMediaType(extension), StringComparison.Ordinal))
                throw new InvalidDataException("The cover image content does not match its declared image type.");

            timeout.Token.ThrowIfCancellationRequested();
            var destination = Path.Combine(_storageDirectory, key + extension);
            File.Move(temporary, destination, overwrite: true);
            return destination;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public static bool IsTrustedUrl(string? value) => TryGetTrustedUri(value, out _);

    private static bool TryGetTrustedUri(string? value, out Uri uri)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var candidate) &&
            string.Equals(candidate.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(candidate.Host, "m.media-amazon.com", StringComparison.OrdinalIgnoreCase) &&
            string.IsNullOrEmpty(candidate.UserInfo))
        {
            uri = candidate;
            return true;
        }
        uri = null!;
        return false;
    }

    private static string? GetImageExtension(ReadOnlySpan<byte> header)
    {
        if (header.Length >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF) return ".jpg";
        if (header.Length >= 8 && header[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })) return ".png";
        if (header.Length >= 12 && header[..4].SequenceEqual("RIFF"u8) && header[8..12].SequenceEqual("WEBP"u8)) return ".webp";
        return null;
    }

    private static string GetMediaType(string extension) => extension switch
    {
        ".jpg" => "image/jpeg",
        ".png" => "image/png",
        ".webp" => "image/webp",
        _ => string.Empty
    };
}
