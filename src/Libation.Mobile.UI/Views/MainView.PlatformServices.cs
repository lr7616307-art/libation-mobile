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

    /// <summary>Creates the platform audio player before the shared view is shown.</summary>
    public static Func<ILocalAudioPlayer>? PlatformLocalAudioPlayerFactory { get; set; }

    public static MainView CreatePlatformView()
    {
        var view = new MainView();
        var signInViewModel = PlatformSignInViewModelFactory?.Invoke();
        if (signInViewModel is not null
            && view.Content is ScrollViewer { Content: StackPanel stack })
        {
            stack.Children.Insert(Math.Min(3, stack.Children.Count), AudibleSignInPanel.Create(signInViewModel));
        }

        return view;
    }
}
