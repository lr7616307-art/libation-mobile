using System.Text.RegularExpressions;
using AudibleApi;
using AudibleApi.Authorization;
using AudibleApi.Common;
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
    private readonly Api _api;
    private readonly string _downloadDirectory;

    public AudibleAudioDownloadSource(Api api, string downloadDirectory)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
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
        var license = await _api.GetDownloadLicenseAsync(
            audiobookId,
            DownloadQuality.High,
            ChapterTitlesType.Tree,
            DrmType.Adrm).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();

        if (license is null || !string.Equals(
            license.StatusCode.ToString(), "Granted", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Audible did not grant an offline download licence for this audiobook.");

        var offlineUrl = license.ContentMetadata?.ContentUrl?.OfflineUrl;
        if (string.IsNullOrWhiteSpace(offlineUrl) ||
            !Uri.TryCreate(offlineUrl, UriKind.Absolute, out var downloadUri) ||
            !string.Equals(downloadUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Audible returned no valid HTTPS offline download URL.");

        var protection = license.DrmType.ToString() switch
        {
            "Mpeg" => AudioProtectionDescriptor.Unprotected,
            "Adrm" => new AudioProtectionDescriptor(AudioProtectionKind.AudibleAdrm),
            "Widevine" => new AudioProtectionDescriptor(AudioProtectionKind.Widevine),
            _ => new AudioProtectionDescriptor(AudioProtectionKind.UnknownProtected)
        };

        // ASIN validation above prevents path traversal. Keep the original
        // provider bytes separate from any future processed/playable output.
        var destinationPath = Path.Combine(_downloadDirectory, audiobookId.ToUpperInvariant() + ".aaxc");
        return new AuthorizedAudioDownload(downloadUri, destinationPath)
        {
            Protection = protection
        };
    }
}
