using Android.App;
using Android.Content;
using Android.Graphics;
using Android.OS;
using Android.Views;
using Android.Widget;

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

    private string _authorizationUrl = string.Empty;
    private string _expectedHost = string.Empty;
    private EditText? _responseInput;
    private TextView? _statusLabel;
    private bool _finished;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        Window?.SetStatusBarColor(Color.Rgb(35, 40, 52));
        Window?.SetNavigationBarColor(Color.Black);

        _authorizationUrl = Intent?.GetStringExtra(AuthorizationUrlExtra) ?? string.Empty;
        _expectedHost = Intent?.GetStringExtra(ExpectedHostExtra) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(_authorizationUrl) || string.IsNullOrWhiteSpace(_expectedHost))
        {
            FinishLogin(null);
            return;
        }

        BuildSignInReturnUi();
    }

    private void BuildSignInReturnUi()
    {
        var root = new LinearLayout(this)
        {
            Orientation = Orientation.Vertical
        };
        root.SetBackgroundColor(Color.Rgb(12, 10, 18));

        var header = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        header.SetGravity(GravityFlags.CenterVertical);
        header.SetPadding(16, 10, 16, 10);
        header.SetBackgroundColor(Color.Rgb(35, 40, 52));

        var back = new Button(this) { Text = "‹  Cancel" };
        back.SetTextColor(Color.White);
        back.SetBackgroundColor(Color.Transparent);
        back.Click += (_, _) => FinishLogin(null);
        header.AddView(back, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));

        var title = new TextView(this)
        {
            Text = "Audible India sign-in",
            TextSize = 18,
            Gravity = GravityFlags.Center
        };
        title.SetTextColor(Color.White);
        header.AddView(title, new LinearLayout.LayoutParams(0,
            ViewGroup.LayoutParams.WrapContent, 1));
        root.AddView(header, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

        var scroll = new ScrollView(this);
        var content = new LinearLayout(this)
        {
            Orientation = Orientation.Vertical
        };
        content.SetPadding(20, 22, 20, 22);

        var heading = new TextView(this)
        {
            Text = "Finish sign-in in your browser",
            TextSize = 23,
            Gravity = GravityFlags.Center
        };
        heading.SetTextColor(Color.White);
        content.AddView(heading, MatchWrap());

        var instructions = new TextView(this)
        {
            Text = "1. Open Amazon sign-in below and complete your password, OTP and any security checks in your normal browser.\n\n2. When sign-in finishes, copy the complete URL from the browser address bar.\n\n3. Return to VØRATH, paste that URL in the box and tap Continue.\n\nThe URL contains temporary sign-in information. Keep it private and do not send it to anyone.",
            TextSize = 15
        };
        instructions.SetTextColor(Color.Rgb(210, 204, 224));
        instructions.SetPadding(0, 16, 0, 18);
        content.AddView(instructions, MatchWrap());

        var openBrowser = new Button(this) { Text = "Open Amazon sign-in" };
        openBrowser.Click += OpenBrowserClicked;
        content.AddView(openBrowser, MatchWrap());

        var pasteLabel = new TextView(this)
        {
            Text = "Paste the complete final address-bar URL",
            TextSize = 14
        };
        pasteLabel.SetTextColor(Color.White);
        pasteLabel.SetPadding(0, 22, 0, 6);
        content.AddView(pasteLabel, MatchWrap());

        _responseInput = new EditText(this)
        {
            Hint = "https://www.amazon.in/ap/maplanding?...",
            Gravity = GravityFlags.Top | GravityFlags.Start,
            InputType = global::Android.Text.InputTypes.ClassText | global::Android.Text.InputTypes.TextFlagMultiLine
        };
        _responseInput.SetMinLines(3);
        _responseInput.SetMaxLines(6);
        _responseInput.SetTextColor(Color.White);
        _responseInput.SetHintTextColor(Color.Rgb(150, 142, 164));
        _responseInput.SetPadding(12, 12, 12, 12);
        content.AddView(_responseInput, MatchWrap());

        _statusLabel = new TextView(this)
        {
            Text = "Your password and OTP are entered only in the browser. VØRATH receives only the final URL you paste.",
            TextSize = 13
        };
        _statusLabel.SetTextColor(Color.Rgb(185, 165, 232));
        _statusLabel.SetPadding(0, 14, 0, 12);
        content.AddView(_statusLabel, MatchWrap());

        var submit = new Button(this) { Text = "Continue to VØRATH" };
        submit.Click += SubmitResponseClicked;
        content.AddView(submit, MatchWrap());

        var cancel = new Button(this) { Text = "Cancel sign-in" };
        cancel.Click += (_, _) => FinishLogin(null);
        content.AddView(cancel, MatchWrap());

        scroll.AddView(content);
        root.AddView(scroll, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, 0, 1));
        SetContentView(root);
    }

    private void OpenBrowserClicked(object? sender, EventArgs e)
    {
        try
        {
            var intent = new Intent(Intent.ActionView, global::Android.Net.Uri.Parse(_authorizationUrl));
            StartActivity(intent);
            SetStatus("Complete sign-in in your browser. Then copy the full final address-bar URL and return here.");
        }
        catch (ActivityNotFoundException)
        {
            SetStatus("No browser app could open the Amazon sign-in page. Install or enable a browser and try again.", isError: true);
        }
        catch (Exception exception)
        {
            SetStatus($"Could not open the browser ({exception.GetType().Name}).", isError: true);
        }
    }

    private void SubmitResponseClicked(object? sender, EventArgs e)
    {
        if (_finished || _responseInput is null)
            return;

        var responseUrl = _responseInput.Text?.Trim();
        if (string.IsNullOrWhiteSpace(responseUrl) || !TryValidateCallback(responseUrl))
        {
            SetStatus(
                "That URL is not a recognized Audible sign-in completion URL. Copy the entire final URL from the Amazon browser address bar and try again.",
                isError: true);
            return;
        }

        // The credential-bearing response exists only in this short-lived local
        // variable and the active authentication call; never persist or log it.
        _responseInput.Text = string.Empty;
        FinishLogin(responseUrl);
    }

    private void SetStatus(string message, bool isError = false)
    {
        if (_statusLabel is null) return;
        _statusLabel.Text = message;
        _statusLabel.SetTextColor(isError ? Color.Rgb(255, 133, 133) : Color.Rgb(185, 165, 232));
    }

    private static LinearLayout.LayoutParams MatchWrap()
        => new(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);

    private void FinishLogin(string? responseUrl)
    {
        if (_finished) return;
        _finished = true;
        if (_responseInput is not null)
            _responseInput.Text = string.Empty;
        AndroidExternalBrowserLauncher.Complete(responseUrl);
        Finish();
    }

    public override void OnBackPressed()
    {
        FinishLogin(null);
    }

    private bool TryValidateCallback(string url)
        => TryParseCallback(url, _expectedHost);

    private static bool TryParseCallback(string url, string expectedHost)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(uri.Host, expectedHost, StringComparison.OrdinalIgnoreCase)
            || !uri.IsDefaultPort
            || !string.IsNullOrEmpty(uri.UserInfo))
            return false;

        var isMarketplaceLanding = string.Equals(
            uri.AbsolutePath.TrimEnd('/'), "/ap/maplanding", StringComparison.OrdinalIgnoreCase);
        var hasAuthorizationCode = uri.Query
            .TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .Any(pair =>
                pair.Length == 2
                && string.Equals(
                    Uri.UnescapeDataString(pair[0].Replace("+", " ", StringComparison.Ordinal)),
                    "openid.oa2.authorization_code",
                    StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(
                    Uri.UnescapeDataString(pair[1].Replace("+", " ", StringComparison.Ordinal))));

        return isMarketplaceLanding && hasAuthorizationCode;
    }

    protected override void OnDestroy()
    {
        if (!_finished)
            FinishLogin(null);
        base.OnDestroy();
    }
}
