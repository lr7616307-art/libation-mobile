using Android.App;
using Android.Content;
using Android.OS;
using Libation.Mobile.Core;
using Libation.Mobile.Audible;
using DownloadStatus = Libation.Mobile.Core.DownloadStatus;
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

    private static AndroidWidevineOfflineDownloadService? currentService;

    public static bool IsDownloadActive => currentService is { } service &&
        (service.isTransitioning || service.runTask is { IsCompleted: false });

    private CancellationTokenSource? runCancellation;
    private Task? runTask;
    private string? activeAsin;
    private string? activeTitle;
    private bool userRequestedPause;
    private bool resumeRequestedWhileStopping;
    private bool isTransitioning;
    private bool queuedStartReceivedWhileTransitioning;
    private JsonWidevineOfflineDownloadStateStore? recoveryStore;
    private const string RecoveryFileName = "vorath-widevine-download-recovery-v1.json";

    private JsonWidevineOfflineDownloadStateStore RecoveryStore
        => recoveryStore ??= new JsonWidevineOfflineDownloadStateStore(
            Path.Combine(FilesDir!.AbsolutePath!, RecoveryFileName));

    public static Func<CancellationToken, Task<IAudibleWidevinePlaybackCoordinator?>>? CoordinatorFactory { get; set; }
    public static Func<JsonDownloadQueue?>? DownloadQueueFactory { get; set; }

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

    public static void Pause(Context context, string asin, string title)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(asin);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        if (!IsValidAsin(asin))
            throw new ArgumentException("The Audible audiobook identifier is invalid.", nameof(asin));

        var intent = new Intent(context, typeof(AndroidWidevineOfflineDownloadService));
        intent.SetAction(ActionPause);
        intent.PutExtra(ExtraAsin, asin);
        intent.PutExtra(ExtraTitle, title);
        context.StartService(intent);
    }

    public override void OnCreate()
    {
        base.OnCreate();
        currentService = this;
        EnsureNotificationChannel();
    }

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        var incomingAsin = intent?.GetStringExtra(ExtraAsin);
        var incomingTitle = intent?.GetStringExtra(ExtraTitle);

        if (intent?.Action == ActionPause)
        {
            if (isTransitioning)
                return StartCommandResult.NotSticky;

            // Never let a Pause action for a queued second title cancel the title
            // currently using the singleton foreground service.
            if (runTask is { IsCompleted: false } &&
                !string.Equals(incomingAsin, activeAsin, StringComparison.OrdinalIgnoreCase))
                return StartCommandResult.NotSticky;

            if (!string.IsNullOrWhiteSpace(incomingAsin)) activeAsin = incomingAsin;
            if (!string.IsNullOrWhiteSpace(incomingTitle)) activeTitle = incomingTitle;
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

        if (intent?.Action != ActionStart || !IsValidAsin(incomingAsin) ||
            string.IsNullOrWhiteSpace(incomingTitle))
        {
            StartForeground(
                NotificationId,
                BuildNotification("Could not start offline download.", null, false, false, null));
            StopForeground(StopForegroundFlags.Detach);
            StopSelf(startId);
            return StartCommandResult.NotSticky;
        }

        if (isTransitioning)
        {
            // A title can be enqueued while the previous task is unwinding.
            // The queue pump will re-read the durable queue after this transition.
            queuedStartReceivedWhileTransitioning = true;
            return StartCommandResult.NotSticky;
        }

        if (runTask is { IsCompleted: false })
        {
            if (userRequestedPause &&
                string.Equals(incomingAsin, activeAsin, StringComparison.OrdinalIgnoreCase))
                resumeRequestedWhileStopping = true;
            else if (userRequestedPause)
                queuedStartReceivedWhileTransitioning = true;

            // A different Audible title remains safely queued. Do not overwrite
            // activeAsin/title while the service is still processing the first.
            return StartCommandResult.NotSticky;
        }

        activeAsin = incomingAsin;
        activeTitle = incomingTitle;
        userRequestedPause = false;
        resumeRequestedWhileStopping = false;
        runCancellation?.Dispose();
        runCancellation = new CancellationTokenSource();
        StartForeground(
            NotificationId,
            BuildNotification("Preparing Audible offline authorization…", null, true, true, "Pause"));
        runTask = RunDownloadAsync(startId, incomingAsin!, incomingTitle!, runCancellation.Token);
        return StartCommandResult.RedeliverIntent;
    }

    private async Task RunDownloadAsync(
        int startId,
        string asin,
        string title,
        CancellationToken cancellationToken)
    {
        var advanceQueue = false;
        var failureStage = "initialization";
        try
        {
            failureStage = "saving download state";
            await RecoveryStore.MarkDownloadingAsync(asin, title, CancellationToken.None).ConfigureAwait(false);
            await UpdateQueueStatusAsync(asin, DownloadStatus.Downloading).ConfigureAwait(false);

            // The atomic recovery record is written before the queue mirror.
            // If Android kills the process between those writes, restore the
            // authoritative byte count into the visible queue on the next run.
            var recoveredState = await RecoveryStore.GetAsync(cancellationToken).ConfigureAwait(false);
            await RestoreQueueProgressAsync(asin, recoveredState).ConfigureAwait(false);
            var recoveredPercentage = recoveredState?.Percentage is { } savedPercentage
                ? (int)Math.Clamp(savedPercentage, 0d, 100d)
                : (int?)null;
            var recoveredMessage = recoveredState?.BytesDownloaded > 0
                ? $"Resuming with {FormatBytes(recoveredState.BytesDownloaded)} of saved encrypted segments…"
                : "Preparing Audible offline authorization…";
            UpdateNotification(
                recoveredMessage,
                recoveredPercentage,
                indeterminate: recoveredPercentage is null,
                ongoing: true,
                actionLabel: "Pause");

            failureStage = "restoring Audible account and Android DRM session";
            var factory = CoordinatorFactory
                ?? throw new InvalidOperationException("The Widevine offline service is not configured.");
            var coordinator = await factory(cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Sign in to Audible again before downloading.");

            failureStage = "reconciling saved offline licence and media";
            var lastPersistedBytes = 0L;
            var lastPersistedAt = DateTimeOffset.MinValue;
            var transferProgress = new InlineProgress<WidevineOfflineDownloadProgress>(value =>
            {
                var detail = value.ContentLength is > 0
                    ? $\"{FormatBytes(value.BytesDownloaded)} of {FormatBytes(value.ContentLength.Value)}\"
                    : $\"{FormatBytes(value.BytesDownloaded)} received\";
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
            var stageProgress = new InlineProgress<AudibleOfflineDownloadStage>(value =>
            {
                failureStage = value.Stage;
                UpdateNotification(
                    value.Message,
                    value.Stage == "completed" ? 100 : null,
                    indeterminate: value.Stage != "completed",
                    ongoing: value.Stage != "completed",
                    actionLabel: value.Stage == "completed" ? null : "Pause");
            });

            // A single pipeline owns the complete lifecycle: reconcile durable
            // state, acquire an Audible-issued offline grant, transfer encrypted
            // media, and verify both before the queue can become Protected.
            await new AudibleOfflineDownloadPipeline().ExecuteAsync(
                asin,
                coordinator,
                stageProgress,
                transferProgress,
                cancellationToken,
                IsRetryableMediaFailure).ConfigureAwait(false);

            failureStage = "saving verified download completion";
            await RecoveryStore.MarkCompletedAsync(asin, CancellationToken.None).ConfigureAwait(false);
            await UpdateQueueStatusAsync(asin, DownloadStatus.Protected).ConfigureAwait(false);
            MainView.PlatformWidevineOfflineDownloadCompletedAction?.Invoke(asin);
            advanceQueue = true;
            FinishWithNotification(
                startId,
                "Offline download verified. Open this title in VØRATH to play it.",
                100,
                actionLabel: null,
                stopService: false);
        }
        catch (System.OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Only an explicit notification Pause is recorded as user-paused.
            // Unexpected service/process teardown leaves Downloading persisted so
            // the next foreground app launch can safely recover it.
            if (userRequestedPause)
                await PersistPausedAsync(asin).ConfigureAwait(false);

            if (resumeRequestedWhileStopping)
            {
                UpdateNotification(
                    "Resuming Audible offline download…",
                    null,
                    indeterminate: true,
                    ongoing: true,
                    actionLabel: "Pause");
            }
            else
            {
                FinishWithNotification(
                    startId,
                    "Paused. Downloaded DASH segments are preserved; tap Resume to continue.",
                    null,
                    actionLabel: "Resume",
                    stopService: false);
            }
        }
        catch (Exception exception)
        {
            advanceQueue = true;
            var safeMessage = SafeFailureMessage(exception, failureStage);
            try { await RecoveryStore.MarkFailedAsync(asin, safeMessage, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception persistenceException) { System.Diagnostics.Debug.WriteLine($"Could not persist Widevine failure state: {persistenceException.Message}"); }
            await UpdateQueueStatusAsync(asin, DownloadStatus.Failed, safeMessage).ConfigureAwait(false);
            FinishWithNotification(
                startId,
                safeMessage,
                null,
                actionLabel: "Retry",
                stopService: false);
        }
        finally
        {
            isTransitioning = true;
            try
            {
                var shouldResume = resumeRequestedWhileStopping &&
                    string.Equals(activeAsin, asin, StringComparison.OrdinalIgnoreCase);
                var explicitQueuedStart = queuedStartReceivedWhileTransitioning;
                var shouldPumpQueue = advanceQueue || explicitQueuedStart;
                var mayAdvanceQueue = !userRequestedPause || explicitQueuedStart;
                resumeRequestedWhileStopping = false;
                queuedStartReceivedWhileTransitioning = false;
                runTask = null;
                runCancellation?.Dispose();
                runCancellation = null;

                // A very fast Resume tap can arrive before cancellation has unwound.
                // Queue it here instead of dropping the user's action.
                if (shouldResume && IsValidAsin(asin))
                {
                    userRequestedPause = false;
                    runCancellation = new CancellationTokenSource();
                    StartForeground(
                        NotificationId,
                        BuildNotification("Resuming Audible offline download…", null, true, true, "Pause"));
                    runTask = RunDownloadAsync(startId, asin, title, runCancellation.Token);
                }
                else
                {
                    var startedNext = false;
                    if (shouldPumpQueue && mayAdvanceQueue)
                    {
                        startedNext = await StartNextQueuedProviderDownloadAsync(startId).ConfigureAwait(false);
                        // A Start intent may arrive while the first queue snapshot is
                        // being read. Its item was already persisted by the UI, so read
                        // the queue once more before deciding the service is idle.
                        if (!startedNext && queuedStartReceivedWhileTransitioning)
                        {
                            queuedStartReceivedWhileTransitioning = false;
                            startedNext = await StartNextQueuedProviderDownloadAsync(startId).ConfigureAwait(false);
                        }
                    }

                    if (!startedNext)
                        StopSelf();
                }
            }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine($"Could not advance the Audible queue: {exception.Message}");
                StopSelf();
            }
            finally
            {
                isTransitioning = false;
            }
        }
    }

    private async Task<bool> StartNextQueuedProviderDownloadAsync(int startId)
    {
        var queue = DownloadQueueFactory?.Invoke();
        if (queue is null)
            return false;

        var items = await queue.GetItemsAsync(CancellationToken.None).ConfigureAwait(false);
        var next = AudibleProviderQueueScheduler.SelectNext(
            items,
            string.IsNullOrWhiteSpace(activeAsin) ? null : "provider:" + activeAsin);
        if (next is null)
            return false;

        var asin = next.AudiobookId["provider:".Length..];
        if (!IsValidAsin(asin))
            return false;

        activeAsin = asin;
        activeTitle = string.IsNullOrWhiteSpace(next.DisplayTitle)
            ? "Audible audiobook"
            : next.DisplayTitle.Trim();
        userRequestedPause = false;
        resumeRequestedWhileStopping = false;
        runCancellation = new CancellationTokenSource();
        StartForeground(
            NotificationId,
            BuildNotification("Preparing next queued Audible download…", null, true, true, "Pause"));
        runTask = RunDownloadAsync(startId, asin, activeTitle, runCancellation.Token);
        return true;
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
            // MainActivity invokes recovery from OnResume. Never interpret an
            // already-running download as a process-death recovery: doing so
            // during a Pause transition could immediately resume that title.
            if (IsDownloadActive)
                return;

            var path = Path.Combine(context.FilesDir!.AbsolutePath!, RecoveryFileName);
            var state = await new JsonWidevineOfflineDownloadStateStore(path)
                .GetAsync(CancellationToken.None).ConfigureAwait(false);
            if (state?.ShouldAutoRecover == true)
            {
                Start(context, state.Asin, state.Title);
                return;
            }

            // An explicit Pause blocks automatic queue advancement. Otherwise,
            // recover a queued item if the process died between two transfers.
            if (state?.Status == WidevineOfflineDownloadStatus.Paused)
                return;

            var queue = DownloadQueueFactory?.Invoke();
            if (queue is null)
                return;
            var next = AudibleProviderQueueScheduler.SelectNext(
                await queue.GetItemsAsync(CancellationToken.None).ConfigureAwait(false));
            if (next is not null)
            {
                var asin = next.AudiobookId["provider:".Length..];
                if (IsValidAsin(asin))
                    Start(context, asin, string.IsNullOrWhiteSpace(next.DisplayTitle)
                        ? "Audible audiobook"
                        : next.DisplayTitle);
            }
        }
        catch (Exception exception)
        {
            // A damaged recovery record must never prevent the app from opening.
            System.Diagnostics.Debug.WriteLine($"Widevine download recovery was skipped: {exception.Message}");
        }
    }

    private async Task RestoreQueueProgressAsync(
        string asin,
        WidevineOfflineDownloadState? state)
    {
        if (state is null || state.BytesDownloaded <= 0)
            return;

        try
        {
            var queue = DownloadQueueFactory?.Invoke();
            if (queue is null)
                return;

            await queue.UpdateProgressAsync(
                "provider:" + asin,
                state.BytesDownloaded,
                state.ContentLength,
                CancellationToken.None).ConfigureAwait(false);
            MainView.PlatformWidevineOfflineDownloadQueueChangedAction?.Invoke();
        }
        catch (Exception exception)
        {
            // The protected transfer can resume from the recovery record even
            // if its visible queue mirror needs another refresh later.
            System.Diagnostics.Debug.WriteLine($"Could not restore Audible queue progress: {exception.Message}");
        }
    }

    private async Task PersistProgressAsync(string asin, WidevineOfflineDownloadProgress value)
    {
        try
        {
            await RecoveryStore.MarkProgressAsync(
                asin, value.BytesDownloaded, value.ContentLength, CancellationToken.None).ConfigureAwait(false);
            var queue = DownloadQueueFactory?.Invoke();
            if (queue is not null)
            {
                await queue.UpdateProgressAsync(
                    "provider:" + asin, value.BytesDownloaded, value.ContentLength, CancellationToken.None).ConfigureAwait(false);
                MainView.PlatformWidevineOfflineDownloadQueueChangedAction?.Invoke();
            }
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
        await UpdateQueueStatusAsync(asin, DownloadStatus.Paused).ConfigureAwait(false);
    }

    private async Task UpdateQueueStatusAsync(
        string asin,
        DownloadStatus status,
        string? errorMessage = null)
    {
        try
        {
            var queue = DownloadQueueFactory?.Invoke();
            if (queue is null)
                return;

            var audiobookId = "provider:" + asin;
            var item = (await queue.GetItemsAsync(CancellationToken.None))
                .FirstOrDefault(candidate => string.Equals(
                    candidate.AudiobookId, audiobookId, StringComparison.Ordinal));
            if (item is null)
            {
                await queue.EnqueueAsync(audiobookId, CancellationToken.None);
                item = (await queue.GetItemsAsync(CancellationToken.None))
                    .First(candidate => string.Equals(
                        candidate.AudiobookId, audiobookId, StringComparison.Ordinal));
            }

            if (item.Status == DownloadStatus.Failed && status == DownloadStatus.Downloading)
            {
                await queue.EnqueueAsync(audiobookId, CancellationToken.None);
                item = (await queue.GetItemsAsync(CancellationToken.None))
                    .First(candidate => string.Equals(
                        candidate.AudiobookId, audiobookId, StringComparison.Ordinal));
            }

            if (item.Status != status)
                await queue.UpdateStatusAsync(
                    audiobookId, status, errorMessage, CancellationToken.None);

            MainView.PlatformWidevineOfflineDownloadQueueChangedAction?.Invoke();
        }
        catch (Exception exception)
        {
            // The download must not be cancelled because a queue presentation update failed.
            System.Diagnostics.Debug.WriteLine($"Could not update Audible queue state: {exception.Message}");
        }
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

    private static string SafeFailureMessage(Exception exception, string stage)
    {
        // Prefer structured, allow-listed provider denial categories. Never surface
        // Audible's raw exception message because it may contain account identifiers,
        // signed URLs, DRM challenges or response details.
        var providerMessage = AudibleLicenseFailureClassifier.GetSafeUserMessage(exception);
        if (!string.IsNullOrWhiteSpace(providerMessage))
            return $"{providerMessage} (stage: {stage}).";

        // Show only exception type and an HTTP status number. Do not expose raw
        // exception text, request URLs, response bodies, account data or DRM values.
        var exceptionType = exception.GetType().Name;
        if (exceptionType.Length > 48 ||
            exceptionType.Any(character => !char.IsAsciiLetterOrDigit(character)))
            exceptionType = "DownloadException";

        var status = FindHttpStatusCode(exception);
        var statusText = status is { } code ? $", HTTP {code}" : string.Empty;
        return $"Offline download failed at {stage} ({exceptionType}{statusText}). " +
               "Verified encrypted segments are preserved; tap Retry.";
    }

    private static int? FindHttpStatusCode(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is HttpRequestException request && request.StatusCode is { } status)
                return (int)status;
        }
        return null;
    }

    private void FinishWithNotification(
        int startId,
        string message,
        int? percentage,
        string? actionLabel,
        bool stopService = true)
    {
        UpdateNotification(message, percentage, indeterminate: false, ongoing: false, actionLabel);
        StopForeground(StopForegroundFlags.Detach);
        // The queue pump keeps the same service alive across serialized items.
        // Terminal paths outside the pump still stop the detached service.
        if (stopService)
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
        if (ReferenceEquals(currentService, this))
            currentService = null;
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
