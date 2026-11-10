using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;
using System.Globalization;

namespace Libation.Mobile.UI.Converters;

/// <summary>
/// Loads app-managed local cover art as bounded-size bitmaps. Weak references avoid
/// keeping every cover in memory after its image controls are no longer using it.
/// </summary>
public sealed class CoverImageConverter : IValueConverter
{
    private const int MaximumCoverWidthPixels = 512;
    private const int MaximumCacheEntries = 48;
    private static readonly object CacheLock = new();
    private static readonly Dictionary<string, WeakReference<Bitmap>> Cache = new(StringComparer.Ordinal);

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string path || string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;

        try
        {
            var fullPath = Path.GetFullPath(path);
            var file = new FileInfo(fullPath);
            if (!file.Exists) return null;

            // Include file metadata so replacing a cover at the same path does not
            // leave a stale bitmap in the cache.
            var cacheKey = $"{fullPath}|{file.Length}|{file.LastWriteTimeUtc.Ticks}";
            lock (CacheLock)
            {
                if (Cache.TryGetValue(cacheKey, out var weakReference)
                    && weakReference.TryGetTarget(out var cachedBitmap))
                    return cachedBitmap;

                using var stream = new FileStream(
                    fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var bitmap = Bitmap.DecodeToWidth(stream, MaximumCoverWidthPixels);
                Cache[cacheKey] = new WeakReference<Bitmap>(bitmap);

                // Prune dead entries first, then cap path metadata. Never dispose an
                // evicted bitmap here: a visible Image control may still reference it.
                foreach (var staleKey in Cache
                    .Where(entry => !entry.Value.TryGetTarget(out _))
                    .Select(entry => entry.Key)
                    .ToArray())
                    Cache.Remove(staleKey);

                while (Cache.Count > MaximumCacheEntries)
                    Cache.Remove(Cache.Keys.First());

                return bitmap;
            }
        }
        catch (Exception)
        {
            return null;
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException("Cover artwork bindings are one-way.");
}

/// <summary>Shows the image only while its local cover file exists.</summary>
public sealed class CoverImageVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string path && !string.IsNullOrWhiteSpace(path) && File.Exists(path);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException("Cover artwork visibility bindings are one-way.");
}

/// <summary>Maps audiobook listening state to a restrained status background color.</summary>
public sealed class ListeningStatusBackgroundConverter : IValueConverter
{
    public static ListeningStatusBackgroundConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value?.ToString() switch
        {
            "Finished" => Avalonia.Media.Brush.Parse("#2EAD65"),
            "In progress" => Avalonia.Media.Brush.Parse("#C63E46"),
            _ => Avalonia.Media.Brush.Parse("#C17A18")
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException("Listening status colors are one-way.");
}
