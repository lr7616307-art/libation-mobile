using Android.Content;
using Android.Runtime;
using Avalonia.Android;
using Libation.Mobile.UI;
using Dinah.Core;
using Libation.Mobile.Audible;
using Libation.Mobile.Core;
using Libation.Mobile.UI.Views;
using Libation.Mobile.UI.ViewModels;

namespace Libation.Mobile.Android;

[Application]
public class AndroidApp : AvaloniaAndroidApplication<global::Libation.Mobile.UI.App>
{
    protected AndroidApp(nint javaReference, JniHandleOwnership transfer)
        : base(javaReference, transfer)
    {
    }

    public override void OnCreate()
    {
        base.OnCreate();

        // Record unexpected managed startup failures for diagnosis without
        // allowing optional platform services to block creation of the first view.
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            try { WriteStartupDiagnostic("UnhandledException", args.ExceptionObject as Exception); }
            catch { /* Diagnostics must never cause another crash. */ }
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            try { WriteStartupDiagnostic("UnobservedTaskException", args.Exception); }
            catch { /* Diagnostics must never cause another crash. */ }
            args.SetObserved();
        };
        MainView.PlatformStartupExceptionReporter = WriteStartupDiagnostic;

        // Do not initialize Android Keystore, Audible, or browser services in
        // Application.OnCreate. These services are created only when the user
        // opens Account / requests a library sync.
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

    private void WriteStartupDiagnostic(string stage, Exception? exception)
    {
        try
        {
            var directory = Path.Combine(
                FilesDir?.AbsolutePath ?? CacheDir?.AbsolutePath ?? "/data/local/tmp",
                "vorath-diagnostics");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "startup-crash.txt");
            File.AppendAllText(path,
                $"[{DateTimeOffset.Now:O}] {stage}: {exception?.GetType().FullName}: {exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // Diagnostics must never cause a second crash.
        }
    }

    private sealed class PlatformServices
    {
        private readonly AudibleIdentitySnapshotStore _snapshots;
        private readonly AccountConnectionService _connection;
        private readonly AndroidExternalBrowserLauncher _browser;

        public PlatformServices(Context context)
        {
            var secrets = new AndroidSecretStore(context);
            _snapshots = new AudibleIdentitySnapshotStore(secrets);
            var authenticator = new AudibleAccountAuthenticator(_snapshots, new SystemDateTime());
            _connection = new AccountConnectionService(authenticator, new AccountSessionStore(secrets));
            _browser = new AndroidExternalBrowserLauncher(context);
        }

        public AudibleSignInViewModel CreateSignInViewModel()
            => new(_connection, _browser);

        public ILibraryCatalogProvider CreateCatalogProvider()
            => new SavedIdentityAudibleCatalogProvider(_snapshots);
    }
}
