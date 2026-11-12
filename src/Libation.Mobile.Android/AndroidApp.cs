using Android.OS;
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
        // Follow LibriSync's current Audible registration profile. This is a provider
        // registration choice and intentionally does not fabricate Android build details.
        var registrationClient = new AudiblePkceRegistrationClient(new HttpClient());
        var tokenRefresher = new AudibleTokenRefreshClient(new HttpClient(), new SystemDateTime());
        var authenticator = new AudibleAccountAuthenticator(
            snapshots, registrationClient, tokenRefresher, new SystemDateTime());
        var connection = new AccountConnectionService(authenticator, new AccountSessionStore(secrets));
        var browser = new AndroidExternalBrowserLauncher(this);

        // Share one live view model so account state changes after login, sync and
        // disconnect are reflected by the account tile without rebuilding the view.
        var signInViewModel = new AudibleSignInViewModel(connection, browser);
        MainView.PlatformSignInViewModelFactory = () => signInViewModel;

        // The provider restores only the identity already stored on this device.
        // If the user has not signed in, sync reports that sign-in is required.
        MainView.PlatformLibraryCatalogProviderFactory =
            () => new SavedIdentityAudibleCatalogProvider(snapshots);
        MainView.PlatformAudiobookDetailsProviderFactory =
            () => new SavedIdentityAudibleBookDetailsProvider(snapshots);
        MainView.PlatformOpenAudibleTitleFactory =
            asin => AndroidAudibleBookLauncher.OpenAsync(this, asin, signInViewModel.Marketplace);
        var audibleDownloadDirectory = Path.Combine(
            global::System.Environment.GetFolderPath(global::System.Environment.SpecialFolder.LocalApplicationData),
            "audible-downloads");
        MainView.PlatformAudibleDownloadSourceFactory = async (quality, cancellationToken) =>
        {
            var source = await AudibleAudioDownloadSource.FromSavedIdentityAsync(
                snapshots, audibleDownloadDirectory, quality, cancellationToken);
            return (IAuthorizedAudioDownloadSource?)source;
        };
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
