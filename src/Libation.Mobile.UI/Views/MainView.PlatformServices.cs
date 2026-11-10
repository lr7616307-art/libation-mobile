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

    /// <summary>Creates the platform audio player before the shared view is shown.</summary>
    public static Func<ILocalAudioPlayer>? PlatformLocalAudioPlayerFactory { get; set; }

    public static MainView CreatePlatformView()
    {
        try
        {
            var view = new MainView();
            var signInViewModel = PlatformSignInViewModelFactory?.Invoke();
            if (signInViewModel is not null && view.Content is TabControl tabs)
            {
                var accountTab = tabs.Items
                    .OfType<TabItem>()
                    .FirstOrDefault(tab => string.Equals(
                        tab.Header?.ToString(), "Account", StringComparison.Ordinal));

                if (accountTab?.Content is ScrollViewer { Content: StackPanel accountStack })
                {
                    // Keep the native sign-in panel inside the visible Account tab,
                    // rather than trying to insert it into the TabControl's root.
                    accountStack.Children.Insert(
                        Math.Min(2, accountStack.Children.Count),
                        AudibleSignInPanel.Create(signInViewModel));
                }
            }

            return view;
        }
        catch (Exception exception)
        {
            // Keep the process alive and show the actual view-construction failure.
            // This is especially useful on devices where ADB/Logcat is unavailable.
            return new MainView(exception);
        }
    }

    private MainView(Exception startupException)
    {
        var dataDirectory = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _viewModel = new MainViewModel(
            new JsonLibraryRepository(Path.Combine(dataDirectory, "library.json")),
            new JsonDownloadQueue(Path.Combine(dataDirectory, "download-queue.json")));

        var details = $"{startupException.GetType().FullName}\n{startupException.Message}\n\n{startupException.StackTrace}";
        Content = new StackPanel
        {
            Spacing = 14,
            Margin = new Avalonia.Thickness(20),
            Children =
            {
                new TextBlock
                {
                    Text = "VØRATH startup error",
                    FontSize = 22,
                    FontWeight = Avalonia.Media.FontWeight.Bold
                },
                new TextBlock
                {
                    Text = "The app caught an exception while constructing its main screen. Long-press the details below to select and copy them. Review them before sharing; remove any personal path or account detail.",
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap
                },
                new TextBox
                {
                    Text = details,
                    IsReadOnly = true,
                    AcceptsReturn = true,
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    MinHeight = 260
                }
            }
        };
    }
}
