using Android.App;
using Android.Content.PM;
using Android.OS;
using Avalonia.Android;

namespace Libation.Mobile.Android;

[Activity(
    Label = "VØRATH",
    // AvaloniaMainActivity derives from AppCompatActivity. A platform Material
    // theme does not satisfy AppCompatDelegate and crashes during onCreate.
    Theme = "@style/Theme.AppCompat.Light.NoActionBar",
    MainLauncher = true,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
public class MainActivity : AvaloniaMainActivity
{
    internal static Libation.Mobile.UI.Views.MainView? ActiveMainView { get; set; }

    public bool HandleAppBack()
        => ActiveMainView?.HandleAndroidBack() ?? false;

    public override void OnBackPressed()
    {
        if (HandleAppBack()) return;
        base.OnBackPressed();
    }

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        StartupDiagnostics.Install(this);

        // Preserve a previous process failure, but let AppCompat initialize before
        // replacing its content with the native diagnostic screen.
        var previousFailure = StartupDiagnostics.Read(this);
        try
        {
            if (Avalonia.Application.Current?.ApplicationLifetime is
                Avalonia.Controls.ApplicationLifetimes.IActivityApplicationLifetime lifetime)
            {
                var originalFactory = lifetime.MainViewFactory;
                lifetime.MainViewFactory = () =>
                {
                    var view = originalFactory?.Invoke() as Libation.Mobile.UI.Views.MainView;
                    ActiveMainView = view;
                    return view;
                };
            }

            base.OnCreate(savedInstanceState);
            if (!string.IsNullOrWhiteSpace(previousFailure))
                StartupDiagnostics.Show(this, previousFailure);
        }
        catch (Exception exception)
        {
            StartupDiagnostics.Record(this, "MainActivity.OnCreate", exception);
            StartupDiagnostics.Show(this, StartupDiagnostics.Read(this) ?? exception.ToString());
        }
    }
}
