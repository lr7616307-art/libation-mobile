namespace Libation.Mobile.Core;

/// <summary>
/// Converts an already-unprotected, locally owned audio file into an AAC/MP4
/// audiobook container. Implementations must reject provider-protected AAX/AAXC.
/// </summary>
public interface IUnprotectedAudioConverter
{
    Task ConvertToM4bAsync(
        string sourceFilePath,
        string destinationFilePath,
        CancellationToken cancellationToken = default);
}

/// <summary>Input allow-list for local conversion. Protected Audible containers are never accepted.</summary>
public static class LocalAudioConversionPolicy
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".m4a", ".m4b", ".aac", ".wav", ".flac", ".ogg", ".opus", ".3gp", ".mp4", ".webm"
    };

    public static bool IsProtectedContainer(string? filePath)
    {
        var extension = Path.GetExtension(filePath ?? string.Empty);
        return extension.Equals(".aax", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".aaxc", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".protected", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsSupportedUnprotectedInput(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || IsProtectedContainer(filePath))
            return false;

        return SupportedExtensions.Contains(Path.GetExtension(filePath));
    }

    public static void EnsureSupportedUnprotectedInput(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (IsProtectedContainer(filePath))
            throw new NotSupportedException(
                "Protected Audible AAX/AAXC files cannot be converted by the unprotected-audio exporter.");
        if (!IsSupportedUnprotectedInput(filePath))
            throw new NotSupportedException(
                "This audio format is not supported by the local M4B exporter.");
    }
}
