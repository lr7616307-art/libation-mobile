namespace Libation.Mobile.Core;

/// <summary>
/// Validates direct, public HTTPS audio URLs. URLs containing query parameters,
/// fragments or embedded credentials are rejected because queue metadata is
/// persisted in the app-private queue file.
/// </summary>
public static class AuthorizedAudioDownloadUrl
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".m4a", ".m4b", ".aac", ".flac", ".ogg", ".opus", ".wav"
    };

    public static Uri Parse(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 2048)
            throw new ArgumentException("Enter a direct HTTPS audio URL.", nameof(value));

        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Only direct HTTPS audio URLs are supported.", nameof(value));

        if (!string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException(
                "For privacy, use a public HTTPS audio URL without login credentials, query tokens or fragments.",
                nameof(value));

        var extension = Path.GetExtension(Uri.UnescapeDataString(uri.AbsolutePath));
        if (!SupportedExtensions.Contains(extension))
            throw new ArgumentException(
                "The direct URL must end in MP3, M4A, M4B, AAC, FLAC, OGG, OPUS or WAV.",
                nameof(value));

        return uri;
    }
}

/// <summary>
/// Resolves only the direct HTTPS URL explicitly supplied for a queue item.
/// It never probes Audible catalogue metadata or derives a media URL from an ASIN.
/// </summary>
public sealed class QueuedHttpsAudioDownloadSource : IAuthorizedAudioDownloadSource
{
    private readonly string _stagingDirectory;

    public QueuedHttpsAudioDownloadSource(string stagingDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingDirectory);
        _stagingDirectory = Path.GetFullPath(stagingDirectory);
    }

    public Task<AuthorizedAudioDownload> ResolveAsync(
        string audiobookId,
        CancellationToken cancellationToken = default)
        => throw new InvalidOperationException("A direct URL is required for this download queue item.");

    public Task<AuthorizedAudioDownload> ResolveAsync(
        DownloadQueueItem item,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(item.SourceUrl))
            throw new InvalidOperationException("No direct HTTPS audio URL was supplied for this queue item.");

        var uri = AuthorizedAudioDownloadUrl.Parse(item.SourceUrl);
        var extension = Path.GetExtension(Uri.UnescapeDataString(uri.AbsolutePath)).ToLowerInvariant();
        var idHash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(item.AudiobookId)))
            .ToLowerInvariant();

        Directory.CreateDirectory(_stagingDirectory);
        var stagingPath = Path.Combine(_stagingDirectory, idHash + extension + ".download");
        return Task.FromResult(new AuthorizedAudioDownload(uri, stagingPath));
    }
}
