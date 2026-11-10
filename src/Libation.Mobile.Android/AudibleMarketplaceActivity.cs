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

        AddMarketplace(root, "🇮🇳  India  ·  audible.in", "in");
        AddMarketplace(root, "🇺🇸  United States  ·  audible.com", "us");
        AddMarketplace(root, "🇬🇧  United Kingdom  ·  audible.co.uk", "uk");

        var cancel = new Button(this) { Text = "Cancel" };
        cancel.Click += (_, _) => FinishSelection(null);
        root.AddView(cancel, MatchWidthWrapHeight());
        SetContentView(root);
    }

    private void AddMarketplace(LinearLayout root, string label, string code)
    {
        var button = new Button(this)
        {
            Text = code == _defaultMarketplace ? label + "  ✓" : label
        };
        button.SetMinimumHeight(56);
        button.Click += (_, _) => FinishSelection(code);
        root.AddView(button, MatchWidthWrapHeight());
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
