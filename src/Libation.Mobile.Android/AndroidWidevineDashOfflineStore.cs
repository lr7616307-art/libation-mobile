using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Libation.Mobile.Audible;
using Android.Content;
using AndroidX.Media3.Common;
using AndroidX.Media3.DataSource;
using AndroidX.Media3.DataSource.Cache;
using AndroidX.Media3.Database;
using AndroidX.Media3.ExoPlayer.Dash.Offline;

namespace Libation.Mobile.Android;

/// <summary>
/// Downloads provider-encrypted DASH audio segments into an app-private Media3
/// cache. Cache index keys are scoped to a hashed account/title identity and
/// hashed resource URI; signed provider URLs are never stored as cache keys.
/// </summary>
public sealed class AndroidWidevineDashOfflineStore
{
    private const string CacheDirectoryName = "vorath-widevine-dash-cache-v1";
    private const string CacheIndexFileName = "vorath-widevine-dash-cache-index-v1.json";
    private const long MaximumCachedBytes = 8L * 1024 * 1024 * 1024;
    private const int MaximumIndexedKeys = 100_000;
    private static readonly object CacheGate = new();
    private static readonly object CacheIndexGate = new();
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

        var streamKeyList = new global::Android.Runtime.JavaList<StreamKey>();
        foreach (var key in streamKeys)
            streamKeyList.Add(new StreamKey(key.PeriodIndex, key.GroupIndex, key.TrackIndex));

        var mediaItem = new MediaItem.Builder()
            .SetUri(androidUri)
            .SetMimeType("application/dash+xml")
            .SetStreamKeys(streamKeyList)
            .Build();

        // The generated .NET binding exposes the public Java constructor, while
        // DashDownloader.Factory.create(MediaItem) is not projected as a method.
        var downloader = new DashDownloader(mediaItem, scopedCacheFactory);
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

            if (cache.CacheSpace > MaximumCachedBytes)
            {
                RemoveTitle(accountId, asin);
                throw new IOException(
                    "The protected Audible download would exceed VØRATH's 8 GiB offline cache limit. The incomplete title cache was removed.");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
    }

    public bool HasCachedResources(string accountId, string asin)
    {
        ValidateIdentity(accountId, asin);
        var scope = GetTitleScope(accountId, asin);
        lock (CacheIndexGate)
        {
            var index = ReadCacheIndex();
            return index.TryGetValue(scope, out var keys) && keys.Count > 0;
        }
    }

    public CacheDataSource.Factory CreatePlaybackDataSourceFactory(
        string accountId,
        string asin,
        Uri manifestUri)
    {
        ValidateIdentity(accountId, asin);
        ValidateManifestUri(manifestUri);
        // No upstream factory is configured. A cache miss fails closed rather
        // than silently switching to the network when the user expects offline playback.
        return CreateCacheDataSourceFactory(
            GetCache(), GetTitleScope(accountId, asin), manifestUri, allowNetwork: false);
    }

    public void RemoveTitle(string accountId, string asin)
    {
        ValidateIdentity(accountId, asin);
        var scope = GetTitleScope(accountId, asin);
        var cache = GetCache();

        lock (CacheIndexGate)
        {
            var index = ReadCacheIndex();
            if (index.TryGetValue(scope, out var keys))
            {
                foreach (var key in keys)
                    cache.RemoveResource(key);
                index.Remove(scope);
                WriteCacheIndex(index);
            }
        }
    }

    private CacheDataSource.Factory CreateCacheDataSourceFactory(
        SimpleCache cache,
        string titleScope,
        Uri manifestUri,
        bool allowNetwork = true)
    {
        var factory = new CacheDataSource.Factory()
            .SetCache(cache)
            .SetCacheKeyFactory(new ScopedCacheKeyFactory(
                titleScope,
                manifestUri.AbsoluteUri,
                RecordCacheKey));

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

    private void RecordCacheKey(string titleScope, string cacheKey)
    {
        lock (CacheIndexGate)
        {
            var index = ReadCacheIndex();
            if (!index.TryGetValue(titleScope, out var keys))
            {
                keys = new HashSet<string>(StringComparer.Ordinal);
                index.Add(titleScope, keys);
            }

            if (keys.Add(cacheKey))
            {
                if (index.Sum(pair => pair.Value.Count) > MaximumIndexedKeys)
                    throw new IOException("The protected-audio cache index has reached its safety limit.");
                WriteCacheIndex(index);
            }
        }
    }

    private Dictionary<string, HashSet<string>> ReadCacheIndex()
    {
        var path = CacheIndexPath;
        if (!File.Exists(path))
            return new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        var json = File.ReadAllText(path, Encoding.UTF8);
        if (Encoding.UTF8.GetByteCount(json) > 8 * 1024 * 1024)
            throw new InvalidDataException("The protected-audio cache index exceeds its supported size.");

        try
        {
            var stored = JsonSerializer.Deserialize<Dictionary<string, string[]>>(json)
                ?? throw new InvalidDataException("The protected-audio cache index is empty.");
            if (stored.Count > 4096 ||
                stored.Any(pair => pair.Value is null || pair.Value.Length > MaximumIndexedKeys))
                throw new InvalidDataException("The protected-audio cache index exceeds its supported limits.");

            return stored.ToDictionary(
                pair => pair.Key,
                pair => new HashSet<string>(pair.Value, StringComparer.Ordinal),
                StringComparer.Ordinal);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The protected-audio cache index is malformed.", exception);
        }
    }

    private void WriteCacheIndex(Dictionary<string, HashSet<string>> index)
    {
        var path = CacheIndexPath;
        var temporaryPath = path + ".tmp";
        var payload = index.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
            StringComparer.Ordinal);
        var json = JsonSerializer.Serialize(payload);
        if (Encoding.UTF8.GetByteCount(json) > 8 * 1024 * 1024)
            throw new InvalidDataException("The protected-audio cache index exceeds its supported size.");

        File.WriteAllText(temporaryPath, json, new UTF8Encoding(false));
        File.Move(temporaryPath, path, overwrite: true);
    }

    private string CacheIndexPath
        => Path.Combine(applicationContext.FilesDir!.AbsolutePath!, CacheIndexFileName);

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
    /// Cache keys contain only hashes. A private app-only index records the
    /// hashed keys by title so cache checks and removal survive process death.
    /// </summary>
    private sealed class ScopedCacheKeyFactory : Java.Lang.Object, ICacheKeyFactory
    {
        private readonly string titleScope;
        private readonly string manifestUri;
        private readonly Action<string, string> recordCacheKey;

        public ScopedCacheKeyFactory(
            string titleScope,
            string manifestUri,
            Action<string, string> recordCacheKey)
        {
            this.titleScope = titleScope;
            this.manifestUri = manifestUri;
            this.recordCacheKey = recordCacheKey;
        }

        public string? BuildCacheKey(DataSpec? dataSpec)
        {
            var uri = dataSpec?.Uri?.ToString()
                ?? throw new InvalidDataException("Media3 supplied a DASH resource without a URI.");
            var kind = string.Equals(uri, manifestUri, StringComparison.Ordinal)
                ? "manifest"
                : "segment";
            var digest = SHA256.HashData(Encoding.UTF8.GetBytes(uri));
            var cacheKey = titleScope + ":" + kind + ":" + Convert.ToHexString(digest).ToLowerInvariant();
            recordCacheKey(titleScope, cacheKey);
            return cacheKey;
        }
    }
}
