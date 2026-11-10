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

    /// <summary>Creates the platform audio player after the main view is loaded.</summary>
    public static Func<ILocalAudioPlayer>? PlatformLocalAudioPlayerFactory { get; set; }

    /// <summary>Persists startup exceptions that occur after the Android activity is visible.</summary>
    public static Action<string, Exception>? PlatformStartupExceptionReporter { get; set; }

    public static MainView CreatePlatformView()
    {
        try
        {
            // The current screen is a Grid, not a TabControl. Account sign-in is
            // already hosted by the Account section's button, so do not construct
            // an unused sign-in panel during application startup.
            return new MainView();
        }
        catch (Exception exception)
        {
            // Keep the process alive and show the actual view-construction failure.
            return new MainView(exception);
        }
    }

    private MainView(Exception startupException)
    {
        var dataDirectory = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _viewModel = new MainViewModel(
            new JsonLibraryRepository(Path.Combine(dataDirectory, "library.json")),
            new JsonDownloadQueue(Path.Combine(dataDirectory, "download-queue.json")));

        var details = startupException.ToString();
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
