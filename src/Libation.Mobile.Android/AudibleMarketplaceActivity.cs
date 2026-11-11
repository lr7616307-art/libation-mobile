using Android.App;
using Android.Content.PM;
using Android.Graphics;
using Android.OS;
using Android.Views;
using Android.Widget;

namespace Libation.Mobile.Android;

/// <summary>Marketplace selector shown before Audible creates its login URL.</summary>
[Activity(
    Exported = false,
    Label = "Choose Audible marketplace",
    Theme = "@android:style/Theme.Material.NoActionBar",
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize)]
public sealed class AudibleMarketplaceActivity : Activity
{
    internal const string DefaultMarketplaceExtra = "audible.default_marketplace";
    private bool _finished;
    private string _defaultMarketplace = "in";

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        Window?.SetStatusBarColor(Color.Rgb(35, 40, 52));
        Window?.SetNavigationBarColor(Color.Black);
        _defaultMarketplace = Intent?.GetStringExtra(DefaultMarketplaceExtra) ?? "in";

        var root = new LinearLayout(this) { Orientation = Orientation.Vertical };
        root.SetPadding(24, 32, 24, 24);
        root.SetBackgroundColor(Color.Rgb(18, 16, 25));

        var title = new TextView(this)
        {
            Text = "Choose Audible marketplace",
            TextSize = 22,
            Gravity = GravityFlags.Center
        };
        title.SetTextColor(Color.White);
        root.AddView(title, MatchWidthWrapHeight());

        var subtitle = new TextView(this)
        {
            Text = "Select the region where your Audible account is registered.",
            TextSize = 14,
            Gravity = GravityFlags.Center
        };
        subtitle.SetTextColor(Color.Rgb(190, 182, 205));
        subtitle.SetPadding(0, 12, 0, 24);
        root.AddView(subtitle, MatchWidthWrapHeight());

        var list = new ScrollView(this);
        var markets = new LinearLayout(this) { Orientation = Orientation.Vertical };
        markets.AddView(CreateDemoModeButton());
        AddMarketplace(markets, "🇺🇸  United States", "audible.com", "us");
        AddMarketplace(markets, "🇬🇧  United Kingdom", "audible.co.uk", "uk");
        AddMarketplace(markets, "🇩🇪  Germany", "audible.de", "de");
        AddMarketplace(markets, "🇫🇷  France", "audible.fr", "fr");
        AddMarketplace(markets, "🇨🇦  Canada", "audible.ca", "ca");
        AddMarketplace(markets, "🇦🇺  Australia", "audible.com.au", "au");
        AddMarketplace(markets, "🇮🇹  Italy", "audible.it", "it");
        AddMarketplace(markets, "🇪🇸  Spain", "audible.es", "es");
        AddMarketplace(markets, "🇮🇳  India", "audible.in", "in");
        AddMarketplace(markets, "🇯🇵  Japan", "audible.co.jp", "jp");
        AddMarketplace(markets, "🇧🇷  Brazil", "audible.com.br", "br");
        list.AddView(markets);
        root.AddView(list, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, 0, 1));

        var cancel = new Button(this) { Text = "Back to Accounts" };
        cancel.Click += (_, _) => FinishSelection(null);
        root.AddView(cancel, MatchWidthWrapHeight());
        SetContentView(root);
    }

    private Button CreateDemoModeButton()
    {
        var button = new Button(this) { Text = "🎬  Demo Mode\\n       no login" };
        button.SetMinimumHeight(64);
        button.Enabled = false;
        button.Alpha = 0.65f;
        return button;
    }

    private void AddMarketplace(LinearLayout root, string name, string domain, string code)
    {
        var label = $"{name}\\n{domain}";
        var button = new Button(this)
        {
            Text = code == _defaultMarketplace ? label + "  ✓" : label
        };
        button.SetAllCaps(false);
        button.SetMinimumHeight(68);
        button.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 15);
        button.SetTextColor(Color.Rgb(225, 225, 232));
        button.SetBackgroundColor(Color.Rgb(43, 49, 63));
        var margin = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent)
        { BottomMargin = 8 };
        button.Click += (_, _) => FinishSelection(code);
        root.AddView(button, margin);
    }

    private static LinearLayout.LayoutParams MatchWidthWrapHeight()
        => new(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);

    private void FinishSelection(string? marketplace)
    {
        if (_finished) return;
        _finished = true;
        AndroidExternalBrowserLauncher.CompleteMarketplace(marketplace);
        Finish();
    }

    public override void OnBackPressed()
    {
        FinishSelection(null);
        base.OnBackPressed();
    }

    protected override void OnDestroy()
    {
        if (!_finished) FinishSelection(null);
        base.OnDestroy();
    }
}
