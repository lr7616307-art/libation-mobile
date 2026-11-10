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
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        StartupDiagnostics.Install(this);

        // Preserve a previous process failure, but let AppCompat initialize before
        // replacing its content with the native diagnostic screen.
        var previousFailure = StartupDiagnostics.Read(this);
        try
        {
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
