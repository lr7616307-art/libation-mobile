using Android.Content;
using Libation.Mobile.Core;

namespace Libation.Mobile.Android;

/// <summary>Launches a provider authorization URL using Android's external activity resolver.</summary>
public sealed class AndroidExternalBrowserLauncher(Context context) : IExternalBrowserLauncher
{
    private readonly Context _context = context ?? throw new ArgumentNullException(nameof(context));

    public void Open(Uri authorizationUri)
    {
        ArgumentNullException.ThrowIfNull(authorizationUri);
        if (!authorizationUri.IsAbsoluteUri
            || !string.Equals(authorizationUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(authorizationUri.Host))
            throw new ArgumentException("Only absolute HTTPS authorization URLs may be opened.", nameof(authorizationUri));

        var intent = new Intent(Intent.ActionView, global::Android.Net.Uri.Parse(authorizationUri.AbsoluteUri));
        if (_context is not global::Android.App.Activity)
            intent.AddFlags(ActivityFlags.NewTask);

        _context.StartActivity(intent);
    }
}
