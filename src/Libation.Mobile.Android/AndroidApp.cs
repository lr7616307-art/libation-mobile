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
        StartupDiagnostics.Install(this);
        try
        {
            base.OnCreate();

            // Keep Android-only dependencies in the Android host. Both the complete
            // Audible identity and the minimum account session use encrypted storage.
            var secrets = new AndroidSecretStore(this);
            var snapshots = new AudibleIdentitySnapshotStore(secrets);
            var authenticator = new AudibleAccountAuthenticator(snapshots, new SystemDateTime());
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
        catch (Exception exception)
        {
            // Do not rethrow here. Android will not create MainActivity when
            // Application.OnCreate throws, so the saved diagnostic could never
            // be shown. MainActivity catches any follow-on Avalonia startup error
            // and renders the native diagnostic screen.
            StartupDiagnostics.Record(this, "AndroidApp.OnCreate", exception);
        }
    }
}
