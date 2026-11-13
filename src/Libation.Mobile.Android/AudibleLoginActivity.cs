using Libation.Mobile.Audible;
using Libation.Mobile.Core;
using System.Text.Json;
using Android.App;
using Android.OS;
using Android.Views;
using Android.Webkit;
using Android.Widget;
using Android.Graphics;

namespace Libation.Mobile.Android;

[Activity(
    Exported = false,
    Label = "Audible sign-in",
    Theme = "@android:style/Theme.Material.NoActionBar",
    ConfigurationChanges = global::Android.Content.PM.ConfigChanges.Orientation | global::Android.Content.PM.ConfigChanges.ScreenSize)]
public sealed class AudibleLoginActivity : Activity
{
    internal const string AuthorizationUrlExtra = "audible.authorization_url";
    internal const string ExpectedHostExtra = "audible.expected_host";
    internal const string SignInCookiesExtra = "audible.sign_in_cookies";
    internal const string RequestIdExtra = "audible.request_id";
    private static readonly object ActiveGate = new();
    private static readonly Dictionary<string, WeakReference<AudibleLoginActivity>> ActiveActivities =
        new(StringComparer.Ordinal);
    private WebView? _webView;
    private string _expectedHost = string.Empty;
    private string _requestId = string.Empty;
    private bool _finished;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        Window?.SetStatusBarColor(Color.Rgb(35, 40, 52));
        Window?.SetNavigationBarColor(Color.Black);

        var authorizationUrl = Intent?.GetStringExtra(AuthorizationUrlExtra);
        var signInCookiesJson = Intent?.GetStringExtra(SignInCookiesExtra);
        _expectedHost = Intent?.GetStringExtra(ExpectedHostExtra) ?? string.Empty;
        _requestId = Intent?.GetStringExtra(RequestIdExtra) ?? string.Empty;
        ExternalSignInCookie[] signInCookies;
        try
        {
            signInCookies = string.IsNullOrWhiteSpace(signInCookiesJson)
                ? []
                : JsonSerializer.Deserialize<ExternalSignInCookie[]>(signInCookiesJson) ?? [];
        }
        catch (JsonException)
        {
            signInCookies = [];
        }

        if (string.IsNullOrWhiteSpace(authorizationUrl)
            || !Uri.TryCreate(authorizationUrl, UriKind.Absolute, out var authorizationUri)
            || !string.Equals(authorizationUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(_expectedHost)
            || string.IsNullOrWhiteSpace(_requestId)
            || signInCookies.Length != 3
            || !signInCookies.Any(cookie => cookie.Name == "frc")
            || !signInCookies.Any(cookie => cookie.Name == "map-md")
            || !signInCookies.Any(cookie => cookie.Name == "sid"))
        {
            FinishLogin(null);
            return;
        }

        lock (ActiveGate)
            ActiveActivities[_requestId] = new WeakReference<AudibleLoginActivity>(this);

        var root = new LinearLayout(this) { Orientation = Orientation.Vertical };
        root.SetBackgroundColor(Color.Black);

        var header = new LinearLayout(this)
        {
            Orientation = Orientation.Horizontal,
        };
        header.SetGravity(GravityFlags.CenterVertical);
        header.SetPadding(18, 12, 18, 12);
        header.SetBackgroundColor(Color.Rgb(43, 49, 63));

        var back = new Button(this) { Text = "‹  Back" };
        back.SetTextColor(Color.Rgb(145, 190, 205));
        back.SetBackgroundColor(Color.Transparent);
        back.Click += (_, _) => FinishLogin(null);
        header.AddView(back, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));

        var title = new TextView(this)
        {
            Text = FormatMarketplaceHeader(_expectedHost),
            TextSize = 18,
            Gravity = GravityFlags.Center
        };
        title.SetTextColor(Color.White);
        header.AddView(title, new LinearLayout.LayoutParams(0,
            ViewGroup.LayoutParams.WrapContent, 1));

        root.AddView(header, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

        _webView = new WebView(this);
        _webView.Settings.JavaScriptEnabled = true;
        _webView.Settings.DomStorageEnabled = true;
        _webView.Settings.AllowFileAccess = false;
        _webView.Settings.AllowContentAccess = false;
        _webView.Settings.MixedContentMode = MixedContentHandling.NeverAllow;
        if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
            _webView.Settings.SafeBrowsingEnabled = true;

        var cookieManager = CookieManager.Instance;
        cookieManager.SetAcceptCookie(true);
        cookieManager.SetAcceptThirdPartyCookies(_webView, true);
        var cookieOrigin = authorizationUri.GetLeftPart(UriPartial.Authority) + "/ap/signin";
        foreach (var cookie in signInCookies)
        {
            var cookieDomain = cookie.Domain.TrimStart('.');
            if (string.IsNullOrWhiteSpace(cookie.Name)
                || !string.Equals(cookie.Name, "frc", StringComparison.Ordinal)
                    && !string.Equals(cookie.Name, "map-md", StringComparison.Ordinal)
                    && !string.Equals(cookie.Name, "sid", StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(cookie.Domain)
                || !(string.Equals(authorizationUri.Host, cookieDomain, StringComparison.OrdinalIgnoreCase) || authorizationUri.Host.EndsWith("." + cookieDomain, StringComparison.OrdinalIgnoreCase))
                || cookie.Value is null
                || cookie.Value.Contains(';')
                || cookie.Value.Contains('\n')
                || (cookie.Path != "/ap" && cookie.Path != "/"))
            {
                FinishLogin(null);
                return;
            }

            var cookieHeader = $"{cookie.Name}={cookie.Value}; Domain={cookie.Domain}; Path={cookie.Path}; Secure; SameSite=None";
            cookieManager.SetCookie(cookieOrigin, cookieHeader);
        }
        cookieManager.Flush();

        _webView.SetWebViewClient(new AudibleLoginWebViewClient(
            _expectedHost,
            url => FinishLogin(url)));
        root.AddView(_webView, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, 0, 1));

        SetContentView(root);
        _webView.LoadUrl(authorizationUrl);
    }

    private void FinishLogin(string? responseUrl)
    {
        if (_finished) return;
        _finished = true;
        AndroidExternalBrowserLauncher.Complete(_requestId, responseUrl);
        Finish();
    }

    internal static void Cancel(string requestId)
    {
        AudibleLoginActivity? activity = null;
        lock (ActiveGate)
        {
            if (ActiveActivities.TryGetValue(requestId, out var weakReference))
            {
                if (weakReference.TryGetTarget(out var found))
                    activity = found;
                else
                    ActiveActivities.Remove(requestId);
            }
        }

        // Cancellation can originate from the view model's continuation thread.
        if (activity is { } target)
            target.RunOnUiThread(() => target.FinishLogin(null));
    }

    public override void OnBackPressed()
    {
        FinishLogin(null);
        base.OnBackPressed();
    }

    public override void OnWindowFocusChanged(bool hasFocus)
    {
        base.OnWindowFocusChanged(hasFocus);
        if (!hasFocus || _finished || _webView is null) return;
        _webView.EvaluateJavascript("window.location.href", new UrlEvaluationCallback(
            url => RunOnUiThread(() => HandleCallbackProbe(url))));
    }

    private void HandleCallbackProbe(string? encodedUrl)
    {
        if (_finished || string.IsNullOrWhiteSpace(encodedUrl)) return;
        string url;
        try
        {
            // EvaluateJavascript returns a JSON-encoded JavaScript string. Decode
            // it as JSON so escaped query delimiters cannot hide the OAuth code.
            url = System.Text.Json.JsonSerializer.Deserialize<string>(encodedUrl)
                ?? string.Empty;
        }
        catch (System.Text.Json.JsonException)
        {
            url = encodedUrl.Trim('"');
        }
        if (!TryValidateCallback(url)) return;
        FinishLogin(url);
    }

    private bool TryValidateCallback(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !HostsMatch(uri.Host, _expectedHost)
            || !uri.IsDefaultPort
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.Equals(uri.AbsolutePath.TrimEnd('/'), "/ap/maplanding", StringComparison.OrdinalIgnoreCase))
            return false;

        return AudibleOAuthFlow.HasAuthorizationResponse(uri);
    }

    private static bool HostsMatch(string actual, string expected)
    {
        static string Normalize(string host)
        {
            var normalized = host.Trim().TrimEnd('.');
            return normalized.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
                ? normalized[4..] : normalized;
        }
        return string.Equals(Normalize(actual), Normalize(expected), StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatMarketplaceHeader(string callbackHost)
    {
        var normalizedHost = callbackHost.ToLowerInvariant();
        if (normalizedHost.StartsWith("www.", StringComparison.Ordinal))
            normalizedHost = normalizedHost[4..];
        var region = normalizedHost switch
        {
            "amazon.com" or "audible.com" => "🇺🇸  United States",
            "amazon.co.uk" or "audible.co.uk" => "🇬🇧  United Kingdom",
            "amazon.de" or "audible.de" => "🇩🇪  Germany",
            "amazon.fr" or "audible.fr" => "🇫🇷  France",
            "amazon.ca" or "audible.ca" => "🇨🇦  Canada",
            "amazon.com.au" or "audible.com.au" => "🇦🇺  Australia",
            "amazon.it" or "audible.it" => "🇮🇹  Italy",
            "amazon.es" or "audible.es" => "🇪🇸  Spain",
            "amazon.in" or "audible.in" => "🇮🇳  India",
            "amazon.co.jp" or "audible.co.jp" => "🇯🇵  Japan",
            "amazon.com.br" or "audible.com.br" => "🇧🇷  Brazil",
            _ => "Amazon sign-in"
        };
        return region;
    }

    protected override void OnDestroy()
    {
        if (!_finished) FinishLogin(null);
        lock (ActiveGate)
        {
            if (ActiveActivities.TryGetValue(_requestId, out var weakReference)
                && weakReference.TryGetTarget(out var active)
                && ReferenceEquals(active, this))
                ActiveActivities.Remove(_requestId);
        }
        if (_webView is not null)
        {
            _webView.StopLoading();
            _webView.LoadUrl("about:blank");
            _webView.ClearHistory();
            _webView.ClearCache(true);
            _webView.RemoveAllViews();
            _webView.Destroy();
            _webView = null;
        }

        // The returned provider identity (including website cookies) is stored
        // separately in the encrypted secret store. Do not retain the temporary
        // browser's Amazon session in WebView's persistent cookie/local storage.
        var cookieManager = CookieManager.Instance;
        cookieManager.RemoveAllCookies(new CookieCleanupCallback(cookieManager.Flush));
        WebStorage.Instance.DeleteAllData();
        base.OnDestroy();
    }

    private sealed class UrlEvaluationCallback(Action<string?> callback) : Java.Lang.Object, IValueCallback
    {
        public void OnReceiveValue(Java.Lang.Object? value)
            => callback(value?.ToString());
    }

    private sealed class CookieCleanupCallback(Action completed) : Java.Lang.Object, IValueCallback
    {
        public void OnReceiveValue(Java.Lang.Object? value) => completed();
    }

    private sealed class AudibleLoginWebViewClient(string expectedHost, Action<string> completed) : WebViewClient
    {
        // Amazon may submit the final marketplace redirect through a navigation
        // path that WebView does not expose to ShouldOverrideUrlLoading (notably
        // POST-driven redirects). Observe the top-level page start as a fallback.
        public override void OnPageStarted(WebView? view, string? url, Bitmap? favicon)
        {
            base.OnPageStarted(view, url, favicon);
            if (!string.IsNullOrWhiteSpace(url))
                TryComplete(url);
        }

        public override bool ShouldOverrideUrlLoading(WebView? view, IWebResourceRequest? request)
        {
            var url = request?.Url?.ToString();
            return !string.IsNullOrWhiteSpace(url) && TryComplete(url);
        }

        public override bool ShouldOverrideUrlLoading(WebView? view, string? url)
            => !string.IsNullOrWhiteSpace(url) && TryComplete(url);

        private bool TryComplete(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
                || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                || !HostsMatch(uri.Host, expectedHost)
                || !uri.IsDefaultPort
                || !string.IsNullOrEmpty(uri.UserInfo)
                || !string.Equals(uri.AbsolutePath.TrimEnd('/'), "/ap/maplanding", StringComparison.OrdinalIgnoreCase))
                return false;

            if (!AudibleOAuthFlow.HasAuthorizationResponse(uri))
                return false;

            completed(url);
            return true;
        }

        public override void OnReceivedSslError(WebView? view, SslErrorHandler? handler, global::Android.Net.Http.SslError? error)
        {
            // Never bypass certificate errors on a page that handles account sign-in.
            handler?.Cancel();
        }
    }
}
