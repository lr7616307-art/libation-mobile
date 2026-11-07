namespace Libation.Mobile.Core;

/// <summary>
/// Describes the protection mode and non-secret lifecycle metadata declared by
/// the provider's authorized download licence.
/// </summary>
public enum AudioProtectionKind
{
    /// <summary>The provider explicitly delivered an unprotected media representation.</summary>
    Unprotected,
    /// <summary>Audible ADRM/AAXC protected media; requires a supported authorized processing path.</summary>
    AudibleAdrm,
    /// <summary>Widevine-protected media; requires a platform Widevine playback/processing path.</summary>
    Widevine,
    /// <summary>Protected media whose scheme is not recognized by this app version.</summary>
    UnknownProtected
}

/// <summary>
/// In-memory description of provider licence metadata. Do not serialize licence
/// secrets, voucher keys, challenges, or tokens into the download queue.
/// </summary>
public sealed record AudioProtectionDescriptor(
    AudioProtectionKind Kind,
    string? ContentType = null,
    DateTimeOffset? LicenceExpiresAtUtc = null,
    DateTimeOffset? LicenceRefreshAtUtc = null,
    DateTimeOffset? LicenceRemovalAtUtc = null)
{
    public static AudioProtectionDescriptor Unprotected { get; } =
        new(AudioProtectionKind.Unprotected);

    public bool RequiresProtectedMediaPipeline =>
        Kind != AudioProtectionKind.Unprotected;
}
