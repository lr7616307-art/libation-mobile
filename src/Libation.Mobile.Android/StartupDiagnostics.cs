using Android.App;
using Android.Content;
using Android.Runtime;
using Android.Util;
using Android.Widget;

namespace Libation.Mobile.Android;

/// <summary>
/// Captures the first managed startup exception so a second launch can show
/// the exact failure instead of silently returning to the launcher.
/// The report contains only exception type, message and stack trace.
/// </summary>
internal static class StartupDiagnostics
{
    private const string FileName = "vorath-startup-diagnostic.txt";
    private static readonly object Gate = new();
    private static bool _installed;

    public static void Install(Context context)
    {
        lock (Gate)
        {
            if (_installed) return;
            _installed = true;
        }

        AndroidEnvironment.UnhandledExceptionRaiser += (_, args) =>
            Record(context, "Android managed unhandled exception", args.Exception);

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception exception)
                Record(context, "AppDomain unhandled exception", exception);
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Record(context, "Unobserved task exception", args.Exception);
            args.SetObserved();
        };
    }

    public static void Record(Context context, string phase, Exception exception)
    {
        try
        {
            var report = $"VØRATH STARTUP DIAGNOSTIC\nPhase: {phase}\nTime: {DateTimeOffset.Now:O}\nException: {exception.GetType().FullName}\nMessage: {exception.Message}\n\nStack trace:\n{exception.StackTrace}";
            var path = Path.Combine(context.FilesDir?.AbsolutePath ?? context.CacheDir!.AbsolutePath, FileName);
            File.WriteAllText(path, report);
            Log.Error("VORATH_STARTUP", report);
        }
        catch (Exception loggingException)
        {
            Log.Error("VORATH_STARTUP", $"Could not save startup diagnostic: {loggingException.GetType().Name}");
        }
    }

    public static string? Read(Context context)
    {
        try
        {
            var path = Path.Combine(context.FilesDir?.AbsolutePath ?? context.CacheDir!.AbsolutePath, FileName);
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception exception)
        {
            Log.Error("VORATH_STARTUP", $"Could not read startup diagnostic: {exception.GetType().Name}");
            return null;
        }
    }

    public static void Clear(Context context)
    {
        try
        {
            var path = Path.Combine(context.FilesDir?.AbsolutePath ?? context.CacheDir!.AbsolutePath, FileName);
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception exception)
        {
            Log.Error("VORATH_STARTUP", $"Could not clear startup diagnostic: {exception.GetType().Name}");
        }
    }

    public static void Show(Activity activity, string report)
    {
        var root = new LinearLayout(activity) { Orientation = Orientation.Vertical };
        root.SetPadding(24, 24, 24, 24);
        root.AddView(new TextView(activity)
        {
            Text = "VØRATH could not start",
            TextSize = 22,
            Typeface = Android.Graphics.Typeface.DefaultBold
        });

        root.AddView(new TextView(activity)
        {
            Text = "This diagnostic captures the startup exception. Long-press the text to select and copy it. Review it before sharing; do not share passwords, tokens, or account details.",
            TextSize = 14
        });

        var details = new TextView(activity)
        {
            Text = report,
            TextSize = 12
        };
        details.SetTextIsSelectable(true);
        var scroll = new ScrollView(activity);
        scroll.AddView(details);
        root.AddView(scroll, new LinearLayout.LayoutParams(
            Android.Views.ViewGroup.LayoutParams.MatchParent, 0, 1));

        var dismiss = new Button(activity) { Text = "Dismiss and retry" };
        dismiss.Click += (_, _) =>
        {
            Clear(activity);
            activity.Recreate();
        };
        root.AddView(dismiss);
        activity.SetContentView(root);
    }
}
