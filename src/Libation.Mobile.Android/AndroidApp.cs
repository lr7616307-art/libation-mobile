using Android.OS;
using System.Globalization;
using Android.Runtime;
using Avalonia;
using Avalonia.Android;
using Dinah.Core;
using Libation.Mobile.Audible;
using Libation.Mobile.Core;
using Libation.Mobile.UI;
using Libation.Mobile.UI.Views;
using Libation.Mobile.UI.ViewModels;

namespace Libation.Mobile.Android;

[Application]
public class AndroidApp : AvaloniaAndroidApplication<App>
{
    protected AndroidApp(nint javaReference, JniHandleOwnership transfer)
        : base(javaReference, transfer)
    {
    }

    public override void OnCreate()
    {
        base.OnCreate();

        // Keep Android-only dependencies in the Android host. Both the complete
        // Audible identity and the minimum account session use encrypted storage.
        var secrets = new AndroidSecretStore(this);
        var snapshots = new AudibleIdentitySnapshotStore(secrets);
        // Audible registration metadata is independent of VØRATH's own app version.
        // Use the real Android build fingerprint and documented Android Audible profile values.
        var osVersion = Build.Fingerprint ?? "unknown";
        var androidRelease = Build.VERSION.Release ?? "unknown";
        var osVersionNumber = ((int)Build.VERSION.SdkInt).ToString(CultureInfo.InvariantCulture);
        var model = string.IsNullOrWhiteSpace(Build.Model) ? "Android device" : Build.Model;
        var manufacturer = string.IsNullOrWhiteSpace(Build.Manufacturer) ? "unknown" : Build.Manufacturer;
        var product = string.IsNullOrWhiteSpace(Build.Product) ? "unknown" : Build.Product;
        var buildId = string.IsNullOrWhiteSpace(Build.Id) ? "unknown" : Build.Id;
        var profile = new AudibleAndroidDeviceProfile(
            model,
            osVersion,
            AudibleAndroidDeviceProfile.AudibleAndroidAppVersion,
            AudibleAndroidDeviceProfile.AudibleAndroidSoftwareVersion,
            "Audible for Android",
            manufacturer,
            product,
            osVersionNumber,
            androidRelease,
            buildId);
        var registrationClient = new AudiblePkceRegistrationClient(new HttpClient());
        var authenticator = new AudibleAccountAuthenticator(
            snapshots, registrationClient, profile, new SystemDateTime());
        var connection = new AccountConnectionService(authenticator, new AccountSessionStore(secrets));
        var browser = new AndroidExternalBrowserLauncher(this);

        MainView.PlatformSignInViewModelFactory =
            () => new AudibleSignInViewModel(connection, browser);

        // The provider restores only the identity already stored on this device.
        // If the user has not signed in, sync reports that sign-in is required.
        MainView.PlatformLibraryCatalogProviderFactory =
            () => new SavedIdentityAudibleCatalogProvider(snapshots);
        MainView.PlatformLocalAudioPlayerFactory = () => new AndroidLocalAudioPlayer();
        MainView.PlatformWidevineDiagnosticFactory = () =>
        {
            var result = new AndroidWidevineCapabilityProbe().Probe();
            return result.IsSupported
                ? $"Widevine supported. Security level: {result.SecurityLevel ?? "not reported"}. {result.Diagnostic}"
                : $"Widevine not available. {result.Diagnostic}";
        };
    }
}
