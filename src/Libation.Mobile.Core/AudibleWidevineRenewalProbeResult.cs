namespace Libation.Mobile.Core;

/// <summary>
/// Safe diagnostic summary of an Audible Widevine renewal response. The
/// opaque DRM licence and challenge are intentionally excluded.
/// </summary>
public sealed record AudibleWidevineRenewalProbeResult(
    string Asin,
    string? StatusCode,
    bool LicensePayloadPresent,
    bool LicenseIdentifierPresent,
    string? LicenseRefreshDate,
    string? LicenseRemovalDate,
    string? LicenseExpirationDate,
    string? ErrorMessage);
