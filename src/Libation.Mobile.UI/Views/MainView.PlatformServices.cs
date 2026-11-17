using Libation.Mobile.Core;
using Libation.Mobile.UI.ViewModels;

namespace Libation.Mobile.UI.Views;

public partial class MainView
{
    /// <summary>Creates account services only when the user opens the account flow.</summary>
    public static Func<AudibleSignInViewModel>? PlatformSignInViewModelFactory { get; set; }

    /// <summary>Creates the saved-library provider only when the user requests a sync.</summary>
    public static Func<ILibraryCatalogProvider>? PlatformLibraryCatalogProviderFactory { get; set; }

    /// <summary>Runs an optional Android Widevine capability diagnostic.</summary>
    public static Func<string>? PlatformWidevineDiagnosticFactory { get; set; }

    /// <summary>Creates the playback adapter after the first library load.</summary>
    public static Func<ILocalAudioPlayer>? PlatformLocalAudioPlayerFactory { get; set; }

    /// <summary>Persists startup failures without making diagnostics fatal.</summary>
    public static Action<string, Exception?>? PlatformStartupExceptionReporter { get; set; }

    public static MainView CreatePlatformView() => new MainView();
}
