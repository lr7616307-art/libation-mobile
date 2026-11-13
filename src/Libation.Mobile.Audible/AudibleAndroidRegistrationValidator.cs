namespace Libation.Mobile.Audible;

/// <summary>
/// Verifies that a saved Audible identity was registered using the exact
/// Android profile required by the pinned AudibleApi Widevine request path.
/// Older saved profiles remain readable for account recovery, but are never
/// silently sent through a Widevine request that AudibleApi will downgrade.
/// </summary>
public static class AudibleAndroidRegistrationValidator
{
    public static bool IsCurrentAndroidRegistration(AudibleIdentitySnapshot? snapshot)
        => snapshot is not null
            && string.Equals(
                snapshot.RegistrationProfileId,
                AudibleOAuthFlow.RegistrationProfileId,
                StringComparison.Ordinal)
            && string.Equals(
                snapshot.DeviceType,
                AudibleOAuthFlow.AndroidDeviceType,
                StringComparison.Ordinal);

    public static void EnsureCurrentAndroidRegistration(AudibleIdentitySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!IsCurrentAndroidRegistration(snapshot))
            throw new AudibleAndroidRegistrationRequiredException();
    }
}

/// <summary>
/// Signals that the saved account predates or differs from the current Android
/// Audible registration. Re-registering is a one-time account sign-in action,
/// not an approval request for each audiobook.
/// </summary>
public sealed class AudibleAndroidRegistrationRequiredException : InvalidOperationException
{
    public AudibleAndroidRegistrationRequiredException()
        : base("The saved Audible identity is not registered with the current Android device profile.") { }
}
