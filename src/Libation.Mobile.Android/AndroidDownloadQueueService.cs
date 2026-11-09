using Android.App;
using Android.Content;
using Android.OS;
using Libation.Mobile.Core;

namespace Libation.Mobile.Android;

/// <summary>
/// Android foreground-service host for a complete download queue run.
/// The host must supply a controller factory backed by a provider-authorized
/// download source before this service can execute downloads.
/// </summary>
[Service(Name = "org.libationmobile.app.AndroidDownloadQueueService", Exported = false)]
public sealed class AndroidDownloadQueueService : Service
{
    private const string ChannelId = "vorath_downloads";
    private const int NotificationId = 2401;
    private const string ActionCancel = "org.libationmobile.app.action.CANCEL_DOWNLOADS";

    private DownloadQueueExecutionController? _controller;
    private bool _runStarted;

    /// <summary>
    /// Configured by the Android host when an authorized download source is available.
    /// Intentionally unset by default: the generic downloader cannot resolve Audible media.
    /// </summary>
    public static Func<DownloadQueueExecutionController?>? ControllerFactory { get; set; }

    public static void Start(Context context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var intent = new Intent(context, typeof(AndroidDownloadQueueService));
        if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
            context.StartForegroundService(intent);
        else
            context.StartService(intent);
    }

    public static void Cancel(Context context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var intent = new Intent(context, typeof(AndroidDownloadQueueService));
        intent.SetAction(ActionCancel);
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
        if (intent?.Action == ActionCancel)
        {
            // Cancel() may be delivered after Android has recreated the service
            // process. Because Cancel() uses StartForegroundService on API 26+,
            // this path must also promote the service before doing any work.
            StartForeground(
                NotificationId,
                BuildNotification("Pausing download queue", 0, indeterminate: true));

            _controller?.Cancel();
            StopIfIdle(startId);
            return StartCommandResult.NotSticky;
        }

        // Android requires a foreground notification promptly after a foreground
        // service is started. The notification remains visible for the whole batch.
        StartForeground(NotificationId, BuildNotification("Preparing download queue", 0, indeterminate: true));

        if (_runStarted)
            return StartCommandResult.NotSticky;

        try
        {
            _controller = ControllerFactory?.Invoke();
        }
        catch (Exception)
        {
            // A platform composition failure must not crash the Android process.
            // Keep the service fail-closed and leave the persisted queue untouched.
            _controller = null;
        }

        if (_controller is null)
        {
            UpdateNotification(
                "Download provider unavailable",
                0,
                indeterminate: false);
            StopForegroundAndRemoveNotification();
            StopSelf(startId);
            return StartCommandResult.NotSticky;
        }

        _runStarted = true;
        _ = RunQueueAsync(startId);
        return StartCommandResult.RedeliverIntent;
    }

    private async Task RunQueueAsync(int startId)
    {
        try
        {
            var progress = new Progress<DownloadQueueProgress>(value =>
            {
                var detail = value.TotalBytes is > 0
                    ? $"{FormatBytes(value.BytesReceived)} of {FormatBytes(value.TotalBytes.Value)}"
                    : $"{FormatBytes(value.BytesReceived)} downloaded";
                UpdateNotification(
                    detail,
                    (int)Math.Clamp(value.Percentage ?? 0, 0, 100),
                    indeterminate: value.TotalBytes is null or 0);
            });

            var processed = await _controller!.RunAsync(progress);
            UpdateNotification($"Queue finished · {processed} item(s) processed", 100, indeterminate: false);
        }
        catch (System.OperationCanceledException)
        {
            UpdateNotification("Download queue paused", 0, indeterminate: false);
        }
        catch (Exception)
        {
            UpdateNotification("Download queue stopped after an unexpected error", 0, indeterminate: false);
        }
        finally
        {
            _controller = null;
            _runStarted = false;
            StopForegroundAndRemoveNotification();
            StopSelf(startId);
        }
    }

    public override void OnDestroy()
    {
        // If Android tears down this service, the worker's cancellation path
        // returns the active item to Queued and preserves its partial .part file.
        _controller?.Cancel();
        base.OnDestroy();
    }

    public override IBinder? OnBind(Intent? intent) => null;

    private void StopIfIdle(int startId)
    {
        if (!_runStarted)
        {
            StopForegroundAndRemoveNotification();
            StopSelf(startId);
        }
    }

    private void StopForegroundAndRemoveNotification()
    {
        // Android 13 introduced the flag-based overload. Keep the legacy
        // overload for older supported devices (including API 29).
        if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu)
            StopForeground(StopForegroundFlags.Remove);
        else
            StopForeground(true);
    }

    private void EnsureNotificationChannel()
    {
        if (Build.VERSION.SdkInt < BuildVersionCodes.O)
            return;

        var manager = (NotificationManager?)GetSystemService(NotificationService);
        manager?.CreateNotificationChannel(new NotificationChannel(
            ChannelId,
            "Audiobook downloads",
            NotificationImportance.Low)
        {
            Description = "Progress and controls for audiobook downloads"
        });
    }

    private Notification BuildNotification(string text, int progress, bool indeterminate)
    {
        var cancelIntent = new Intent(this, typeof(AndroidDownloadQueueService));
        cancelIntent.SetAction(ActionCancel);
        var cancelPendingIntent = PendingIntent.GetService(
            this,
            0,
            cancelIntent,
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);

        return new Notification.Builder(this, ChannelId)
            .SetSmallIcon(Resource.Mipmap.ic_launcher)
            .SetContentTitle("VØRATH downloads")
            .SetContentText(text)
            .SetOnlyAlertOnce(true)
            .SetOngoing(true)
            .SetProgress(100, progress, indeterminate)
            .AddAction(new Notification.Action.Builder(
                0,
                "Pause",
                cancelPendingIntent).Build())
            .Build();
    }

    private void UpdateNotification(string text, int progress, bool indeterminate)
    {
        var manager = (NotificationManager?)GetSystemService(NotificationService);
        manager?.Notify(NotificationId, BuildNotification(text, progress, indeterminate));
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024d:0.0} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024d * 1024):0.0} MB";
        return $"{bytes / (1024d * 1024 * 1024):0.00} GB";
    }
}
