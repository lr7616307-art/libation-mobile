using Android.App;
using Android.Content;
using Android.OS;
using AudibleApi;
using Libation.Mobile.Audible;
using Libation.Mobile.Core;
using Libation.Mobile.UI.Views;

namespace Libation.Mobile.Android;

/// <summary>
/// Runs the LibriSync-compatible Audible ADRM download in Android's foreground
/// service. The provider-issued voucher is used to convert the downloaded AAX/AAXC
/// file into a playable M4B; pause and process interruption retain the HTTP .part.
/// </summary>
[Service(Name = "org.libationmobile.app.AndroidAudibleOfflineDownloadService", Exported = false)]
public sealed class AndroidAudibleOfflineDownloadService : Service
{
    private const string ChannelId = "vorath_audible_downloads";
    private const int NotificationId = 2410;
    private const string ActionStart = "org.libationmobile.app.action.AUDIBLE_DOWNLOAD_START";
    private const string ActionPause = "org.libationmobile.app.action.AUDIBLE_DOWNLOAD_PAUSE";
    private const string ExtraAsin = "asin";
    private const string ExtraTitle = "title";

    private static readonly HttpClient DownloadHttpClient = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = true,
        MaxAutomaticRedirections = 5,
        AutomaticDecompression = System.Net.DecompressionMethods.None,
        PooledConnectionLifetime = TimeSpan.FromMinutes(10)
    })
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    private CancellationTokenSource? _runCancellation;
    private Task? _runTask;
    private string? _activeAsin;
    private string? _activeTitle;

    public static Func<CancellationToken, Task<AudibleAudioDownloadSource?>>? SourceFactory { get; set; }
    public static string DownloadDirectory { get; set; } = Path.Combine(
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "audible-downloads");

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
                    "Download paused. Your received bytes are saved; tap Resume to continue.",
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
                "Preparing Audible ADRM download…",
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
        var partialPath = string.Empty;
        var resumeMetadataPath = string.Empty;

        try
        {
            var sourceFactory = SourceFactory
                ?? throw new InvalidOperationException("The Audible ADRM download source is not configured.");
            var source = await sourceFactory(cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Sign in to Audible again before downloading.");

            Directory.CreateDirectory(DownloadDirectory);
            var stagingDirectory = Path.Combine(DownloadDirectory, "staging");
            Directory.CreateDirectory(stagingDirectory);
            partialPath = Path.Combine(stagingDirectory, asin.ToUpperInvariant() + ".encrypted.part");
            resumeMetadataPath = partialPath + ".resume.json";

            UpdateNotification(
                title,
                "Requesting Audible ADRM/AAC-LC licence…",
                null,
                indeterminate: true,
                ongoing: true,
                actionLabel: "Pause");

            // LibriSync's actual Audible request: ADRM + AAC-LC, not the unrelated
            // Widevine DASH route. The response includes the signed file URL and
            // the provider-issued AAXC voucher (or the legacy AAX fallback).
            var plan = await source.ResolveAdrmDownloadPlanAsync(asin, cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            var progress = new Progress<AudioDownloadProgress>(value =>
            {
                var detail = value.TotalBytes is > 0
                    ? FormatBytes(value.BytesReceived) + " of " + FormatBytes(value.TotalBytes.Value)
                    : FormatBytes(value.BytesReceived) + " received";

                UpdateNotification(
                    title,
                    detail,
                    value.Percentage is { } percent ? (int)Math.Clamp(percent, 0d, 100d) : null,
                    indeterminate: value.Percentage is null,
                    ongoing: true,
                    actionLabel: "Pause");
            });

            var downloader = new ResumableAudioFileDownloader(DownloadHttpClient);
            var downloaded = await downloader.DownloadAsync(
                plan.DownloadUri,
                partialPath,
                plan.ResumeFingerprint,
                progress,
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            var outputPath = plan.MediaKind == AudibleAdrmMediaKind.Mpeg
                ? Path.Combine(DownloadDirectory, asin.ToUpperInvariant() + ".mp3")
                : Path.Combine(DownloadDirectory, asin.ToUpperInvariant() + ".m4b");

            if (plan.MediaKind == AudibleAdrmMediaKind.Mpeg)
            {
                File.Move(downloaded.PartialFilePath, outputPath, overwrite: true);
            }
            else
            {
                UpdateNotification(
                    title,
                    "Download complete. Preparing playable audiobook…",
                    100,
                    indeterminate: false,
                    ongoing: true,
                    actionLabel: null);

                var converter = new AndroidFfmpegKitRunner();
                await converter.ConvertToM4bAsync(
                    downloaded.PartialFilePath,
                    outputPath,
                    plan,
                    cancellationToken).ConfigureAwait(false);

                // Delete the encrypted staging file only after FFmpeg has written
                // and verified a non-empty M4B. Conversion errors keep the source
                // and resume metadata so the user does not have to download again.
                TryDelete(downloaded.PartialFilePath);
            }

            TryDelete(resumeMetadataPath);
            cancellationToken.ThrowIfCancellationRequested();

            var outputInfo = new FileInfo(outputPath);
            if (!outputInfo.Exists || outputInfo.Length <= 0)
                throw new InvalidDataException("VØRATH did not produce a complete playable audiobook file.");

            MainView.PlatformAudibleDownloadCompletedAction?.Invoke(asin, outputPath);
            FinishWithNotification(
                startId,
                "Download complete. The audiobook is ready to play offline in VØRATH.",
                100,
                actionLabel: null);
        }
        catch (System.OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            FinishWithNotification(
                startId,
                "Download paused. Received bytes are preserved; tap Resume to continue.",
                null,
                actionLabel: "Resume");
        }
        catch (ContentLicenseDeniedException)
        {
            FinishWithNotification(
                startId,
                "Audible did not grant the ADRM licence or legacy AAX download URL for this account/title.",
                null,
                actionLabel: "Retry");
        }
        catch (NotSupportedException ex)
        {
            var message = SafeStatusMessage(ex.Message);
            FinishWithNotification(startId, message, null, actionLabel: "Retry");
        }
        catch (Exception ex)
        {
            // Do not display signed provider URLs, vouchers, keys or raw HTTP bodies.
            var detail = SafeStatusMessage(ex.Message);
            FinishWithNotification(
                startId,
                "Download could not finish: " + detail + " Received bytes are kept where safe; tap Retry.",
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

    private static string SafeStatusMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return "Unknown Audible download error.";
        var safe = message.Trim();
        if (safe.Contains("https://", StringComparison.OrdinalIgnoreCase) ||
            safe.Contains("authorization", StringComparison.OrdinalIgnoreCase) ||
            safe.Contains("voucher", StringComparison.OrdinalIgnoreCase) ||
            safe.Contains("bearer", StringComparison.OrdinalIgnoreCase))
            return "Audible rejected or could not complete the authorized media request.";
        return safe.Length > 180 ? safe[..180] : safe;
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
            .SetContentTitle(title.Length > 64 ? "VØRATH · " + title[..64] : "VØRATH · " + title)
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
        if (bytes < 1024) return bytes + " B";
        if (bytes < 1024 * 1024) return (bytes / 1024d).ToString("0.0") + " KB";
        if (bytes < 1024L * 1024 * 1024) return (bytes / (1024d * 1024)).ToString("0.0") + " MB";
        return (bytes / (1024d * 1024 * 1024)).ToString("0.00") + " GB";
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
