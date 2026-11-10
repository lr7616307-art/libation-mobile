using System.Web;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.OS;
using Android.Text;
using Android.Views;
using Android.Widget;

namespace Libation.Mobile.Android;

/// <summary>
/// Native handoff screen for the supported Audible manual-return flow.
/// Amazon credentials are entered only in the external browser. The final
/// response URL is pasted by the user and kept in memory for one submission.
/// </summary>
[Activity(
    Exported = false,
    Label = "Connect Audible",
    Theme = "@android:style/Theme.Material.NoActionBar",
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize)]
public sealed class AudibleLoginActivity : Activity
{
    internal const string AuthorizationUrlExtra = "audible.authorization_url";
    internal const string ExpectedHostExtra = "audible.expected_host";
    internal const string ExpectedPathExtra = "audible.expected_path";

    private string _authorizationUrl = string.Empty;
    private string _expectedHost = string.Empty;
    private string _expectedPath = "/ap/maplanding";
    private EditText? _responseInput;
    private bool _finished;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        Window?.SetStatusBarColor(Color.Rgb(35, 40, 52));
        Window?.SetNavigationBarColor(Color.Black);

        _authorizationUrl = Intent?.GetStringExtra(AuthorizationUrlExtra) ?? string.Empty;
        _expectedHost = Intent?.GetStringExtra(ExpectedHostExtra) ?? string.Empty;
        _expectedPath = Intent?.GetStringExtra(ExpectedPathExtra) ?? "/ap/maplanding";
        if (string.IsNullOrWhiteSpace(_authorizationUrl) || string.IsNullOrWhiteSpace(_expectedHost))
        {
            FinishLogin(null);
            return;
        }

        var root = new LinearLayout(this)
        {
            Orientation = Orientation.Vertical
        };
        root.SetPadding(24, 28, 24, 24);
        root.SetBackgroundColor(Color.Rgb(18, 16, 25));

        var title = new TextView(this)
        {
            Text = "Connect your Audible account",
            TextSize = 23,
            Gravity = GravityFlags.Center
        };
        title.SetTextColor(Color.White);
        root.AddView(title, MatchWidthWrapHeight());

        var marketplace = new TextView(this)
        {
            Text = "Marketplace: " + _expectedHost,
            TextSize = 13,
            Gravity = GravityFlags.Center,
        };
        marketplace.SetTextColor(Color.Rgb(196, 181, 253));
        marketplace.SetPadding(0, 10, 0, 22);
        root.AddView(marketplace, MatchWidthWrapHeight());

        var instructions = new TextView(this)
        {
            Text = "1. Open Amazon/Audible sign-in in your browser.\n2. Complete sign-in and any verification there.\n3. Return here and paste the complete final address-bar URL.\n4. Tap Complete sign-in.",
            TextSize = 15,
            Gravity = GravityFlags.Start
        };
        instructions.SetTextColor(Color.Rgb(225, 220, 235));
        instructions.SetPadding(0, 0, 0, 18);
        root.AddView(instructions, MatchWidthWrapHeight());

        var openBrowser = new Button(this) { Text = "Open Amazon sign-in" };
        openBrowser.Click += (_, _) => OpenExternalBrowser();
        root.AddView(openBrowser, MatchWidthWrapHeight());

        var privacy = new TextView(this)
        {
            Text = "The URL contains a short-lived authorization code. Do not share it. VØRATH does not read your password, OTP, CAPTCHA response or browser cookies.",
            TextSize = 12
        };
        privacy.SetTextColor(Color.Rgb(175, 168, 190));
        privacy.SetPadding(0, 14, 0, 10);
        root.AddView(privacy, MatchWidthWrapHeight());

        _responseInput = new EditText(this)
        {
            Hint = "Paste the complete final browser URL here",
            InputType = InputTypes.ClassText | InputTypes.TextFlagMultiLine | InputTypes.TextFlagNoSuggestions,
            MinLines = 3,
            MaxLines = 5,
            Gravity = GravityFlags.Top | GravityFlags.Start
        };
        _responseInput.SetTextColor(Color.White);
        _responseInput.SetHintTextColor(Color.Rgb(145, 138, 160));
        _responseInput.SetPadding(12, 12, 12, 12);
        root.AddView(_responseInput, MatchWidthWrapHeight());

        var complete = new Button(this) { Text = "Complete sign-in" };
        complete.Click += (_, _) => SubmitResponse();
        root.AddView(complete, MatchWidthWrapHeight());

        var cancel = new Button(this) { Text = "Cancel" };
        cancel.Click += (_, _) => FinishLogin(null);
        root.AddView(cancel, MatchWidthWrapHeight());

        SetContentView(root);
    }

    private void OpenExternalBrowser()
    {
        try
        {
            var intent = new Intent(Intent.ActionView, global::Android.Net.Uri.Parse(_authorizationUrl));
            StartActivity(intent);
        }
        catch (ActivityNotFoundException)
        {
            Toast.MakeText(this, "No browser is available to open Audible sign-in.", ToastLength.Long)?.Show();
        }
        catch (Exception)
        {
            Toast.MakeText(this, "Could not open the external browser.", ToastLength.Long)?.Show();
        }
    }

    private void SubmitResponse()
    {
        var responseUrl = _responseInput?.Text?.Trim();
        if (string.IsNullOrWhiteSpace(responseUrl) || !IsExpectedCallback(responseUrl))
        {
            Toast.MakeText(this,
                "Paste the complete HTTPS return URL from the selected Audible marketplace.",
                ToastLength.Long)?.Show();
            return;
        }

        // Clear the editable control before handing the short-lived response to
        // the authenticator. The value is never written to preferences or logs.
        if (_responseInput is not null) _responseInput.Text = string.Empty;
        FinishLogin(responseUrl);
    }

    private bool IsExpectedCallback(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(uri.Host, _expectedHost, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(uri.AbsolutePath.TrimEnd('/'), _expectedPath.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !uri.IsDefaultPort)
            return false;

        return !string.IsNullOrWhiteSpace(
            HttpUtility.ParseQueryString(uri.Query)["openid.oa2.authorization_code"]);
    }

    private static LinearLayout.LayoutParams MatchWidthWrapHeight()
        => new(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);

    private void FinishLogin(string? responseUrl)
    {
        if (_finished) return;
        _finished = true;
        if (_responseInput is not null) _responseInput.Text = string.Empty;
        AndroidExternalBrowserLauncher.Complete(responseUrl);
        Finish();
    }

    public override void OnBackPressed()
    {
        FinishLogin(null);
        base.OnBackPressed();
    }

    protected override void OnDestroy()
    {
        if (!_finished) FinishLogin(null);
        _responseInput?.Dispose();
        _responseInput = null;
        base.OnDestroy();
    }
}
