using Android.App;
using Android.Content;
using Android.OS;
using Libation.Mobile.Core;
using Libation.Mobile.UI.Views;

namespace Libation.Mobile.Android;

[Service(Name = "org.libationmobile.app.AndroidAudibleOfflineDownloadService", Exported = false)]
public sealed class AndroidAudibleOfflineDownloadService : Service
{
    private const string ChannelId = "vorath_audible_downloads";
    private const int NotificationId = 2410;
    private const string ActionStart = "org.libationmobile.app.action.AUDIBLE_DOWNLOAD_START";
    private const string ActionPause = "org.libationmobile.app.action.AUDIBLE_DOWNLOAD_PAUSE";
    private const string ExtraAsin = "asin";
    private const string ExtraTitle = "title";

    private CancellationTokenSource? _runCancellation;
    private Task? _runTask;
    private string? _activeAsin;
    private string? _activeTitle;

    public static Func<CancellationToken, Task<IAudibleWidevinePlaybackCoordinator?>>? CoordinatorFactory { get; set; }

    public static void Start(Context context, string asin, string title)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(asin);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);

        var intent = new Intent(context, typeof(AndroidAudibleOfflineDownloadService));
        intent.SetAction(ActionStart);
        intent.PutExtra(ExtraAsin, asin);
        intent.PutExtra(ExtraTitle, title);
        StartAndroidService(context, intent);
    }

    private static void StartAndroidService(Context context, Intent intent)
    {
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
        var asin = intent?.GetStringExtra(ExtraAsin);
        var title = intent?.GetStringExtra(ExtraTitle);
        if (!string.IsNullOrWhiteSpace(asin)) _activeAsin = asin;
        if (!string.IsNullOrWhiteSpace(title)) _activeTitle = title;

        if (intent?.Action == ActionPause)
        {
            StartForeground(
                NotificationId,
                BuildNotification(
                    _activeTitle ?? "Audible audiobook",
                    "Pausing download…",
                    null,
                    indeterminate: true,
                    ongoing: true,
                    actionLabel: null));
            try { _runCancellation?.Cancel(); }
            catch (ObjectDisposedException) { }

            if (_runTask is null)
                FinishWithNotification(
                    startId,
                    "Download paused. Tap Resume to continue.",
                    null,
                    actionLabel: "Resume");
            return StartCommandResult.NotSticky;
        }

        if (intent?.Action != ActionStart ||
            !IsValidAsin(_activeAsin) ||
            string.IsNullOrWhiteSpace(_activeTitle))
        {
            StartForeground(
                NotificationId,
                BuildNotification(
                    "VØRATH downloads",
                    "Could not start this Audible download.",
                    null,
                    indeterminate: false,
                    ongoing: false,
                    actionLabel: null));
            StopForeground(StopForegroundFlags.Detach);
            StopSelf(startId);
            return StartCommandResult.NotSticky;
        }

        if (_runTask is { IsCompleted: false })
            return StartCommandResult.NotSticky;

        _runCancellation?.Dispose();
        _runCancellation = new CancellationTokenSource();
        StartForeground(
            NotificationId,
            BuildNotification(
                _activeTitle!,
                "Preparing Audible offline download…",
                null,
                indeterminate: true,
                ongoing: true,
                actionLabel: "Pause"));

        _runTask = RunDownloadAsync(startId, _activeAsin!, _activeTitle!, _runCancellation.Token);
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
            var coordinator = await (CoordinatorFactory?.Invoke(cancellationToken)
                ?? Task.FromResult<IAudibleWidevinePlaybackCoordinator?>(null));
            if (coordinator is null)
                throw new InvalidOperationException("Audible account session unavailable.");

            UpdateNotification(
                title,
                "Checking Audible offline authorization…",
                null,
                indeterminate: true,
                ongoing: true,
                actionLabel: "Pause");

            if (!await coordinator.HasOfflineLicenseAsync(asin, cancellationToken))
            {
                UpdateNotification(
                    title,
                    "Requesting Audible offline licence…",
                    null,
                    indeterminate: true,
                    ongoing: true,
                    actionLabel: "Pause");
                await coordinator.AcquireOfflineLicenseAsync(asin, cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (!await coordinator.HasOfflineMediaAsync(asin, cancellationToken))
            {
                UpdateNotification(
                    title,
                    "Downloading encrypted audio…",
                    null,
                    indeterminate: true,
                    ongoing: true,
                    actionLabel: "Pause");

                var progress = new Progress<AudioDownloadProgress>(value =>
                {
                    var detail = value.TotalBytes is > 0
                        ? $"{FormatBytes(value.BytesReceived)} of {FormatBytes(value.TotalBytes.Value)}"
                        : $"{FormatBytes(value.BytesReceived)} received";
                    UpdateNotification(
                        title,
                        detail,
                        value.Percentage is { } percent ? (int)Math.Clamp(percent, 0d, 100d) : null,
                        indeterminate: value.Percentage is null,
                        ongoing: true,
                        actionLabel: "Pause");
                });

                await coordinator.DownloadOfflineMediaAsync(asin, progress, cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (!await coordinator.HasOfflineMediaAsync(asin, cancellationToken))
                throw new InvalidDataException("Audible's encrypted offline audio did not pass its cache integrity check.");

            MainView.PlatformAudibleDownloadCompletedAction?.Invoke(asin);
            FinishWithNotification(
                startId,
                "Download complete. This audiobook is ready to play offline in VØRATH.",
                100,
                actionLabel: null);
        }
        catch (System.OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            FinishWithNotification(
                startId,
                "Download paused. Completed encrypted segments are kept; tap Resume to continue.",
                null,
                actionLabel: "Resume");
        }
        catch (NotSupportedException)
        {
            FinishWithNotification(
                startId,
                "Audible did not grant Widevine offline media for this title or device. No DRM fallback was used.",
                null,
                actionLabel: "Retry");
        }
        catch (Exception)
        {
            FinishWithNotification(
                startId,
                "Download could not finish. Tap Retry to continue. VØRATH keeps valid encrypted cache segments.",
                null,
                actionLabel: "Retry");
        }
        finally
        {
            _runTask = null;
            _runCancellation?.Dispose();
            _runCancellation = null;
        }
    }

    private void FinishWithNotification(int startId, string text, int? percentage, string? actionLabel)
    {
        UpdateNotification(
            _activeTitle ?? "Audible audiobook",
            text,
            percentage,
            indeterminate: false,
            ongoing: false,
            actionLabel: actionLabel);
        StopForeground(StopForegroundFlags.Detach);
        StopSelf(startId);
    }

    private void UpdateNotification(
        string title,
        string text,
        int? percentage,
        bool indeterminate,
        bool ongoing,
        string? actionLabel)
    {
        var manager = (NotificationManager?)GetSystemService(NotificationService);
        manager?.Notify(
            NotificationId,
            BuildNotification(title, text, percentage, indeterminate, ongoing, actionLabel));
    }

    private Notification BuildNotification(
        string title,
        string text,
        int? percentage,
        bool indeterminate,
        bool ongoing,
        string? actionLabel)
    {
        var builder = new Notification.Builder(this, ChannelId)
            .SetSmallIcon(Resource.Drawable.vorath_notification)
            .SetContentTitle(title.Length > 64 ? $"VØRATH · {title[..64]}" : $"VØRATH · {title}")
            .SetContentText(text.Length > 180 ? text[..180] : text)
            .SetOnlyAlertOnce(true)
            .SetOngoing(ongoing);

        if (indeterminate)
            builder.SetProgress(0, 0, true);
        else if (percentage is { } value)
            builder.SetProgress(100, Math.Clamp(value, 0, 100), false);

        if (!string.IsNullOrWhiteSpace(actionLabel))
        {
            var action = new Intent(this, typeof(AndroidAudibleOfflineDownloadService));
            action.SetAction(actionLabel == "Pause" ? ActionPause : ActionStart);
            action.PutExtra(ExtraAsin, _activeAsin);
            action.PutExtra(ExtraTitle, _activeTitle);

            var pendingIntent = PendingIntent.GetService(
                this,
                actionLabel == "Pause" ? 2411 : 2412,
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
            "Audible audiobook downloads",
            NotificationImportance.Low)
        {
            Description = "Download progress and pause/resume controls for VØRATH"
        });
    }

    public override void OnDestroy()
    {
        try { _runCancellation?.Cancel(); }
        catch (ObjectDisposedException) { }
        base.OnDestroy();
    }

    public override IBinder? OnBind(Intent? intent) => null;

    private static bool IsValidAsin(string? asin)
        => asin is { Length: 10 } && asin.All(char.IsAsciiLetterOrDigit);

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024d:0.0} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024d * 1024):0.0} MB";
        return $"{bytes / (1024d * 1024 * 1024):0.00} GB";
    }
}
