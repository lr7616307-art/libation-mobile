using Android.App;
using Android.Content;
using Android.OS;
using AndroidX.Core.App;
using Libation.Mobile.UI.Views;

namespace Libation.Mobile.Android;

/// <summary>
/// Keeps the ordinary authorized ADRM download worker alive and visible while
/// the user switches apps. Queue execution remains owned by the shared worker;
/// this service supplies the Android foreground notification and its actions.
/// </summary>
[Service(
    Name = "org.libationmobile.app.AndroidDownloadNotificationService",
    Exported = false)]
public sealed class AndroidDownloadNotificationService : Service
{
    private const string ChannelId = "vorath_downloads";
    private const int NotificationId = 2430;
    private const string ActionStart = "org.libationmobile.app.action.DOWNLOAD_NOTIFICATION_START";
    private const string ActionUpdate = "org.libationmobile.app.action.DOWNLOAD_NOTIFICATION_UPDATE";
    private const string ActionPause = "org.libationmobile.app.action.DOWNLOAD_NOTIFICATION_PAUSE";
    private const string ActionResume = "org.libationmobile.app.action.DOWNLOAD_NOTIFICATION_RESUME";
    private const string ActionPaused = "org.libationmobile.app.action.DOWNLOAD_NOTIFICATION_PAUSED";
    private const string ActionFinish = "org.libationmobile.app.action.DOWNLOAD_NOTIFICATION_FINISH";
    private const string ExtraTitle = "title";
    private const string ExtraText = "text";
    private const string ExtraPercent = "percent";
    private const string ExtraIndeterminate = "indeterminate";

    private static AndroidDownloadNotificationService? current;
    private string activeTitle = "VØRATH downloads";
    private string statusText = "Preparing download…";
    private int? percentage;
    private bool indeterminate = true;
    private bool paused;

    public static void Start(Context context, string title)
    {
        var intent = CreateIntent(context, ActionStart);
        intent.PutExtra(ExtraTitle, title);
        StartServiceCompat(context, intent);
    }

    public static void Update(Context context, string title, string text, int? percent, bool isIndeterminate)
    {
        if (current is { } service)
        {
            service.activeTitle = title;
            service.statusText = text;
            service.percentage = percent;
            service.indeterminate = isIndeterminate;
            service.paused = false;
            service.Notify("Pause");
            return;
        }

        var intent = CreateIntent(context, ActionUpdate);
        intent.PutExtra(ExtraTitle, title);
        intent.PutExtra(ExtraText, text);
        intent.PutExtra(ExtraIndeterminate, isIndeterminate);
        if (percent is { } value)
            intent.PutExtra(ExtraPercent, value);
        StartServiceCompat(context, intent);
    }

    public static void SetPaused(Context context)
    {
        if (current is { } service)
        {
            service.paused = true;
            service.statusText = "Paused · partial download data is saved";
            service.indeterminate = false;
            service.Notify("Resume");
            return;
        }

        StartServiceCompat(context, CreateIntent(context, ActionPaused));
    }

    public static void Finish(Context context)
    {
        if (current is { } service)
        {
            service.Notify(null, "Download queue finished");
            service.StopForeground(StopForegroundFlags.Detach);
            service.StopSelf();
            return;
        }

        StartServiceCompat(context, CreateIntent(context, ActionFinish));
    }

    private static Intent CreateIntent(Context context, string action)
    {
        var intent = new Intent(context, typeof(AndroidDownloadNotificationService));
        intent.SetAction(action);
        return intent;
    }

    private static void StartServiceCompat(Context context, Intent intent)
    {
        if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
            context.StartForegroundService(intent);
        else
            context.StartService(intent);
    }

    public override void OnCreate()
    {
        base.OnCreate();
        current = this;
        EnsureNotificationChannel();
    }

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        var action = intent?.Action;
        if (action == ActionFinish)
        {
            StopForeground(StopForegroundFlags.Remove);
            StopSelf(startId);
            return StartCommandResult.NotSticky;
        }

        if (action == ActionStart || action == ActionUpdate || action == ActionPaused)
        {
            activeTitle = intent?.GetStringExtra(ExtraTitle) ?? activeTitle;
            statusText = intent?.GetStringExtra(ExtraText) ??
                (action == ActionPaused ? "Paused · partial download data is saved" : statusText);
            percentage = intent?.HasExtra(ExtraPercent) == true ? intent.GetIntExtra(ExtraPercent, 0) : percentage;
            indeterminate = intent?.HasExtra(ExtraIndeterminate) == true
                ? intent.GetBooleanExtra(ExtraIndeterminate, true)
                : action != ActionPaused && percentage is null;
            paused = action == ActionPaused;
            var notification = BuildNotification(paused ? "Resume" : "Pause");
            if (action == ActionStart || current is null)
                StartForeground(NotificationId, notification);
            else
                ((NotificationManager?)GetSystemService(NotificationService))?.Notify(NotificationId, notification);
            return StartCommandResult.Sticky;
        }

        if (action == ActionPause)
        {
            paused = true;
            statusText = "Pausing · saving partial download data";
            Notify("Resume");
            MainView.PlatformDownloadQueuePauseAction?.Invoke();
            return StartCommandResult.Sticky;
        }

        if (action == ActionResume)
        {
            paused = false;
            statusText = "Resuming download…";
            Notify("Pause");
            MainView.PlatformDownloadQueueResumeAction?.Invoke();
            return StartCommandResult.Sticky;
        }

        return StartCommandResult.NotSticky;
    }

    private void Notify(string? actionLabel, string? overrideText = null)
    {
        if (overrideText is not null)
            statusText = overrideText;
        ((NotificationManager?)GetSystemService(NotificationService))?.Notify(
            NotificationId, BuildNotification(actionLabel));
    }

    private Notification BuildNotification(string? actionLabel)
    {
        var builder = new Notification.Builder(this, ChannelId)
            .SetSmallIcon(Resource.Drawable.vorath_notification)
            .SetContentTitle(activeTitle.Length > 64 ? activeTitle[..64] : activeTitle)
            .SetContentText(statusText.Length > 180 ? statusText[..180] : statusText)
            .SetCategory(Notification.CategoryProgress)
            .SetOnlyAlertOnce(true)
            .SetOngoing(actionLabel is not null);

        if (indeterminate)
            builder.SetProgress(0, 0, true);
        else if (percentage is { } value)
            builder.SetProgress(100, Math.Clamp(value, 0, 100), false);

        if (actionLabel is not null)
        {
            var action = new Intent(this, typeof(AndroidDownloadNotificationService));
            action.SetAction(actionLabel == "Pause" ? ActionPause : ActionResume);
            var pendingIntent = PendingIntent.GetService(
                this,
                actionLabel == "Pause" ? 2431 : 2432,
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
            "Audiobook downloads",
            NotificationImportance.Low)
        {
            Description = "Download progress with Pause and Resume controls"
        });
    }

    public override void OnDestroy()
    {
        if (ReferenceEquals(current, this))
            current = null;
        base.OnDestroy();
    }

    public override IBinder? OnBind(Intent? intent) => null;
}
