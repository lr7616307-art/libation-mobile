using Android.App;
using Android.Content.PM;
using Avalonia.Android;
using Libation.Mobile.UI.Views;

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
    protected override void OnResume()
    {
        base.OnResume();
        // Reconcile a download left active by process death only after the app
        // is visibly resumed. Explicitly paused jobs remain paused until tapped.
        AndroidWidevineOfflineDownloadService.RecoverInterrupted(this);
    }

    public override void OnBackPressed()
    {
        // Consume Back while an app page, overlay, search, or player screen
        // can handle it. Do not finish the activity and tear down playback.
        if (MainView.TryHandleSystemBack())
            return;

        base.OnBackPressed();
    }
}
