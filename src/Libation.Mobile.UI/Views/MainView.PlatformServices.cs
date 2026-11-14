using Avalonia.Controls;
using Libation.Mobile.Core;
using Libation.Mobile.UI.ViewModels;

namespace Libation.Mobile.UI.Views;

public interface IAudibleWidevinePlaybackCoordinator : IAudibleOfflineDownloadBackend
{
    Task<AudibleWidevineRenewalProbeResult> ProbeOfflineLicenseRenewalAsync(string asin, CancellationToken cancellationToken = default);
    Task AcquireOfflineLicenseAsync(string asin, CancellationToken cancellationToken = default);
    Task DownloadOfflineMediaAsync(string asin, CancellationToken cancellationToken = default);
    Task PlayAsync(string asin, string title, long startPositionMilliseconds = 0, bool autoPlay = true, CancellationToken cancellationToken = default);
    Task RemoveOfflineLicenseAsync(string asin, CancellationToken cancellationToken = default);
    Task RemoveAllOfflineLicensesAsync(CancellationToken cancellationToken = default);
}

public partial class MainView
{
    /// <summary>Starts the Android foreground notification for the regular ADRM download queue.</summary>
    public static Action<string>? PlatformDownloadNotificationStartAction { get; set; }

    /// <summary>Updates the active download notification with bytes, speed and ETA.</summary>
    public static Action<string, string, int?, bool>? PlatformDownloadNotificationUpdateAction { get; set; }

    /// <summary>Stops the foreground download notification after the queue finishes.</summary>
    public static Action? PlatformDownloadNotificationFinishAction { get; set; }

    /// <summary>Keeps a paused queue visible in notifications with a Resume action.</summary>
    public static Action? PlatformDownloadNotificationPausedAction { get; set; }

    /// <summary>Notification action callbacks routed to the same ADRM queue worker as the UI.</summary>
    public static Action? PlatformDownloadQueuePauseAction { get; set; }
    public static Action? PlatformDownloadQueueResumeAction { get; set; }

    /// <summary>Creates an authorized Widevine offline-licence/playback coordinator on demand.</summary>
    public static Func<CancellationToken, Task<IAudibleWidevinePlaybackCoordinator?>>? PlatformWidevinePlaybackCoordinatorFactory { get; set; }

    /// <summary>Starts the provider-authorized offline download in Android's foreground service.</summary>
    public static Action<string, string>? PlatformAudibleOfflineDownloadStartAction { get; set; }

    /// <summary>Called after the foreground service verifies the offline licence and all DASH byte ranges.</summary>
    public static Action<string>? PlatformWidevineOfflineDownloadCompletedAction { get; set; }

    /// <summary>Set by the platform host before Avalonia creates the main view.</summary>
    public static Func<AudibleSignInViewModel>? PlatformSignInViewModelFactory { get; set; }

    /// <summary>
    /// Supplies a provider that restores the already-saved identity on demand.
    /// The shared UI remains usable without platform-specific account services.
    /// </summary>
    public static Func<ILibraryCatalogProvider>? PlatformLibraryCatalogProviderFactory { get; set; }

    /// <summary>Runs a non-invasive, platform-specific Widevine capability diagnostic.</summary>
    public static Func<string>? PlatformWidevineDiagnosticFactory { get; set; }

    /// <summary>Shares the app's single durable queue instance with Android background services.</summary>
    public static Func<IDownloadQueue>? PlatformDownloadQueueFactory { get; set; }

    /// <summary>Pauses an active provider-authorized Audible Widevine download.</summary>
    public static Action<string, string>? PlatformAudibleOfflineDownloadPauseAction { get; set; }

    /// <summary>Raised by the foreground service after it persists a queue status or progress update.</summary>
    public static Action? PlatformWidevineOfflineDownloadQueueChangedAction { get; set; }

    /// <summary>Creates an on-demand provider metadata reader for book details.</summary>
    public static Func<IAudiobookDetailsProvider>? PlatformAudiobookDetailsProviderFactory { get; set; }

    /// <summary>Creates an authorized Audible media source from the saved account identity.</summary>
    public static Func<AudibleDownloadQuality, CancellationToken, Task<IAuthorizedAudioDownloadSource?>>? PlatformAudibleDownloadSourceFactory { get; set; }

    /// <summary>Legacy external title launcher, retained for non-download navigation only.</summary>
    public static Func<string, Task>? PlatformOpenAudibleTitleFactory { get; set; }

    /// <summary>Creates the platform audio player before the shared view is shown.</summary>
    public static Func<ILocalAudioPlayer>? PlatformLocalAudioPlayerFactory { get; set; }

    public static MainView CreatePlatformView()
    {
        var view = new MainView();
        var signInViewModel = PlatformSignInViewModelFactory?.Invoke();
        if (signInViewModel is not null
            && view.FindControl<StackPanel>("AccountContentPanel") is { } accountStack)
        {
            // Navigation uses visibility-switched page panels, not a TabControl.
            // Attach the account tile directly beneath the Accounts heading so
            // it is present even on a fresh, logged-out installation.
            accountStack.Children.Insert(
                Math.Min(1, accountStack.Children.Count),
                AudibleSignInPanel.Create(
                    signInViewModel,
                    () => view._viewModel.OpenAddAccountSheet(),
                    () => view.SyncLibraryAndUpdateAccountAsync()));
        }

        return view;
    }
}
