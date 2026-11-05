using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;
using System.Collections.Concurrent;
using System.Globalization;

namespace Libation.Mobile.UI.Converters;

/// <summary>Loads app-managed local cover art for Avalonia image bindings.</summary>
public sealed class CoverImageConverter : IValueConverter
{
    private static readonly ConcurrentDictionary<string, Bitmap> Cache = new(StringComparer.Ordinal);

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string path || string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;

        try
        {
            var fullPath = Path.GetFullPath(path);
            return Cache.GetOrAdd(fullPath, static key => new Bitmap(key));
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
