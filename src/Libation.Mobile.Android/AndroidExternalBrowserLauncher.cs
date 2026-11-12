using Android.Content;
using Libation.Mobile.Core;
using Libation.Mobile.UI.ViewModels;

namespace Libation.Mobile.Android;

/// <summary>
/// Hosts the selected Audible marketplace authorization page in an in-app WebView.
/// Only the validated Audible marketplace callback URL reaches the auth adapter.
/// </summary>
public sealed class AndroidExternalBrowserLauncher(Context context) : IExternalBrowserLauncher, IAudibleMarketplaceSelector
{
    private static readonly object Gate = new();
    private static TaskCompletionSource<string?>? _pending;
    private static string? _pendingRequestId;
    private static TaskCompletionSource<string?>? _pendingMarketplace;
    private readonly Context _context = context ?? throw new ArgumentNullException(nameof(context));

    public async Task<string?> SelectMarketplaceAsync(
        string defaultMarketplace,
        CancellationToken cancellationToken = default)
    {
        var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (Gate)
        {
            if (_pendingMarketplace is not null)
                throw new InvalidOperationException("An Audible marketplace selection is already open.");
            _pendingMarketplace = completion;
        }

        try
        {
            var intent = new Intent(_context, typeof(AudibleMarketplaceActivity));
            intent.PutExtra(AudibleMarketplaceActivity.DefaultMarketplaceExtra, defaultMarketplace);
            intent.AddFlags(ActivityFlags.NewTask);
            _context.StartActivity(intent);
            using var registration = cancellationToken.Register(() => CompleteMarketplace(null));
            return await completion.Task.WaitAsync(TimeSpan.FromMinutes(2), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (Gate)
            {
                if (ReferenceEquals(_pendingMarketplace, completion)) _pendingMarketplace = null;
            }
        }
    }

    public async Task<string?> OpenAsync(Uri authorizationUri, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authorizationUri);
        if (!authorizationUri.IsAbsoluteUri
            || !string.Equals(authorizationUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(authorizationUri.Host))
            throw new ArgumentException("Only absolute HTTPS authorization URLs may be opened.", nameof(authorizationUri));

        var parameters = System.Web.HttpUtility.ParseQueryString(authorizationUri.Query);
        var returnTo = parameters["openid.return_to"];
        if (!Uri.TryCreate(returnTo, UriKind.Absolute, out var callbackUri)
            || !string.Equals(callbackUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(callbackUri.AbsolutePath.TrimEnd('/'), "/ap/maplanding", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(callbackUri.Host)
            || !callbackUri.IsDefaultPort)
            throw new InvalidOperationException("Audible authorization did not include a valid marketplace callback URL.");

        var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requestId = Guid.NewGuid().ToString("N");
        lock (Gate)
        {
            if (_pending is not null)
                throw new InvalidOperationException("An Audible sign-in window is already open.");
            _pending = completion;
            _pendingRequestId = requestId;
        }

        try
        {
            var intent = new Intent(_context, typeof(AudibleLoginActivity));
            intent.PutExtra(AudibleLoginActivity.AuthorizationUrlExtra, authorizationUri.AbsoluteUri);
            intent.PutExtra(AudibleLoginActivity.ExpectedHostExtra, callbackUri.Host);
            intent.PutExtra(AudibleLoginActivity.RequestIdExtra, requestId);
            intent.AddFlags(ActivityFlags.NewTask);
            _context.StartActivity(intent);
            using var registration = cancellationToken.Register(() => Complete(requestId, null));
            return await completion.Task.WaitAsync(TimeSpan.FromMinutes(10), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // This also runs after timeout. Never leave an old login WebView active
            // to deliver a late callback into a later sign-in attempt.
            Complete(requestId, null);
            AudibleLoginActivity.Cancel(requestId);
            lock (Gate)
            {
                if (string.Equals(_pendingRequestId, requestId, StringComparison.Ordinal))
                {
                    _pendingRequestId = null;
                    if (ReferenceEquals(_pending, completion)) _pending = null;
                }
            }
        }
    }

    internal static void CompleteMarketplace(string? marketplace)
    {
        TaskCompletionSource<string?>? completion;
        lock (Gate)
        {
            completion = _pendingMarketplace;
            _pendingMarketplace = null;
        }
        completion?.TrySetResult(marketplace);
    }

    internal static void Complete(string requestId, string? responseUrl)
    {
        if (string.IsNullOrWhiteSpace(requestId))
            return;

        TaskCompletionSource<string?>? completion;
        lock (Gate)
        {
            if (!string.Equals(_pendingRequestId, requestId, StringComparison.Ordinal))
                return;
            completion = _pending;
            _pending = null;
            _pendingRequestId = null;
        }
        completion?.TrySetResult(responseUrl);
    }
}
