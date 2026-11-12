using System.Security.Cryptography;
using System.Text;
using Android.Content;
using AndroidX.Media3.Common;
using AndroidX.Media3.Datasource;
using AndroidX.Media3.Datasource.Cache;
using AndroidX.Media3.Database;
using AndroidX.Media3.Exoplayer.Dash.Offline;

namespace Libation.Mobile.Android;

/// <summary>
/// Downloads provider-encrypted DASH audio segments into an app-private Media3
/// cache. Cache index keys are scoped to a hashed account/title identity and
/// hashed resource URI; signed provider URLs are not written as cache keys.
/// </summary>
public sealed class AndroidWidevineDashOfflineStore
{
    private const string CacheDirectoryName = "vorath-widevine-dash-cache-v1";
    private const long MaximumCachedBytes = 8L * 1024 * 1024 * 1024;
    private static readonly object CacheGate = new();
    private static SimpleCache? sharedCache;

    private readonly Context applicationContext;

    public AndroidWidevineDashOfflineStore(Context context)
    {
        ArgumentNullException.ThrowIfNull(context);
        applicationContext = context.ApplicationContext
            ?? throw new InvalidOperationException("Android application context is unavailable.");
    }

    public async Task DownloadAsync(
        string accountId,
        string asin,
        Uri manifestUri,
        IReadOnlyList<AudibleWidevineStreamKey> streamKeys,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(accountId, asin);
        ValidateManifest(manifestUri, streamKeys);
        cancellationToken.ThrowIfCancellationRequested();

        var cache = GetCache();
        if (cache.CacheSpace >= MaximumCachedBytes)
            throw new IOException(
                "VØRATH's protected-audio cache has reached its 8 GiB limit. Remove an offline Audible title before downloading another.");

        var scope = GetTitleScope(accountId, asin);
        var scopedCacheFactory = CreateCacheDataSourceFactory(cache, scope, manifestUri);
        var androidUri = global::Android.Net.Uri.Parse(manifestUri.AbsoluteUri);

        var streamKeyList = new Java.Util.ArrayList<StreamKey>();
        foreach (var key in streamKeys)
            streamKeyList.Add(new StreamKey(key.PeriodIndex, key.GroupIndex, key.TrackIndex));

        var mediaItem = new MediaItem.Builder()
            .SetUri(androidUri)
            .SetMimeType("application/dash+xml")
            .SetStreamKeys(streamKeyList)
            .Build();

        var downloader = new DashDownloader.Factory(scopedCacheFactory).Create(mediaItem);
        using var registration = cancellationToken.Register(() =>
        {
            try { downloader.Cancel(); }
            catch (Exception) { /* Cancellation must not emit DRM or URL diagnostics. */ }
        });

        try
        {
            // DASH downloader is synchronous; run it away from the UI thread.
            // Media3 stores the provider-delivered CENC-encrypted segments as-is.
            await Task.Run(() => downloader.Download(null), cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
    }

    public bool HasCachedResources(string accountId, string asin)
    {
        ValidateIdentity(accountId, asin);
        var prefix = GetTitleScope(accountId, asin) + ":";
        return GetCache().GetKeys().Any(key => key.StartsWith(prefix, StringComparison.Ordinal));
    }

    public CacheDataSource.Factory CreatePlaybackDataSourceFactory(
        string accountId,
        string asin,
        Uri manifestUri)
    {
        ValidateIdentity(accountId, asin);
        ValidateManifestUri(manifestUri);
        // No upstream factory is configured. A cache miss fails closed rather
        // than silently switching to online streaming when the user expects offline playback.
        return CreateCacheDataSourceFactory(
            GetCache(), GetTitleScope(accountId, asin), manifestUri, allowNetwork: false);
    }

    public void RemoveTitle(string accountId, string asin)
    {
        ValidateIdentity(accountId, asin);
        var prefix = GetTitleScope(accountId, asin) + ":";
        var cache = GetCache();

        // Materialize first because RemoveResource mutates the key set.
        foreach (var key in cache.GetKeys().Where(key =>
                     key.StartsWith(prefix, StringComparison.Ordinal)).ToArray())
            cache.RemoveResource(key);
    }

    private CacheDataSource.Factory CreateCacheDataSourceFactory(
        SimpleCache cache,
        string titleScope,
        Uri manifestUri,
        bool allowNetwork = true)
    {
        var factory = new CacheDataSource.Factory()
            .SetCache(cache)
            .SetCacheKeyFactory(new ScopedCacheKeyFactory(titleScope, manifestUri.AbsoluteUri));

        if (allowNetwork)
        {
            factory.SetUpstreamDataSourceFactory(
                new DefaultHttpDataSource.Factory()
                    .SetAllowCrossProtocolRedirects(false));
        }

        return factory;
    }

    private SimpleCache GetCache()
    {
        lock (CacheGate)
        {
            if (sharedCache is not null)
                return sharedCache;

            var directory = new Java.IO.File(
                Path.Combine(applicationContext.FilesDir!.AbsolutePath!, CacheDirectoryName));
            if (!directory.Exists() && !directory.Mkdirs())
                throw new IOException("Unable to create VØRATH's private protected-audio cache.");

            sharedCache = new SimpleCache(
                directory,
                new NoOpCacheEvictor(),
                new StandaloneDatabaseProvider(applicationContext));
            return sharedCache;
        }
    }

    private static string GetTitleScope(string accountId, string asin)
    {
        var identity = Encoding.UTF8.GetBytes(
            accountId.Trim() + "\n" + asin.ToUpperInvariant());
        var digest = SHA256.HashData(identity);
        return "vorath-v1-" + Convert.ToHexString(digest).ToLowerInvariant();
    }

    private static void ValidateIdentity(string accountId, string asin)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        if (accountId.Length > 512)
            throw new ArgumentOutOfRangeException(nameof(accountId));
        if (string.IsNullOrWhiteSpace(asin) || asin.Length != 10 ||
            asin.Any(character => !char.IsAsciiLetterOrDigit(character)))
            throw new ArgumentException("The Audible audiobook identifier is not a valid 10-character ASIN.", nameof(asin));
    }

    private static void ValidateManifest(
        Uri manifestUri,
        IReadOnlyList<AudibleWidevineStreamKey> streamKeys)
    {
        ValidateManifestUri(manifestUri);
        ArgumentNullException.ThrowIfNull(streamKeys);
        if (streamKeys.Count is 0 or > 128 ||
            streamKeys.Any(key => key.PeriodIndex < 0 || key.GroupIndex < 0 || key.TrackIndex < 0))
            throw new ArgumentException("The Audible audio stream selection is invalid.", nameof(streamKeys));
    }

    private static void ValidateManifestUri(Uri manifestUri)
    {
        ArgumentNullException.ThrowIfNull(manifestUri);
        if (!manifestUri.IsAbsoluteUri ||
            !string.Equals(manifestUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            manifestUri.UserInfo.Length != 0 ||
            manifestUri.AbsoluteUri.Length > 8192)
            throw new ArgumentException("The Audible DASH manifest must be a bounded HTTPS URI.", nameof(manifestUri));
    }

    /// <summary>
    /// Cache keys contain only hashes. The manifest URI is tagged separately so
    /// title-scoped cache cleanup can be performed without retaining the signed URL.
    /// </summary>
    private sealed class ScopedCacheKeyFactory : Java.Lang.Object, ICacheKeyFactory
    {
        private readonly string titleScope;
        private readonly string manifestUri;

        public ScopedCacheKeyFactory(string titleScope, string manifestUri)
        {
            this.titleScope = titleScope;
            this.manifestUri = manifestUri;
        }

        public string BuildCacheKey(DataSpec dataSpec)
        {
            var uri = dataSpec.Uri?.ToString()
                ?? throw new InvalidDataException("Media3 supplied a DASH resource without a URI.");
            var kind = string.Equals(uri, manifestUri, StringComparison.Ordinal)
                ? "manifest"
                : "segment";
            var digest = SHA256.HashData(Encoding.UTF8.GetBytes(uri));
            return titleScope + ":" + kind + ":" + Convert.ToHexString(digest).ToLowerInvariant();
        }
    }
}
