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

        // The foreground Widevine service and the Avalonia UI must mutate the
        // exact same queue instance and file, so status/progress cannot race.
        var queuePath = Path.Combine(
            global::System.Environment.GetFolderPath(global::System.Environment.SpecialFolder.LocalApplicationData),
            "download-queue.json");
        var sharedDownloadQueue = new JsonDownloadQueue(queuePath);
        MainView.PlatformDownloadQueueFactory = () => sharedDownloadQueue;
        AndroidWidevineOfflineDownloadService.DownloadQueueFactory = () => sharedDownloadQueue;
        MainView.PlatformDownloadNotificationStartAction =
            title => AndroidDownloadNotificationService.Start(this, title);
        MainView.PlatformDownloadNotificationUpdateAction =
            (title, detail, percent, indeterminate) =>
                AndroidDownloadNotificationService.Update(this, title, detail, percent, indeterminate);
        MainView.PlatformDownloadNotificationFinishAction =
            () => AndroidDownloadNotificationService.Finish(this);
        MainView.PlatformDownloadNotificationPausedAction =
            () => AndroidDownloadNotificationService.SetPaused(this);

        // Keep Android-only dependencies in the Android host. Both the complete
        // Audible identity and the minimum account session use encrypted storage.
        var secrets = new AndroidSecretStore(this);
        var snapshots = new AudibleIdentitySnapshotStore(secrets);
        // Registration metadata must describe this real Android host. Do not send
        // an emulator fingerprint, fabricated Pixel model, or guessed carrier.
        var displayMetrics = Resources?.DisplayMetrics;
        var deviceInfo = new AudibleAndroidDeviceInfo(
            Build.Manufacturer ?? "Unknown",
            Build.Brand ?? "Unknown",
            Build.Model ?? "Unknown",
            Build.Device ?? "Unknown",
            Build.Product ?? "Unknown",
            Build.Fingerprint ?? "Unknown",
            Build.VERSION.Release ?? "Unknown",
            ((int)Build.VERSION.SdkInt).ToString(System.Globalization.CultureInfo.InvariantCulture),
            $"{Build.Manufacturer} {Build.Model}".Trim(),
            System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName,
            displayMetrics?.WidthPixels ?? 0,
            displayMetrics?.HeightPixels ?? 0);
        var deviceSerialStore = new AudibleAndroidDeviceSerialStore(this);
        var registrationClient = new AudiblePkceRegistrationClient(new HttpClient());
        var tokenRefresher = new AudibleTokenRefreshClient(new HttpClient(), new SystemDateTime());
        var authenticator = new AudibleAccountAuthenticator(
            snapshots, registrationClient, tokenRefresher, new SystemDateTime(),
            () => deviceInfo, () => deviceSerialStore.GetOrCreate());
        var connection = new AccountConnectionService(authenticator, new AccountSessionStore(secrets));
        var browser = new AndroidExternalBrowserLauncher(this);

        // Share one live view model so account state changes after login, sync and
        // disconnect are reflected by the account tile without rebuilding the view.
        var signInViewModel = new AudibleSignInViewModel(
            connection,
            browser,
            async cancellationToken =>
            {
                var savedIdentity = await snapshots.LoadAsync(cancellationToken);
                return AudibleAndroidRegistrationValidator.IsCurrentAndroidRegistration(savedIdentity);
            });
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
        MainView.PlatformWidevinePlaybackCoordinatorFactory = async cancellationToken =>
        {
            var savedIdentity = await snapshots.LoadAsync(cancellationToken);
            if (savedIdentity is null)
                return null;

            // AudibleApi 14.1.2 silently downgrades a Widevine request to ADRM
            // unless the saved identity uses its Android device type. Stop here
            // with an actionable account-level fix instead of showing a vague
            // per-title licence failure.
            AudibleAndroidRegistrationValidator.EnsureCurrentAndroidRegistration(savedIdentity);

            if (string.IsNullOrWhiteSpace(savedIdentity.AmazonAccountId))
                throw new InvalidOperationException(
                    "The saved Audible identity does not contain the account identifier required for offline licence storage.");

            var session = await new AudibleApiSessionFactory(snapshots)
                .CreateSessionFromSavedIdentityAsync(cancellationToken);
            if (session is null)
                return null;

            var source = new AudibleAudioDownloadSource(
                session.Value.Api,
                session.Value.Locale,
                audibleDownloadDirectory);
            var manifestClient = new AudibleWidevineManifestClient(
                new HttpClient { Timeout = Timeout.InfiniteTimeSpan });
            var keySetStore = new AudibleOfflineLicenseKeySetStore(secrets);
            return new AndroidWidevineOfflinePlaybackCoordinator(
                source, manifestClient, keySetStore, savedIdentity.AmazonAccountId);
        };
        AndroidWidevineOfflineDownloadService.CoordinatorFactory =
            MainView.PlatformWidevinePlaybackCoordinatorFactory;
        MainView.PlatformAudibleOfflineDownloadStartAction =
            (asin, title) => AndroidWidevineOfflineDownloadService.Start(this, asin, title);
        MainView.PlatformAudibleOfflineDownloadPauseAction =
            (asin, title) => AndroidWidevineOfflineDownloadService.Pause(this, asin, title);
        signInViewModel.SetBeforeDisconnectCleanup(async cancellationToken =>
        {
            if (MainView.PlatformWidevinePlaybackCoordinatorFactory is null)
                return;

            var coordinator = await MainView.PlatformWidevinePlaybackCoordinatorFactory(cancellationToken);
            if (coordinator is not null)
                await coordinator.RemoveAllOfflineLicensesAsync(cancellationToken);
        });

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
