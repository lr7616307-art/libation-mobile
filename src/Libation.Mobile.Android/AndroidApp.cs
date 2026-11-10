using Android.Runtime;
using Avalonia;
using Avalonia.Android;
using Dinah.Core;
using Libation.Mobile.Audible;
using Libation.Mobile.Core;
using Libation.Mobile.UI;
using Libation.Mobile.UI.Views;
using Libation.Mobile.UI.ViewModels;

namespace Libation.Mobile.Android;

[Application]
public class AndroidApp : AvaloniaAndroidApplication<App>
{
    protected AndroidApp(nint javaReference, JniHandleOwnership transfer)
        : base(javaReference, transfer)
    {
    }

    public override void OnCreate()
    {
        base.OnCreate();

        // Do not initialize Android Keystore, Audible, or Media3 components
        // during Application.OnCreate. On some Android 10/ARMv7 devices these
        // optional services can fail before Avalonia has created its first view.
        // Keep startup minimal and construct each dependency only when requested.
        var services = new Lazy<PlatformServices>(() => new PlatformServices(this));

        MainView.PlatformSignInViewModelFactory =
            () => services.Value.CreateSignInViewModel();

        MainView.PlatformLibraryCatalogProviderFactory =
            () => services.Value.CreateCatalogProvider();

        MainView.PlatformLocalAudioPlayerFactory =
            () => new AndroidLocalAudioPlayer();

        MainView.PlatformWidevineDiagnosticFactory = () =>
        {
            var result = new AndroidWidevineCapabilityProbe().Probe();
            return result.IsSupported
                ? $"Widevine supported. Security level: {result.SecurityLevel ?? "not reported"}. {result.Diagnostic}"
                : $"Widevine not available. {result.Diagnostic}";
        };
    }

    private sealed class PlatformServices
    {
        private readonly AndroidSecretStore _secrets;
        private readonly AudibleIdentitySnapshotStore _snapshots;
        private readonly AccountConnectionService _connection;
        private readonly AndroidExternalBrowserLauncher _browser;

        public PlatformServices(Context context)
        {
            _secrets = new AndroidSecretStore(context);
            _snapshots = new AudibleIdentitySnapshotStore(_secrets);
            var authenticator = new AudibleAccountAuthenticator(_snapshots, new SystemDateTime());
            _connection = new AccountConnectionService(authenticator, new AccountSessionStore(_secrets));
            _browser = new AndroidExternalBrowserLauncher(context);
        }

        public AudibleSignInViewModel CreateSignInViewModel()
            => new(_connection, _browser);

        public ILibraryCatalogProvider CreateCatalogProvider()
            => new SavedIdentityAudibleCatalogProvider(_snapshots);
    }
}
