using Avalonia.Controls;
using Libation.Mobile.Core;
using Libation.Mobile.UI.ViewModels;

namespace Libation.Mobile.UI.Views;

public partial class MainView
{
    /// <summary>Set by the platform host before Avalonia creates the main view.</summary>
    public static Func<AudibleSignInViewModel>? PlatformSignInViewModelFactory { get; set; }

    /// <summary>
    /// Supplies a provider that restores the already-saved identity on demand.
    /// The shared UI remains usable without platform-specific account services.
    /// </summary>
    public static Func<ILibraryCatalogProvider>? PlatformLibraryCatalogProviderFactory { get; set; }

    /// <summary>Runs a non-invasive, platform-specific Widevine capability diagnostic.</summary>
    public static Func<string>? PlatformWidevineDiagnosticFactory { get; set; }

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
