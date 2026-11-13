using Android.App;
using Android.Content;
using Android.OS;
using Libation.Mobile.Core;
using Libation.Mobile.UI.Views;

namespace Libation.Mobile.Android;

/// <summary>
/// Keeps the provider-authorized Widevine offline download alive when VØRATH
/// leaves the foreground. The media remains encrypted in Media3's app-private
/// cache and the licence remains managed by Android MediaDrm.
/// </summary>
[Service(
    Name = "org.libationmobile.app.AndroidWidevineOfflineDownloadService",
    Exported = false)]
public sealed class AndroidWidevineOfflineDownloadService : Service
{
    private const string ChannelId = "vorath_widevine_downloads";
    private const int NotificationId = 2420;
    private const string ActionStart = "org.libationmobile.app.action.WIDEVINE_DOWNLOAD_START";
    private const string ActionPause = "org.libationmobile.app.action.WIDEVINE_DOWNLOAD_PAUSE";
    private const string ExtraAsin = "asin";
    private const string ExtraTitle = "title";

    private CancellationTokenSource? runCancellation;
    private Task? runTask;
    private string? activeAsin;
    private string? activeTitle;
    private bool userRequestedPause;
    private JsonWidevineOfflineDownloadStateStore? recoveryStore;
    private const string RecoveryFileName = "vorath-widevine-download-recovery-v1.json";

    private JsonWidevineOfflineDownloadStateStore RecoveryStore
        => recoveryStore ??= new JsonWidevineOfflineDownloadStateStore(
            Path.Combine(FilesDir!.AbsolutePath!, RecoveryFileName));

    public static Func<CancellationToken, Task<IAudibleWidevinePlaybackCoordinator?>>? CoordinatorFactory { get; set; }

    public static void Start(Context context, string asin, string title)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(asin);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        if (!IsValidAsin(asin))
            throw new ArgumentException("The Audible audiobook identifier is invalid.", nameof(asin));

        var intent = new Intent(context, typeof(AndroidWidevineOfflineDownloadService));
        intent.SetAction(ActionStart);
        intent.PutExtra(ExtraAsin, asin);
        intent.PutExtra(ExtraTitle, title);
        if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
            context.StartForegroundService(intent);
        else
            context.StartService(intent);
    }

    public override void OnCreate()
    {
        base.OnCreate();
        EnsureNotificationChannel();
    }

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        var incomingAsin = intent?.GetStringExtra(ExtraAsin);
        var incomingTitle = intent?.GetStringExtra(ExtraTitle);
        if (!string.IsNullOrWhiteSpace(incomingAsin)) activeAsin = incomingAsin;
        if (!string.IsNullOrWhiteSpace(incomingTitle)) activeTitle = incomingTitle;

        if (intent?.Action == ActionPause)
        {
            userRequestedPause = true;
            UpdateNotification(
                "Pausing offline download…",
                null,
                indeterminate: true,
                ongoing: true,
                actionLabel: null);
            try { runCancellation?.Cancel(); }
            catch (ObjectDisposedException) { }

            if (runTask is null || runTask.IsCompleted)
            {
                _ = PersistPausedAsync(activeAsin);
                FinishWithNotification(
                    startId,
                    "Paused. Downloaded DASH segments are preserved; tap Resume to continue.",
                    null,
                    actionLabel: "Resume");
            }
            return StartCommandResult.NotSticky;
        }

        if (intent?.Action != ActionStart || !IsValidAsin(activeAsin) || string.IsNullOrWhiteSpace(activeTitle))
        {
            StartForeground(
                NotificationId,
                BuildNotification("Could not start offline download.", null, false, false, null));
            StopForeground(StopForegroundFlags.Detach);
            StopSelf(startId);
            return StartCommandResult.NotSticky;
        }

        if (runTask is { IsCompleted: false })
            return StartCommandResult.NotSticky;

        userRequestedPause = false;
        runCancellation?.Dispose();
        runCancellation = new CancellationTokenSource();
        StartForeground(
            NotificationId,
            BuildNotification("Preparing Audible offline authorization…", null, true, true, "Pause"));
        runTask = RunDownloadAsync(startId, activeAsin!, activeTitle!, runCancellation.Token);
        return StartCommandResult.RedeliverIntent;
    }

    private async Task RunDownloadAsync(
        int startId,
        string asin,
        string title,
        CancellationToken cancellationToken)
    {
        try
        {
            await RecoveryStore.MarkDownloadingAsync(asin, title, CancellationToken.None).ConfigureAwait(false);
            var factory = CoordinatorFactory
                ?? throw new InvalidOperationException("The Widevine offline service is not configured.");
            var coordinator = await factory(cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Sign in to Audible again before downloading.");

            if (await coordinator.HasOfflineMediaAsync(asin, cancellationToken).ConfigureAwait(false))
            {
                await RecoveryStore.MarkCompletedAsync(asin, CancellationToken.None).ConfigureAwait(false);
                MainView.PlatformWidevineOfflineDownloadCompletedAction?.Invoke(asin);
                FinishWithNotification(
                    startId,
                    "This audiobook is already verified for offline playback in VØRATH.",
                    100,
                    actionLabel: null);
                return;
            }

            if (!await coordinator.HasOfflineLicenseAsync(asin, cancellationToken).ConfigureAwait(false))
            {
                UpdateNotification(
                    "Requesting Audible-authorized Widevine offline licence…",
                    null,
                    indeterminate: true,
                    ongoing: true,
                    actionLabel: "Pause");
                await coordinator.AcquireOfflineLicenseAsync(asin, cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            UpdateNotification(
                "Audible licence granted. Preparing encrypted audio transfer…",
                null,
                indeterminate: true,
                ongoing: true,
                actionLabel: "Pause");

            var lastPersistedBytes = 0L;
            var lastPersistedAt = DateTimeOffset.MinValue;
            var progress = new InlineProgress<WidevineOfflineDownloadProgress>(value =>
            {
                var detail = value.ContentLength is > 0
                    ? $"{FormatBytes(value.BytesDownloaded)} of {FormatBytes(value.ContentLength.Value)}"
                    : $"{FormatBytes(value.BytesDownloaded)} received";
                UpdateNotification(
                    detail,
                    value.Percentage is { } percentage ? (int)Math.Clamp(percentage, 0d, 100d) : null,
                    indeterminate: value.Percentage is null,
                    ongoing: true,
                    actionLabel: "Pause");

                var now = DateTimeOffset.UtcNow;
                if (value.BytesDownloaded - lastPersistedBytes >= 1_048_576 ||
                    (now - lastPersistedAt).TotalSeconds >= 2 ||
                    value.Percentage is >= 100d)
                {
                    lastPersistedBytes = Math.Max(lastPersistedBytes, value.BytesDownloaded);
                    lastPersistedAt = now;
                    _ = PersistProgressAsync(asin, value);
                }
            });

            // Retry only transient transport errors. Media3's cache keeps completed
            // encrypted segments, and the coordinator obtains a fresh manifest on
            // each attempt, so the next pass resumes without requesting a new licence.
            var retryNumber = 0;
            while (true)
            {
                try
                {
                    await coordinator.DownloadOfflineMediaWithProgressAsync(
                        asin, progress, cancellationToken).ConfigureAwait(false);
                    break;
                }
                catch (Exception exception) when (
                    !cancellationToken.IsCancellationRequested &&
                    retryNumber < OfflineDownloadRetryPolicy.MaximumAutomaticRetries &&
                    IsRetryableMediaFailure(exception))
                {
                    retryNumber++;
                    var delay = OfflineDownloadRetryPolicy.GetDelay(retryNumber);
                    UpdateNotification(
                        $"Connection interrupted. Reusing saved encrypted segments; retry {retryNumber}/{OfflineDownloadRetryPolicy.MaximumAutomaticRetries} in {delay.TotalSeconds:0}s…",
                        null,
                        indeterminate: true,
                        ongoing: true,
                        actionLabel: "Pause");
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!await coordinator.HasOfflineLicenseAsync(asin, cancellationToken).ConfigureAwait(false) ||
                !await coordinator.HasOfflineMediaAsync(asin, cancellationToken).ConfigureAwait(false))
                throw new InvalidDataException(
                    "Android could not verify both the offline licence and the complete cached audiobook.");

            await RecoveryStore.MarkCompletedAsync(asin, CancellationToken.None).ConfigureAwait(false);
            MainView.PlatformWidevineOfflineDownloadCompletedAction?.Invoke(asin);
            FinishWithNotification(
                startId,
                "Offline download verified. Open this title in VØRATH to play it.",
                100,
                actionLabel: null);
        }
        catch (System.OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Only an explicit notification Pause is recorded as user-paused.
            // Unexpected service/process teardown leaves Downloading persisted so
            // the next foreground app launch can safely recover it.
            if (userRequestedPause)
                await PersistPausedAsync(asin).ConfigureAwait(false);
            FinishWithNotification(
                startId,
                "Paused. Downloaded DASH segments are preserved; tap Resume to continue.",
                null,
                actionLabel: "Resume");
        }
        catch (Exception exception)
        {
            var safeMessage = SafeFailureMessage(exception);
            try { await RecoveryStore.MarkFailedAsync(asin, safeMessage, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception persistenceException) { System.Diagnostics.Debug.WriteLine($"Could not persist Widevine failure state: {persistenceException.Message}"); }
            FinishWithNotification(
                startId,
                safeMessage,
                null,
                actionLabel: "Retry");
        }
        finally
        {
            runTask = null;
            runCancellation?.Dispose();
            runCancellation = null;
        }
    }

    public static void RecoverInterrupted(Context context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var appContext = context.ApplicationContext ?? context;
        _ = RecoverInterruptedCoreAsync(appContext);
    }

    private static async Task RecoverInterruptedCoreAsync(Context context)
    {
        try
        {
            var path = Path.Combine(context.FilesDir!.AbsolutePath!, RecoveryFileName);
            var state = await new JsonWidevineOfflineDownloadStateStore(path)
                .GetAsync(CancellationToken.None).ConfigureAwait(false);
            if (state?.ShouldAutoRecover == true)
                Start(context, state.Asin, state.Title);
        }
        catch (Exception exception)
        {
            // A damaged recovery record must never prevent the app from opening.
            System.Diagnostics.Debug.WriteLine($"Widevine download recovery was skipped: {exception.Message}");
        }
    }

    private async Task PersistProgressAsync(string asin, WidevineOfflineDownloadProgress value)
    {
        try
        {
            await RecoveryStore.MarkProgressAsync(
                asin, value.BytesDownloaded, value.ContentLength, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Could not persist Widevine progress: {exception.Message}");
        }
    }

    private async Task PersistPausedAsync(string? asin)
    {
        if (string.IsNullOrWhiteSpace(asin)) return;
        try { await RecoveryStore.MarkPausedAsync(asin, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception exception) { System.Diagnostics.Debug.WriteLine($"Could not persist Widevine pause state: {exception.Message}"); }
    }

    private static bool IsRetryableMediaFailure(Exception exception)
    {
        if (OfflineDownloadRetryPolicy.IsRetryable(exception))
            return true;

        // Media3 reports HTTP transport failures as Java IOException subclasses.
        // HTTP status failures are not retried: 401/403/404 often indicate a
        // provider denial or an expired resource, not a transient connection.
        if (exception is Java.IO.IOException)
            return !string.Equals(
                exception.GetType().Name,
                "InvalidResponseCodeException",
                StringComparison.Ordinal);

        return exception.InnerException is not null
            && IsRetryableMediaFailure(exception.InnerException);
    }

    private static string SafeFailureMessage(Exception exception)
    {
        var message = exception.Message ?? string.Empty;
        if (exception is NotSupportedException ||
            message.Contains("denied", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("did not grant", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("licence", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("license", StringComparison.OrdinalIgnoreCase))
            return "Audible did not grant a usable Widevine offline licence for this title/device. No playable download was completed.";

        if (message.Contains("https://", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("challenge", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("bearer", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("key-set", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("key set", StringComparison.OrdinalIgnoreCase))
            return "The protected offline authorization could not be completed. Sensitive provider details were hidden.";

        return "Offline download failed. Any verified cached segments are preserved; tap Retry.";
    }

    private void FinishWithNotification(
        int startId,
        string message,
        int? percentage,
        string? actionLabel)
    {
        UpdateNotification(message, percentage, indeterminate: false, ongoing: false, actionLabel);
        StopForeground(StopForegroundFlags.Detach);
        // Pause/resume starts a newer service startId. StopSelf(startId) with
        // the original run's ID would leave the detached service alive.
        StopSelf();
    }

    private void UpdateNotification(
        string message,
        int? percentage,
        bool indeterminate,
        bool ongoing,
        string? actionLabel)
    {
        var manager = (NotificationManager?)GetSystemService(NotificationService);
        manager?.Notify(
            NotificationId,
            BuildNotification(message, percentage, indeterminate, ongoing, actionLabel));
    }

    private Notification BuildNotification(
        string message,
        int? percentage,
        bool indeterminate,
        bool ongoing,
        string? actionLabel)
    {
        var builder = new Notification.Builder(this, ChannelId)
            .SetSmallIcon(Resource.Drawable.vorath_notification)
            .SetContentTitle(
                string.IsNullOrWhiteSpace(activeTitle)
                    ? "VØRATH · Audible download"
                    : "VØRATH · " + (activeTitle!.Length > 48 ? activeTitle[..48] : activeTitle))
            .SetContentText(message.Length > 180 ? message[..180] : message)
            .SetCategory(Notification.CategoryProgress)
            .SetOnlyAlertOnce(true)
            .SetOngoing(ongoing);

        if (indeterminate)
            builder.SetProgress(0, 0, true);
        else if (percentage is { } value)
            builder.SetProgress(100, Math.Clamp(value, 0, 100), false);

        if (!string.IsNullOrWhiteSpace(actionLabel))
        {
            var action = new Intent(this, typeof(AndroidWidevineOfflineDownloadService));
            action.SetAction(actionLabel == "Pause" ? ActionPause : ActionStart);
            action.PutExtra(ExtraAsin, activeAsin);
            action.PutExtra(ExtraTitle, activeTitle);
            var pendingIntent = PendingIntent.GetService(
                this,
                actionLabel == "Pause" ? 2421 : 2422,
                action,
                PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
            builder.AddAction(
                Resource.Drawable.vorath_notification,
                new Java.Lang.String(actionLabel),
                pendingIntent);
        }

        return builder.Build();
    }

    private void EnsureNotificationChannel()
    {
        if (Build.VERSION.SdkInt < BuildVersionCodes.O)
            return;
        var manager = (NotificationManager?)GetSystemService(NotificationService);
        manager?.CreateNotificationChannel(new NotificationChannel(
            ChannelId,
            "Audible offline downloads",
            NotificationImportance.Low)
        {
            Description = "Verified offline Audible download progress and Pause/Resume controls"
        });
    }

    public override void OnDestroy()
    {
        try { runCancellation?.Cancel(); }
        catch (ObjectDisposedException) { }
        base.OnDestroy();
    }

    public override IBinder? OnBind(Intent? intent) => null;

    private static bool IsValidAsin(string? asin) =>
        !string.IsNullOrWhiteSpace(asin) &&
        asin.Length == 10 &&
        asin.All(character => char.IsAsciiLetterOrDigit(character));

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024d:0.0} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024d * 1024):0.0} MB";
        return $"{bytes / (1024d * 1024 * 1024):0.00} GB";
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
