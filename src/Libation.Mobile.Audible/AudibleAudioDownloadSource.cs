using System.Text.RegularExpressions;
using AudibleApi;
using AudibleApi.Authorization;
using AudibleApi.Common;
using Libation.Mobile.Core;

namespace Libation.Mobile.Audible;

/// <summary>
/// Requests an Audible-issued offline licence for a library ASIN and returns only
/// the signed HTTPS media URL and the protection metadata granted by Audible.
/// It does not remove or bypass any DRM.
/// </summary>
public sealed class AudibleAudioDownloadSource : IAuthorizedAudioDownloadSource
{
    private static readonly Regex AsinPattern = new(
        "^[A-Za-z0-9]{10}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly Func<string, CancellationToken, Task<ContentLicense>> _requestLicense;
    private readonly string _downloadDirectory;

    public AudibleAudioDownloadSource(Api api, string downloadDirectory)
    {
        ArgumentNullException.ThrowIfNull(api);
        _requestLicense = (asin, _) => api.GetDownloadLicenseAsync(
            asin, DownloadQuality.High, ChapterTitlesType.Tree, DrmType.Adrm);
        ArgumentException.ThrowIfNullOrWhiteSpace(downloadDirectory);
        _downloadDirectory = Path.GetFullPath(downloadDirectory);
    }

    /// <summary>
    /// Restores the already-authenticated account. This method does not launch
    /// login or create a new device registration.
    /// </summary>
    public static async Task<AudibleAudioDownloadSource?> FromSavedIdentityAsync(
        AudibleIdentitySnapshotStore snapshotStore,
        string downloadDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshotStore);
        ArgumentException.ThrowIfNullOrWhiteSpace(downloadDirectory);
        var api = await new AudibleApiSessionFactory(snapshotStore)
            .CreateFromSavedIdentityAsync(cancellationToken)
            .ConfigureAwait(false);
        return api is null ? null : new AudibleAudioDownloadSource(api, downloadDirectory);
    }

    public async Task<AuthorizedAudioDownload> ResolveAsync(
        string audiobookId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(audiobookId) || !AsinPattern.IsMatch(audiobookId))
            throw new ArgumentException(
                "The Audible audiobook identifier is not a valid 10-character ASIN.",
                nameof(audiobookId));

        var license = await _requestLicense(audiobookId, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        if (license is null ||
            !string.Equals(license.StatusCode, "Granted", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "Audible did not grant an offline download licence for this audiobook.");

        var offlineUrl = license.ContentMetadata?.ContentUrl?.OfflineUrl;
        if (string.IsNullOrWhiteSpace(offlineUrl) ||
            !Uri.TryCreate(offlineUrl, UriKind.Absolute, out var downloadUri) ||
            !string.Equals(downloadUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Audible returned no valid HTTPS offline download URL.");

        var drmName = license.DrmType.ToString();
        var protectionKind = drmName switch
        {
            "Mpeg" => AudioProtectionKind.Unprotected,
            "Adrm" => AudioProtectionKind.AudibleAdrm,
            "Widevine" => AudioProtectionKind.Widevine,
            _ => AudioProtectionKind.UnknownProtected
        };
        var contentFormat = license.ContentMetadata?.ContentReference?.ContentFormat;
        var protection = new AudioProtectionDescriptor(protectionKind, contentFormat);

        // GetDownloadLicenseAsync can fall back to Audible's older AAX
        // delivery for titles that do not have an AAXC representation.
        // Keep the original container extension accurate; neither container
        // is attached to VØRATH's normal audio player while it remains protected.
        var extension = drmName switch
        {
            "Mpeg" => ".mp3",
            "Adrm" when contentFormat?.Contains("AAXC", StringComparison.OrdinalIgnoreCase) == true => ".aaxc",
            "Adrm" when contentFormat?.Contains("AAX", StringComparison.OrdinalIgnoreCase) == true => ".aax",
            _ => ".protected"
        };

        Directory.CreateDirectory(_downloadDirectory);
        var destinationPath = Path.Combine(
            _downloadDirectory,
            audiobookId.ToUpperInvariant() + extension);

        return new AuthorizedAudioDownload(downloadUri, destinationPath)
        {
            Protection = protection
        };
    }
}
