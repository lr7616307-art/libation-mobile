using System.Text.RegularExpressions;
using AudibleApi;
using AudibleApi.Authorization;
using AudibleApi.Common;
using Dinah.Core;
using Libation.Mobile.Core;

namespace Libation.Mobile.Audible;

/// <summary>
/// Resolves Audible library ASINs through AudibleApi's authorized offline
/// licence request. It only returns the provider-issued HTTPS URL and protection
/// metadata; it does not decrypt or transform protected media.
/// </summary>
public sealed class AudibleAudioDownloadSource : IAuthorizedAudioDownloadSource
{
    private static readonly Regex AsinPattern = new("^[A-Za-z0-9]{10}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
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

    /// <summary>Injectable licence boundary for deterministic tests; production uses AudibleApi above.</summary>
    public AudibleAudioDownloadSource(
        Func<string, CancellationToken, Task<ContentLicense>> requestLicense,
        string downloadDirectory)
    {
        _requestLicense = requestLicense ?? throw new ArgumentNullException(nameof(requestLicense));
        ArgumentException.ThrowIfNullOrWhiteSpace(downloadDirectory);
        _downloadDirectory = Path.GetFullPath(downloadDirectory);
    }

    /// <summary>
    /// Restores the already-saved identity and prepares an authorized download
    /// source. This does not initiate login or create a device registration.
    /// </summary>
    public static async Task<AudibleAudioDownloadSource?> FromSavedIdentityAsync(
        AudibleIdentitySnapshotStore snapshotStore,
        string downloadDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshotStore);
        ArgumentException.ThrowIfNullOrWhiteSpace(downloadDirectory);
        cancellationToken.ThrowIfCancellationRequested();

        var snapshot = await snapshotStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (snapshot is null)
            return null;

        cancellationToken.ThrowIfCancellationRequested();
        var identity = AudibleIdentitySnapshotMapper.Restore(snapshot);
        var authorize = new Authorize(identity.Locale);
        var maintainer = new AudibleIdentityMaintainer(
            identity,
            authorize,
            new SystemDateTime(),
            snapshotStore);
        return new AudibleAudioDownloadSource(new Api(maintainer), downloadDirectory);
    }

    public async Task<AuthorizedAudioDownload> ResolveAsync(
        string audiobookId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(audiobookId) || !AsinPattern.IsMatch(audiobookId))
            throw new ArgumentException("The Audible audiobook identifier must be a valid 10-character ASIN.", nameof(audiobookId));

        // ADRM is explicitly requested because this app does not yet have a
        // supported Widevine CDM/player integration. AudibleApi remains
        // responsible for authenticating and requesting the provider licence.
        // The official package does not accept a CancellationToken for this API call.
        var license = await _requestLicense(audiobookId, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        if (license is null)
            throw new InvalidOperationException("Audible returned no offline download licence.");
        if (!string.Equals(license.StatusCode, "Granted", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Audible did not grant an offline download licence for this audiobook.");

        var offlineUrl = license.ContentMetadata?.ContentUrl?.OfflineUrl;
        if (string.IsNullOrWhiteSpace(offlineUrl) ||
            !Uri.TryCreate(offlineUrl, UriKind.Absolute, out var downloadUri) ||
            !string.Equals(downloadUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Audible returned no valid HTTPS offline download URL.");

        var contentMetadata = license.ContentMetadata
            ?? throw new InvalidDataException("Audible granted the licence without content metadata.");
        var contentReference = contentMetadata.ContentReference
            ?? throw new InvalidDataException("Audible granted the licence without a content reference.");

        if (!string.Equals(contentReference.Asin, audiobookId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Audible returned a download licence for a different audiobook.");

        var contentType = contentReference.ContentFormat;
        var protectionKind = license.DrmType.ToString() switch
        {
            "Mpeg" => AudioProtectionKind.Unprotected,
            "Adrm" => AudioProtectionKind.AudibleAdrm,
            "Widevine" => AudioProtectionKind.Widevine,
            _ => AudioProtectionKind.UnknownProtected
        };

        var protection = new AudioProtectionDescriptor(
            protectionKind,
            string.IsNullOrWhiteSpace(contentType) ? null : contentType,
            ToOptionalUtc(license.AccessExpiryDate),
            ToOptionalUtc(license.RefreshDate),
            ToOptionalUtc(license.RemovalDate));

        // The provider can grant an unencrypted MPEG representation even when
        // ADRM was requested. Keep the on-disk extension aligned with the
        // granted representation; never label MPEG bytes as an AAXC container.
        // Unknown protected formats get a neutral extension until a dedicated
        // authorized processor understands their container.
        var extension = license.DrmType switch
        {
            DrmType.Mpeg => ".mp3",
            DrmType.Adrm => ".aaxc",
            _ => ".protected"
        };

        // ASIN validation above prevents path traversal. Keep the original
        // provider bytes separate from any future processed/playable output.
        var destinationPath = Path.Combine(_downloadDirectory, audiobookId.ToUpperInvariant() + extension);
        return new AuthorizedAudioDownload(downloadUri, destinationPath)
        {
            Protection = protection
        };
    }

    private static DateTimeOffset? ToOptionalUtc(DateTimeOffset value) =>
        value == default ? null : value.ToUniversalTime();
}
