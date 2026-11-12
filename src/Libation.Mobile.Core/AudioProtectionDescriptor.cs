namespace Libation.Mobile.Core;

/// <summary>
/// Describes the protection declared by the provider's granted download licence.
/// </summary>
public enum AudioProtectionKind
{
    Unprotected,
    AudibleAdrm,
    Widevine,
    UnknownProtected
}

/// <summary>
/// In-memory protection metadata. License secrets and voucher keys are never
/// stored in queue JSON or ordinary catalogue metadata.
/// </summary>
public sealed record AudioProtectionDescriptor(
    AudioProtectionKind Kind,
    string? ContentType = null)
{
    public static AudioProtectionDescriptor Unprotected { get; } =
        new(AudioProtectionKind.Unprotected);

    public bool RequiresProtectedMediaPipeline =>
        Kind != AudioProtectionKind.Unprotected;
}
