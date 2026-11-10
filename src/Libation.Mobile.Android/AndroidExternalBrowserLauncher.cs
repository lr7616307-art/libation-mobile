using Android.Content;
using Libation.Mobile.Core;

namespace Libation.Mobile.Android;

/// <summary>
/// Opens Audible in the user's normal browser and collects the final URL that
/// the user explicitly pastes back into the app. Credentials remain in the browser.
/// </summary>
public sealed class AndroidExternalBrowserLauncher(Context context) : IExternalBrowserLauncher
{
    private static readonly object Gate = new();
    private static TaskCompletionSource<string?>? _pending;
    private readonly Context _context = context ?? throw new ArgumentNullException(nameof(context));

    public async Task<string?> OpenAsync(Uri authorizationUri, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authorizationUri);
        if (!authorizationUri.IsAbsoluteUri
            || !string.Equals(authorizationUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(authorizationUri.Host))
            throw new ArgumentException("Only absolute HTTPS authorization URLs may be opened.", nameof(authorizationUri));

        var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (Gate)
        {
            if (_pending is not null)
                throw new InvalidOperationException("An Audible sign-in window is already open.");
            _pending = completion;
        }

        try
        {
            var intent = new Intent(_context, typeof(AudibleLoginActivity));
            intent.PutExtra(AudibleLoginActivity.AuthorizationUrlExtra, authorizationUri.AbsoluteUri);
            intent.PutExtra(AudibleLoginActivity.ExpectedHostExtra, authorizationUri.Host);
            intent.AddFlags(ActivityFlags.NewTask);
            _context.StartActivity(intent);
            using var registration = cancellationToken.Register(() => Complete(null));
            return await completion.Task.WaitAsync(TimeSpan.FromMinutes(10), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (Gate)
            {
                if (ReferenceEquals(_pending, completion)) _pending = null;
            }
        }
    }

    internal static void Complete(string? responseUrl)
    {
        TaskCompletionSource<string?>? completion;
        lock (Gate)
        {
            completion = _pending;
            _pending = null;
        }
        completion?.TrySetResult(responseUrl);
    }
}
