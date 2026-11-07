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
    private static readonly HttpClient DownloadHttpClient = new();

    /// <summary>Process-wide queue shared with the UI and Android foreground-service host.</summary>
    public static IDownloadQueue? PersistentDownloadQueue { get; private set; }

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
        var authenticator = new AudibleAccountAuthenticator(snapshots, new SystemDateTime());
        var connection = new AccountConnectionService(authenticator, new AccountSessionStore(secrets));
        var browser = new AndroidExternalBrowserLauncher(this);
        var dataDirectory = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        PersistentDownloadQueue = new JsonDownloadQueue(Path.Combine(dataDirectory, "download-queue.json"));

        MainView.PlatformSignInViewModelFactory =
            () => new AudibleSignInViewModel(connection, browser);

        // The provider restores only the identity already stored on this device.
        // If the user has not signed in, sync reports that sign-in is required.
        MainView.PlatformLibraryCatalogProviderFactory =
            () => new SavedIdentityAudibleCatalogProvider(snapshots);
        MainView.PlatformLocalAudioPlayerFactory = () => new AndroidLocalAudioPlayer();

        // One queue instance is shared by the UI and future foreground-service
        // composition. MainViewModel.InitializeAsync recovers interrupted items
        // from this same persisted queue when the app opens after process death.
        MainView.PlatformDownloadQueueFactory = () => PersistentDownloadQueue
            ?? throw new InvalidOperationException("The Android download queue has not been initialized.");

        // Resolve Audible credentials only when the user starts the queue. This
        // keeps startup independent of network access and allows a fresh sign-in
        // to be used without restarting the app.
        var audibleDownloadDirectory = Path.Combine(dataDirectory, "audible-downloads");
        var libraryRepository = new JsonLibraryRepository(Path.Combine(dataDirectory, "library.json"));
        AndroidDownloadQueueService.ControllerFactory = async () =>
        {
            var source = await AudibleAudioDownloadSource.FromSavedIdentityAsync(
                snapshots, audibleDownloadDirectory);
            if (source is null)
                return null;

            var queue = PersistentDownloadQueue
                ?? throw new InvalidOperationException("The Android download queue has not been initialized.");
            var worker = new DownloadQueueWorker(
                queue,
                new AudioFileDownloader(DownloadHttpClient),
                source,
                libraryRepository: libraryRepository);
            return new DownloadQueueExecutionController(worker);
        };
        MainView.PlatformDownloadQueueStartAction = () => AndroidDownloadQueueService.Start(this);
    }
}
