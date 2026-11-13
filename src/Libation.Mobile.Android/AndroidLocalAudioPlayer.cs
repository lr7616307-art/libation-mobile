using Android.Content;
using Libation.Mobile.Core;
using Libation.Mobile.Audible;
using Libation.Mobile.UI.Views;

namespace Libation.Mobile.Android;

/// <summary>UI adapter for the playback service, including crash-safe session recovery.</summary>
public sealed class AndroidLocalAudioPlayer : ILocalAudioPlayer, ILocalAudioPlayerStateNotifier, ISleepTimerAudioPlayer, IChapterPositionAudioPlayer
{
    private static Context AppContext => global::Android.App.Application.Context;

    public event EventHandler? PlaybackStateChanged;

    public AndroidLocalAudioPlayer()
    {
        AndroidLocalAudioPlaybackService.PlaybackStateChanged += OnServicePlaybackStateChanged;

        // Rehydrate the last session when the app is opened after a process death
        // or device restart. The saved play/pause state is respected.
        var widevineSnapshot = WidevinePlaybackRecoveryStore.Load(AppContext);
        if (widevineSnapshot is not null)
        {
            _ = RestoreWidevinePlaybackAsync(widevineSnapshot);
            return;
        }

        var snapshot = PlaybackRecoveryStore.Load(AppContext);
        if (snapshot is not null)
        {
            var intent = CreateIntent(AndroidLocalAudioPlaybackService.ActionRestore);
            if (global::Android.OS.Build.VERSION.SdkInt >= global::Android.OS.BuildVersionCodes.O)
                AppContext.StartForegroundService(intent);
            else
                AppContext.StartService(intent);
        }
    }

    private static async Task RestoreWidevinePlaybackAsync(WidevinePlaybackRecoverySnapshot snapshot)
    {
        try
        {
            var factory = MainView.PlatformWidevinePlaybackCoordinatorFactory;
            if (factory is null)
                return;

            var coordinator = await factory(CancellationToken.None).ConfigureAwait(false);
            if (coordinator is null)
                return;

            await coordinator.PlayAsync(
                snapshot.Asin,
                snapshot.Title,
                snapshot.PositionMilliseconds,
                snapshot.WasPlaying,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Keep the non-secret checkpoint so the next app launch can retry.
            // The UI can still open and the DRM/cache failure is not logged.
        }
    }

    private PlaybackRecoverySnapshot? Recovery => PlaybackRecoveryStore.Load(AppContext);

    // Once ExoPlayer has been created, all values come from that live engine.
    // The saved snapshot is only a fallback while its service is being restored.
    public bool IsPlaying => AndroidLocalAudioPlaybackService.HasActivePlayer
        ? AndroidLocalAudioPlaybackService.IsPlaying
        : Recovery?.WasPlaying == true;
    public string? CurrentFilePath => AndroidLocalAudioPlaybackService.CurrentFilePath ?? Recovery?.FilePath;
    public long PositionMilliseconds => AndroidLocalAudioPlaybackService.HasActivePlayer
        ? AndroidLocalAudioPlaybackService.PositionMilliseconds
        : Recovery?.PositionMilliseconds ?? 0;
    public long DurationMilliseconds => AndroidLocalAudioPlaybackService.HasActivePlayer
        ? AndroidLocalAudioPlaybackService.DurationMilliseconds
        : Recovery?.DurationMilliseconds ?? 0;
    public float PlaybackSpeed => AndroidLocalAudioPlaybackService.HasActivePlayer
        ? AndroidLocalAudioPlaybackService.PlaybackSpeed
        : Recovery?.PlaybackSpeed ?? 1f;

    private void OnServicePlaybackStateChanged()
        => PlaybackStateChanged?.Invoke(this, EventArgs.Empty);

    public void Play(string filePath, long startPositionMilliseconds = 0, string? title = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            throw new FileNotFoundException("Imported audiobook audio was not found.", filePath);
        var intent = CreateIntent(AndroidLocalAudioPlaybackService.ActionPlay);
        intent.PutExtra(AndroidLocalAudioPlaybackService.ExtraFilePath, filePath);
        if (!string.IsNullOrWhiteSpace(title)) intent.PutExtra(AndroidLocalAudioPlaybackService.ExtraTitle, title);
        intent.PutExtra(AndroidLocalAudioPlaybackService.ExtraPosition, Math.Max(0, startPositionMilliseconds));
        StartService(intent, foreground: true);
    }

    /// <summary>
    /// Starts protected Audible DASH playback through Media3 using a persisted
    /// Android Widevine offline key-set identifier. The identifier is passed
    /// only in an explicit in-app service intent and is never written to recovery
    /// preferences or the audiobook catalogue.
    /// </summary>
    public static void StartWidevineDash(
        Uri manifestUri,
        byte[] offlineKeySetId,
        string title,
        string accountId,
        string asin,
        int[] streamKeyValues,
        string contentVersionKey,
        long startPositionMilliseconds = 0,
        bool autoPlay = true)
    {
        ArgumentNullException.ThrowIfNull(manifestUri);
        ArgumentNullException.ThrowIfNull(offlineKeySetId);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(asin);
        ArgumentNullException.ThrowIfNull(streamKeyValues);

        if (!manifestUri.IsAbsoluteUri ||
            !string.Equals(manifestUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            manifestUri.UserInfo.Length != 0)
            throw new ArgumentException("Protected Audible playback requires an HTTPS DASH manifest.", nameof(manifestUri));
        if (offlineKeySetId.Length is 0 or > 1024)
            throw new ArgumentOutOfRangeException(nameof(offlineKeySetId));
        if (asin.Length != 10 || asin.Any(character => !char.IsAsciiLetterOrDigit(character)))
            throw new ArgumentException("A valid Audible ASIN is required.", nameof(asin));
        if (streamKeyValues.Length is 0 or > 384 || streamKeyValues.Length % 3 != 0 ||
            streamKeyValues.Any(value => value < 0))
            throw new ArgumentException("The offline DASH audio stream selection is invalid.", nameof(streamKeyValues));
        if (!AudibleWidevineCacheIdentity.IsValidContentVersionKey(contentVersionKey))
            throw new ArgumentException("The Audible cache content-version key is invalid.", nameof(contentVersionKey));

        var intent = CreateIntent(AndroidLocalAudioPlaybackService.ActionPlayWidevineDash);
        intent.PutExtra(AndroidLocalAudioPlaybackService.ExtraManifestUri, manifestUri.AbsoluteUri);
        intent.PutExtra(AndroidLocalAudioPlaybackService.ExtraOfflineKeySetId, offlineKeySetId);
        intent.PutExtra(AndroidLocalAudioPlaybackService.ExtraTitle, title);
        intent.PutExtra(AndroidLocalAudioPlaybackService.ExtraWidevineAccountId, accountId);
        intent.PutExtra(AndroidLocalAudioPlaybackService.ExtraWidevineAsin, asin);
        intent.PutExtra(AndroidLocalAudioPlaybackService.ExtraWidevineStreamKeys, streamKeyValues);
        intent.PutExtra(AndroidLocalAudioPlaybackService.ExtraWidevineContentVersionKey, contentVersionKey);
        intent.PutExtra(AndroidLocalAudioPlaybackService.ExtraPosition, Math.Max(0, startPositionMilliseconds));
        intent.PutExtra(AndroidLocalAudioPlaybackService.ExtraAutoPlay, autoPlay);
        StartService(intent, foreground: true);
    }

    public void Pause() { ObjectDisposedException.ThrowIf(_disposed, this); AppContext.StartService(CreateIntent(AndroidLocalAudioPlaybackService.ActionPause)); }
    public void Resume() { ObjectDisposedException.ThrowIf(_disposed, this); AppContext.StartService(CreateIntent(AndroidLocalAudioPlaybackService.ActionResume)); }

    public void Seek(long positionMilliseconds)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var intent = CreateIntent(AndroidLocalAudioPlaybackService.ActionSeek);
        intent.PutExtra(AndroidLocalAudioPlaybackService.ExtraPosition, Math.Max(0, positionMilliseconds));
        AppContext.StartService(intent);
    }

    public SleepTimerSnapshot? GetSleepTimer()
    {
        var preferences = AppContext.GetSharedPreferences("vorath_sleep_timer", FileCreationMode.Private);
        var deadlineTicks = preferences?.GetLong("deadline_utc_ticks", 0) ?? 0;
        var chapterPosition = preferences?.GetLong("chapter_position_ms", -1) ?? -1;
        if (deadlineTicks <= 0 && chapterPosition < 0) return null;
        DateTimeOffset? deadline = deadlineTicks > 0
            ? new DateTimeOffset(deadlineTicks, TimeSpan.Zero) : null;
        return new SleepTimerSnapshot(deadline, chapterPosition >= 0 ? chapterPosition : null);
    }

    public void SetSleepTimer(DateTimeOffset? deadline, long? chapterPositionMilliseconds)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var intent = CreateIntent(AndroidLocalAudioPlaybackService.ActionSetSleepTimer);
        intent.PutExtra(AndroidLocalAudioPlaybackService.ExtraSleepDeadlineUtcTicks, deadline?.UtcDateTime.Ticks ?? 0);
        intent.PutExtra(AndroidLocalAudioPlaybackService.ExtraSleepChapterPosition, chapterPositionMilliseconds ?? -1);
        AppContext.StartService(intent);
    }

    public void CancelSleepTimer()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        AppContext.StartService(CreateIntent(AndroidLocalAudioPlaybackService.ActionCancelSleepTimer));
    }
    public void SetChapterPositions(IEnumerable<long> chapterStartPositionsMilliseconds)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(chapterStartPositionsMilliseconds);
        var positions = chapterStartPositionsMilliseconds.Where(x => x >= 0).Distinct().OrderBy(x => x).ToArray();
        var intent = CreateIntent(AndroidLocalAudioPlaybackService.ActionSetChapterPositions);
        intent.PutExtra(AndroidLocalAudioPlaybackService.ExtraChapterPositions, positions);
        AppContext.StartService(intent);
    }

    public void SetArtwork(string? coverImagePath)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var intent = CreateIntent(AndroidLocalAudioPlaybackService.ActionSetArtwork);
        intent.PutExtra(AndroidLocalAudioPlaybackService.ExtraArtworkPath, coverImagePath);
        AppContext.StartService(intent);
    }

    public void SetPlaybackSpeed(float speed)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (float.IsNaN(speed) || float.IsInfinity(speed)) throw new ArgumentOutOfRangeException(nameof(speed));
        var intent = CreateIntent(AndroidLocalAudioPlaybackService.ActionSetSpeed);
        intent.PutExtra(AndroidLocalAudioPlaybackService.ExtraSpeed, Math.Clamp(speed, 0.5f, 3f));
        AppContext.StartService(intent);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        AndroidLocalAudioPlaybackService.PlaybackStateChanged -= OnServicePlaybackStateChanged;
    }
    private bool _disposed;

    private static Intent CreateIntent(string action) => new Intent(AppContext, typeof(AndroidLocalAudioPlaybackService)).SetAction(action);
    private static void StartService(Intent intent, bool foreground = false)
    {
        if (foreground && global::Android.OS.Build.VERSION.SdkInt >= global::Android.OS.BuildVersionCodes.O)
            AppContext.StartForegroundService(intent);
        else
            AppContext.StartService(intent);
    }
}
