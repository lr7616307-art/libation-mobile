using Android.App;
using Android.Content.PM;
using Avalonia.Android;

namespace Libation.Mobile.Android;

[Activity(
    Label = "Libation Mobile",
    // AvaloniaMainActivity derives from AppCompatActivity. A platform Material
    // theme does not satisfy AppCompatDelegate and crashes during onCreate.
    Theme = "@style/Theme.AppCompat.Light.NoActionBar",
    MainLauncher = true,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
public class MainActivity : AvaloniaMainActivity
{
}
