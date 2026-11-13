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
using AndroidX.Media3.ExoPlayer.Offline;

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
    private const string CacheRequirementsFileName = "vorath-widevine-dash-cache-ranges-v1.json";
    private const long MaximumCachedBytes = 8L * 1024 * 1024 * 1024;
    private const int MaximumIndexedKeys = 100_000;
    private static readonly object CacheGate = new();
    private static readonly object CacheIndexGate = new();
    private static SimpleCache? sharedCache;

    private readonly Context applicationContext;

    private sealed class DashDownloadProgressListener(
        IProgress<WidevineOfflineDownloadProgress> progress) : Java.Lang.Object, IDownloaderProgressListener
    {
        public void OnProgress(long contentLength, long bytesDownloaded, float percentDownloaded)
        {
            var total = contentLength >= 0 ? contentLength : (long?)null;
            var percentage = float.IsFinite(percentDownloaded) && percentDownloaded >= 0f
                ? Math.Clamp((double)percentDownloaded, 0d, 100d)
                : (double?)null;
            progress.Report(new WidevineOfflineDownloadProgress(
                Math.Max(0, bytesDownloaded), total, percentage));
        }
    }

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
        CancellationToken cancellationToken = default,
        IProgress<WidevineOfflineDownloadProgress>? progress = null)
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
        var progressListener = progress is null ? null : new DashDownloadProgressListener(progress);
        using var registration = cancellationToken.Register(() =>
        {
            try { downloader.Cancel(); }
            catch (Exception) { /* Cancellation must not emit DRM or URL diagnostics. */ }
        });

        try
        {
            // DASH downloader is synchronous; run it away from the UI thread.
            // Media3 stores the provider-delivered CENC-encrypted segments as-is.
            await Task.Run(() => downloader.Download(progressListener), cancellationToken)
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

    /// <summary>
    /// Audits every indexed DASH resource off the UI thread. Each recorded
    /// DataSpec byte range must be fully present, not merely represented by a
    /// cache key or one partial span. Stale index entries are reconciled.
    /// </summary>
    public Task<bool> HasCachedResourcesAsync(
        string accountId,
        string asin,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(accountId, asin);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(() => AuditCachedResources(accountId, asin, cancellationToken), cancellationToken);
    }

    private bool AuditCachedResources(
        string accountId,
        string asin,
        CancellationToken cancellationToken)
    {
        var scope = GetTitleScope(accountId, asin);
        var cache = GetCache();
        var liveKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in GetCacheKeys(cache))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.IsNullOrWhiteSpace(key))
                liveKeys.Add(key);
        }

        lock (CacheIndexGate)
        {
            var index = ReadCacheIndex();
            var requirements = ReadCacheRequirements();
            var indexedKeys = index.TryGetValue(scope, out var storedKeys)
                ? storedKeys
                : new HashSet<string>(StringComparer.Ordinal);
            var reconciliation = AudibleWidevineCacheRangeIntegrity.ReconcileCacheIndex(
                scope, indexedKeys, liveKeys);
            var keys = reconciliation.RetainedKeys.ToHashSet(StringComparer.Ordinal);
            var changed = reconciliation.StaleKeys.Count > 0
                || reconciliation.RecoveredOrphanKeys.Count > 0;

            // Stale requirements are removed with the stale index entry.
            // Recovered orphans have no trusted range metadata yet; they remain
            // removable, but the audit fails closed until a retry re-observes
            // the corresponding DataSpec and its complete byte range.
            if (requirements.TryGetValue(scope, out var titleRequirements))
            {
                foreach (var staleKey in reconciliation.StaleKeys)
                    titleRequirements.Remove(staleKey);
            }

            if (keys.Count == 0)
            {
                index.Remove(scope);
                requirements.Remove(scope);
                if (changed)
                {
                    WriteCacheIndex(index);
                    WriteCacheRequirements(requirements);
                }
                return false;
            }

            if (changed)
            {
                // Persist both previously indexed and newly reconciled orphan
                // resources so removal still finds every cache entry.
                index[scope] = keys;
                WriteCacheIndex(index);
                WriteCacheRequirements(requirements);
            }

            if (!keys.Any(key => key.StartsWith(scope + ":manifest:", StringComparison.Ordinal)) ||
                !keys.Any(key => key.StartsWith(scope + ":segment:", StringComparison.Ordinal)))
                return false;

            if (!requirements.TryGetValue(scope, out var expectedResources))
                return false;

            foreach (var cacheKey in keys)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!expectedResources.TryGetValue(cacheKey, out var ranges) || ranges.Count == 0)
                    return false;

                foreach (var range in ranges)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (range.Position < 0)
                        return false;

                    if (range.Length > 0)
                    {
                        var cachedBytes = cache.GetCachedBytes(
                            cacheKey, range.Position, range.Length);
                        if (!AudibleWidevineCacheRangeIntegrity.IsComplete(
                                range.Position, range.Length, cachedBytes, -1))
                            return false;
                        continue;
                    }

                    // An unknown-length DataSpec is considered complete only
                    // when Media3 recorded the resource's final content length
                    // and every byte from offset zero through EOF is cached.
                    var metadata = cache.GetContentMetadata(cacheKey);
                    var contentLength = metadata.Get("exo_len", -1L);
                    var totalCachedBytes = contentLength > 0
                        ? cache.GetCachedBytes(cacheKey, 0, contentLength)
                        : 0;
                    if (!AudibleWidevineCacheRangeIntegrity.IsComplete(
                            range.Position, range.Length, totalCachedBytes, contentLength))
                        return false;
                }
            }

            return true;
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
            var requirements = ReadCacheRequirements();
            var keysToRemove = index.TryGetValue(scope, out var indexedKeys)
                ? new HashSet<string>(indexedKeys, StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);

            foreach (var cacheKey in GetCacheKeys(cache))
            {
                if (cacheKey.StartsWith(scope + ":", StringComparison.Ordinal))
                    keysToRemove.Add(cacheKey);
            }

            foreach (var key in keysToRemove)
                cache.RemoveResource(key);

            index.Remove(scope);
            requirements.Remove(scope);
            WriteCacheIndex(index);
            WriteCacheRequirements(requirements);
        }
    }

    private static HashSet<string> GetCacheKeys(SimpleCache cache)
    {
        // Media3 exposes SimpleCache.getKeys() publicly, but Xamarin's current
        // binding omits that member from its C# projection. Invoke the public
        // Java signature directly so cache reconciliation/removal stays exact.
        using var runtimeClass = cache.Class;
        using var getKeys = runtimeClass.GetMethod(
            "getKeys", Array.Empty<Java.Lang.Class>());
        using var rawKeys = getKeys.Invoke(
            cache, Array.Empty<Java.Lang.Object>())
            ?? throw new InvalidDataException("Media3 returned no cache-key set.");

        if (rawKeys is not Java.Util.ISet keySet)
            throw new InvalidDataException("Media3 returned an unexpected cache-key collection.");

        using var iterator = keySet.Iterator();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        while (iterator.HasNext)
        {
            using var item = iterator.Next();
            var key = item?.ToString();
            if (!string.IsNullOrWhiteSpace(key))
                keys.Add(key);
        }

        return keys;
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

    private void RecordCacheKey(string titleScope, string cacheKey, long position, long length)
    {
        if (position < 0 || length < -1 || (length > 0 && position > long.MaxValue - length))
            throw new InvalidDataException("Media3 supplied an invalid cache byte range.");

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

            var requirements = ReadCacheRequirements();
            if (!requirements.TryGetValue(titleScope, out var titleRequirements))
            {
                titleRequirements = new Dictionary<string, HashSet<CachedRange>>(StringComparer.Ordinal);
                requirements.Add(titleScope, titleRequirements);
            }

            if (!titleRequirements.TryGetValue(cacheKey, out var ranges))
            {
                ranges = new HashSet<CachedRange>();
                titleRequirements.Add(cacheKey, ranges);
            }

            // Media3 uses -1 for an unknown length. It is checked against the
            // final content-length metadata during the completion audit.
            if (ranges.Add(new CachedRange(position, length > 0 ? length : -1)))
                WriteCacheRequirements(requirements);
        }
    }

    private Dictionary<string, Dictionary<string, HashSet<CachedRange>>> ReadCacheRequirements()
    {
        var path = CacheRequirementsPath;
        if (!File.Exists(path))
            return new Dictionary<string, Dictionary<string, HashSet<CachedRange>>>(StringComparer.Ordinal);

        var json = File.ReadAllText(path, Encoding.UTF8);
        if (Encoding.UTF8.GetByteCount(json) > 16 * 1024 * 1024)
            throw new InvalidDataException("The protected-audio cache range index exceeds its supported size.");

        try
        {
            var stored = JsonSerializer.Deserialize<
                Dictionary<string, Dictionary<string, CachedRange[]>>>(json)
                ?? throw new InvalidDataException("The protected-audio cache range index is empty.");

            if (stored.Count > 4096 ||
                stored.Any(title => title.Value is null ||
                    title.Value.Count > MaximumIndexedKeys ||
                    title.Value.Any(resource => resource.Value is null ||
                        resource.Value.Length is 0 or > 4096 ||
                        resource.Value.Any(range => range.Position < 0 || range.Length < -1))))
                throw new InvalidDataException("The protected-audio cache range index exceeds its supported limits.");

            return stored.ToDictionary(
                title => title.Key,
                title => title.Value.ToDictionary(
                    resource => resource.Key,
                    resource => new HashSet<CachedRange>(resource.Value),
                    StringComparer.Ordinal),
                StringComparer.Ordinal);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The protected-audio cache range index is malformed.", exception);
        }
    }

    private void WriteCacheRequirements(
        Dictionary<string, Dictionary<string, HashSet<CachedRange>>> requirements)
    {
        var path = CacheRequirementsPath;
        var temporaryPath = path + ".tmp";
        var payload = requirements.ToDictionary(
            title => title.Key,
            title => title.Value.ToDictionary(
                resource => resource.Key,
                resource => resource.Value
                    .OrderBy(range => range.Position)
                    .ThenBy(range => range.Length)
                    .ToArray(),
                StringComparer.Ordinal),
            StringComparer.Ordinal);
        var json = JsonSerializer.Serialize(payload);
        if (Encoding.UTF8.GetByteCount(json) > 16 * 1024 * 1024)
            throw new InvalidDataException("The protected-audio cache range index exceeds its supported size.");

        File.WriteAllText(temporaryPath, json, new UTF8Encoding(false));
        File.Move(temporaryPath, path, overwrite: true);
    }

    private sealed record CachedRange(long Position, long Length);

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

    private string CacheRequirementsPath
        => Path.Combine(applicationContext.FilesDir!.AbsolutePath!, CacheRequirementsFileName);

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
        private readonly Action<string, string, long, long> recordCacheKey;

        public ScopedCacheKeyFactory(
            string titleScope,
            string manifestUri,
            Action<string, string, long, long> recordCacheKey)
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
            // The URI check above rejects a null DataSpec before this point.
            var position = dataSpec!.Position;
            var length = dataSpec!.Length;
            recordCacheKey(titleScope, cacheKey, position, length);
            return cacheKey;
        }
    }
}
