using Avalonia.Controls;
using Libation.Mobile.Core;
using Libation.Mobile.UI.ViewModels;

namespace Libation.Mobile.UI.Views;

public partial class MainView
{
    public static Func<AudibleSignInViewModel>? PlatformSignInViewModelFactory { get; set; }
    public static Func<ILibraryCatalogProvider>? PlatformLibraryCatalogProviderFactory { get; set; }
    public static Func<string>? PlatformWidevineDiagnosticFactory { get; set; }
    public static Func<ILocalAudioPlayer>? PlatformLocalAudioPlayerFactory { get; set; }

    public static MainView CreatePlatformView()
    {
        // Never create Android Keystore, Audible, or browser services while the
        // first view is being constructed. Sign-in creates them on user action.
        return new MainView();
    }
}
