using System.Text.RegularExpressions;
using System.Net;
using Newtonsoft.Json.Linq;
using AudibleApi;
using AudibleApi.Authorization;
using AudibleApi.Common;
using Libation.Mobile.Core;

namespace Libation.Mobile.Audible;

/// <summary>
/// A provider-authorized Widevine DASH manifest reference. This is not a downloaded
/// media file and must only be opened by a DRM-capable platform media pipeline.
/// </summary>
public sealed record AudibleWidevineContent(Uri ManifestUri, string? ContentFormat);

/// <summary>
/// Requests Audible-issued content licences and exposes Audible's authenticated
/// Widevine licence exchange. Protected media remains protected; this class does
/// not extract keys, decrypt streams, or bypass DRM.
/// </summary>
public sealed class AudibleAudioDownloadSource : IAuthorizedAudioDownloadSource
{
    private static readonly Regex AsinPattern = new(
        "^[A-Za-z0-9]{10}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly Func<string, DownloadQuality, DrmType, CancellationToken, Task<ContentLicense>> _requestLicense;
    private readonly Func<string, string, CancellationToken, Task<string>> _requestWidevineLicense;
    private readonly bool _widevineLicenseExchangeEnabled;
    private readonly string _downloadDirectory;

    public AudibleAudioDownloadSource(
        Api api,
        Locale locale,
        string downloadDirectory,
        AudibleDownloadQuality quality = AudibleDownloadQuality.High)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(locale);
        var apiQuality = quality == AudibleDownloadQuality.Normal
            ? DownloadQuality.Normal
            : DownloadQuality.High;
        _requestLicense = (asin, requestedQuality, drmType, _) =>
            api.GetDownloadLicenseAsync(asin, requestedQuality, ChapterTitlesType.Tree, drmType);
        // Use AudibleApi's public authenticated transport directly. Its
        // WidevineDrmLicense convenience method logs exceptions with the full
        // JSON request body, which can contain the DRM challenge. This path
        // intentionally emits no request/response body in diagnostics.
        _requestWidevineLicense = (asin, challenge, cancellationToken) =>
            RequestWidevineLicenseSafelyAsync(api, locale, asin, challenge, cancellationToken);
        _widevineLicenseExchangeEnabled = true;
        _downloadDirectory = NormalizeDownloadDirectory(downloadDirectory);
        _quality = apiQuality;
    }

    private readonly DownloadQuality _quality;

    /// <summary>
    /// Injectable adapter seam for deterministic tests. Production callers should
    /// use the Api constructor so requests retain AudibleApi's authenticated session.
    /// </summary>
    public AudibleAudioDownloadSource(
        Func<string, DownloadQuality, DrmType, CancellationToken, Task<ContentLicense>> requestLicense,
        Func<string, string, CancellationToken, Task<string>> requestWidevineLicense,
        string downloadDirectory,
        AudibleDownloadQuality quality = AudibleDownloadQuality.High)
    {
        _requestLicense = requestLicense ?? throw new ArgumentNullException(nameof(requestLicense));
        _requestWidevineLicense = requestWidevineLicense ?? throw new ArgumentNullException(nameof(requestWidevineLicense));
        _widevineLicenseExchangeEnabled = true;
        _downloadDirectory = NormalizeDownloadDirectory(downloadDirectory);
        _quality = quality == AudibleDownloadQuality.Normal ? DownloadQuality.Normal : DownloadQuality.High;
    }

    private static string NormalizeDownloadDirectory(string downloadDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(downloadDirectory);
        return Path.GetFullPath(downloadDirectory);
    }

    /// <summary>
    /// Restores the already-authenticated account. This method does not launch
    /// login or create a new device registration.
    /// </summary>
    public static async Task<AudibleAudioDownloadSource?> FromSavedIdentityAsync(
        AudibleIdentitySnapshotStore snapshotStore,
        string downloadDirectory,
        AudibleDownloadQuality quality = AudibleDownloadQuality.High,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshotStore);
        ArgumentException.ThrowIfNullOrWhiteSpace(downloadDirectory);
        var session = await new AudibleApiSessionFactory(snapshotStore)
            .CreateSessionFromSavedIdentityAsync(cancellationToken)
            .ConfigureAwait(false);
        return session is null
            ? null
            : new AudibleAudioDownloadSource(
                session.Value.Api, session.Value.Locale, downloadDirectory, quality);
    }

    public Task<AuthorizedAudioDownload> ResolveAsync(
        string audiobookId,
        CancellationToken cancellationToken = default)
        => ResolveWithDrmAsync(audiobookId, DrmType.Adrm, cancellationToken);

    /// <summary>
    /// Requests a Widevine content licence specifically. AudibleApi may downgrade
    /// the requested DRM type when the saved device registration is not its
    /// supported Android Audible profile; such a response is rejected here.
    /// </summary>
    public async Task<AudibleWidevineContent> ResolveWidevineAsync(
        string audiobookId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateAsin(audiobookId);

        var license = await _requestLicense(audiobookId, _quality, DrmType.Widevine, cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        if (license is null ||
            !string.Equals(license.StatusCode, "Granted", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "Audible did not grant a Widevine content licence for this audiobook.");

        if (!string.Equals(license.DrmType.ToString(), "Widevine", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException(
                "Audible did not issue a Widevine licence for this device registration. No DRM fallback was used.");

        // For Widevine, AudibleApi's LicenseResponse is the DASH manifest URL.
        // The separate WidevineDrmLicense call exchanges the platform-generated
        // MediaDrm challenge; this URL is not an offline audio-file URL.
        var manifest = license.LicenseResponse;
        if (string.IsNullOrWhiteSpace(manifest) ||
            !Uri.TryCreate(manifest, UriKind.Absolute, out var manifestUri) ||
            !string.Equals(manifestUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Audible returned no valid HTTPS Widevine manifest URL.");

        return new AudibleWidevineContent(
            manifestUri,
            license.ContentMetadata?.ContentReference?.ContentFormat);
    }

    private async Task<AuthorizedAudioDownload> ResolveWithDrmAsync(
        string audiobookId,
        DrmType requestedDrm,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateAsin(audiobookId);

        var license = await _requestLicense(audiobookId, _quality, requestedDrm, cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        if (license is null ||
            !string.Equals(license.StatusCode, "Granted", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "Audible did not grant an offline download licence for this audiobook.");

        var grantedDrm = license.DrmType.ToString();
        var offlineUrl = license.ContentMetadata?.ContentUrl?.OfflineUrl;
        if (string.IsNullOrWhiteSpace(offlineUrl) ||
            !Uri.TryCreate(offlineUrl, UriKind.Absolute, out var downloadUri) ||
            !string.Equals(downloadUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Audible returned no valid HTTPS offline download URL.");

        var protectionKind = grantedDrm switch
        {
            "Mpeg" => AudioProtectionKind.Unprotected,
            "Adrm" => AudioProtectionKind.AudibleAdrm,
            "Widevine" => AudioProtectionKind.Widevine,
            _ => AudioProtectionKind.UnknownProtected
        };
        var contentFormat = license.ContentMetadata?.ContentReference?.ContentFormat;
        var protection = new AudioProtectionDescriptor(protectionKind, contentFormat);

        var extension = grantedDrm switch
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

    /// <summary>
    /// Exchanges the exact challenge string generated by the platform DRM session
    /// with Audible's authenticated Widevine endpoint. The challenge and response
    /// are transient values: callers must not log or persist them.
    /// </summary>
    public async Task<string> RequestWidevineLicenseAsync(
        string audiobookId,
        string licenseChallenge,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateAsin(audiobookId);
        ArgumentException.ThrowIfNullOrWhiteSpace(licenseChallenge);

        if (!_widevineLicenseExchangeEnabled)
            throw new NotSupportedException(
                "Audible Widevine licence exchange is disabled until the upstream challenge-logging issue is fixed.");

        var response = await _requestWidevineLicense(audiobookId, licenseChallenge, cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(response))
            throw new InvalidDataException("Audible returned an empty Widevine licence response.");

        return response;
    }

    private static async Task<string> RequestWidevineLicenseSafelyAsync(
        Api api,
        Locale locale,
        string asin,
        string challenge,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var requestBody = new JObject
        {
            { "consumption_type", "Download" },
            { "drm_type", "Widevine" },
            { "tenant_id", "Audible" },
            { "licenseChallenge", challenge }
        };

        // This mirrors the pinned AudibleApi endpoint contract but deliberately
        // avoids its WidevineDrmLicense wrapper, whose error logs include the
        // request JObject. AdHocAuthenticatedRequestAsync applies the same ADP
        // and bearer authentication using the restored identity.
        using var response = await api.AdHocAuthenticatedRequestAsync(
            $"/1.0/content/{asin}/drmlicense",
            HttpMethod.Post,
            api.Sharer.GetSharedHttpClient(locale.AudibleApiUri()),
            requestBody).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();

        if (response.StatusCode != HttpStatusCode.OK)
            throw new HttpRequestException(
                $"Audible Widevine licence request failed with HTTP status {(int)response.StatusCode}.");

        var responseJson = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        JObject parsed;
        try
        {
            parsed = JObject.Parse(responseJson);
        }
        catch (Newtonsoft.Json.JsonException)
        {
            throw new InvalidDataException("Audible returned an invalid Widevine licence response.");
        }

        var license = parsed["license"]?.Value<string>();
        if (string.IsNullOrWhiteSpace(license))
            throw new InvalidDataException("Audible returned no Widevine licence value.");

        return license;
    }

    private static void ValidateAsin(string audiobookId)
    {
        if (string.IsNullOrWhiteSpace(audiobookId) || !AsinPattern.IsMatch(audiobookId))
            throw new ArgumentException(
                "The Audible audiobook identifier is not a valid 10-character ASIN.",
                nameof(audiobookId));
    }
}
