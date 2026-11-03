using Android.Media;
using Java.Util;

namespace Libation.Mobile.Android;

/// <summary>
/// Reports whether this Android device exposes the Widevine DRM crypto scheme.
/// This is a capability probe only; it does not request licenses, open protected
/// content, or extract keys.
/// </summary>
public sealed class AndroidWidevineCapabilityProbe
{
    private static readonly UUID WidevineUuid =
        UUID.FromString("edef8ba9-79d6-4ace-a3c8-27dcd51d21ed");

    /// <summary>
    /// Checks the platform's Widevine support and, when available, reads the
    /// security-level property reported by MediaDrm.
    /// </summary>
    public WidevineCapabilityResult Probe()
    {
        try
        {
            if (!MediaDrm.IsCryptoSchemeSupported(WidevineUuid))
            {
                return new WidevineCapabilityResult(
                    IsSupported: false,
                    SecurityLevel: null,
                    Diagnostic: "Widevine is not reported as a supported crypto scheme.");
            }

            using var drm = new MediaDrm(WidevineUuid);
            string? securityLevel = null;

            try
            {
                securityLevel = drm.GetPropertyString("securityLevel");
            }
            catch (Java.Lang.Exception)
            {
                // Some platform implementations may support Widevine without
                // exposing this optional diagnostic property.
            }

            return new WidevineCapabilityResult(
                IsSupported: true,
                SecurityLevel: string.IsNullOrWhiteSpace(securityLevel) ? null : securityLevel,
                Diagnostic: securityLevel is null
                    ? "Widevine is supported; the platform did not expose a security level."
                    : "Widevine support was reported by Android MediaDrm.");
        }
        catch (Java.Lang.Exception ex)
        {
            return new WidevineCapabilityResult(
                IsSupported: false,
                SecurityLevel: null,
                Diagnostic: $"Android MediaDrm could not be initialized ({ex.GetType().Name}).");
        }
        catch (NotSupportedException ex)
        {
            return new WidevineCapabilityResult(
                IsSupported: false,
                SecurityLevel: null,
                Diagnostic: $"Widevine probing is not supported ({ex.GetType().Name}).");
        }
    }
}

/// <summary>
/// A non-sensitive snapshot of Android's reported Widevine capability.
/// </summary>
public sealed record WidevineCapabilityResult(
    bool IsSupported,
    string? SecurityLevel,
    string Diagnostic);
