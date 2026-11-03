using Avalonia.Controls;
using Libation.Mobile.UI.ViewModels;

namespace Libation.Mobile.UI.Views;

public partial class MainView
{
    /// <summary>
    /// Set by the Android host before Avalonia creates the main view.
    /// The shared UI remains usable without Android-specific services.
    /// </summary>
    public static Func<AudibleSignInViewModel>? PlatformSignInViewModelFactory { get; set; }

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
