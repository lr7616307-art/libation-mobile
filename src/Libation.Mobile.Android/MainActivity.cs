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
    public bool HandleAppBack()
    {
        var mainView = FindMainView(Window?.DecorView?.RootView);
        return mainView?.HandleAndroidBack() ?? false;
    }

    private static Libation.Mobile.UI.Views.MainView? FindMainView(Android.Views.View? view)
    {
        if (view is null) return null;
        if (view is Avalonia.Controls.Control control)
        {
            while (control.Parent is Avalonia.Controls.Control parent)
                control = parent;
            return control as Libation.Mobile.UI.Views.MainView;
        }
        if (view is Android.Views.ViewGroup group)
        {
            for (var i = 0; i < group.ChildCount; i++)
            {
                var result = FindMainView(group.GetChildAt(i));
                if (result is not null) return result;
            }
        }
        return null;
    }

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
