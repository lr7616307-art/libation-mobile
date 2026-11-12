using Android.App;
using Android.Content;

namespace Libation.Mobile.Android;

/// <summary>Opens one Audible product page in the official Audible Android app when installed.</summary>
internal static class AndroidAudibleBookLauncher
{
    public static Task OpenAsync(Application application, string asin, string marketplace)
    {
        ArgumentNullException.ThrowIfNull(application);
        ArgumentException.ThrowIfNullOrWhiteSpace(asin);

        var host = GetAudibleHost(marketplace);
        var url = $"https://{host}/pd/{System.Uri.EscapeDataString(asin.Trim())}";
        var uri = Android.Net.Uri.Parse(url);
        var appIntent = new Intent(Intent.ActionView, uri);
        appIntent.SetPackage("com.audible.application");
        appIntent.AddFlags(ActivityFlags.NewTask);

        try
        {
            application.StartActivity(appIntent);
        }
        catch (ActivityNotFoundException)
        {
            var browserIntent = new Intent(Intent.ActionView, uri);
            browserIntent.AddFlags(ActivityFlags.NewTask);
            application.StartActivity(browserIntent);
        }

        return Task.CompletedTask;
    }

    private static string GetAudibleHost(string? marketplace)
        => marketplace?.Trim().ToLowerInvariant() switch
        {
            "in" => "www.audible.in",
            "uk" => "www.audible.co.uk",
            "de" => "www.audible.de",
            "fr" => "www.audible.fr",
            "ca" => "www.audible.ca",
            "au" => "www.audible.com.au",
            "it" => "www.audible.it",
            "es" => "www.audible.es",
            "jp" => "www.audible.co.jp",
            "br" => "www.audible.com.br",
            _ => "www.audible.com"
        };
}
