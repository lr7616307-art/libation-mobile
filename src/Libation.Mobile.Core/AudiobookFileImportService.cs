using System.Security.Cryptography;
using System.Text;
using ATL.AudioData;
using ATL;

namespace Libation.Mobile.Core;

public sealed record AudiobookImportResult(
    string AudioFilePath,
    string Title,
    string Author,
    string Narrator,
    string Length,
    string Description,
    long FileSizeBytes,
    string? CoverImagePath,
    IReadOnlyList<AudiobookChapter> Chapters);

/// <summary>
/// Imports a selected audiobook as one item and reads only metadata actually
/// embedded in that file. ATL reads tags, embedded pictures and chapter markers.
/// </summary>
public sealed class AudiobookFileImportService(LocalAudioImportService audioImport, string coverDirectory)
{
    private readonly LocalAudioImportService _audioImport = audioImport ?? throw new ArgumentNullException(nameof(audioImport));
    private readonly string _coverDirectory = Path.GetFullPath(coverDirectory ?? throw new ArgumentNullException(nameof(coverDirectory)));

    public async Task<AudiobookImportResult> ImportAsync(
        string audiobookId,
        string fileName,
        Stream source,
        CancellationToken cancellationToken = default)
    {
        var audioPath = await _audioImport.ImportAsync(audiobookId, fileName, source, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        var fallbackTitle = Path.GetFileNameWithoutExtension(fileName);
        try
        {
            return await Task.Run(() => ReadMetadata(audiobookId, audioPath, fallbackTitle), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TryDeleteImportedFile(audioPath);
            throw;
        }
        catch (Exception ex)
        {
            TryDeleteImportedFile(audioPath);
            throw new InvalidDataException("The selected file could not be read as a supported audiobook.", ex);
        }
    }

    private static void TryDeleteImportedFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception) { /* Cleanup is best-effort; the import still reports failure. */ }
    }

    private AudiobookImportResult ReadMetadata(string id, string audioPath, string fallbackTitle)
    {
        var track = new Track(audioPath);
        var title = FirstNonEmpty(track.Title, track.Album, fallbackTitle);
        var author = FirstNonEmpty(track.Artist, track.AlbumArtist, track.Composer);
        var narrator = track.AdditionalFields
            .Where(pair => pair.Key.Contains("narrator", StringComparison.OrdinalIgnoreCase))
            .Select(pair => pair.Value)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;

        var durationMs = double.IsFinite(track.DurationMs) && track.DurationMs > 0
            ? (long)Math.Round(track.DurationMs) : 0;
        var length = durationMs > 0 ? FormatDuration(durationMs) : string.Empty;

        var description = track.AdditionalFields
            .Where(pair =>
                pair.Key.Contains("description", StringComparison.OrdinalIgnoreCase) ||
                pair.Key.Contains("synopsis", StringComparison.OrdinalIgnoreCase) ||
                pair.Key.Contains("comment", StringComparison.OrdinalIgnoreCase))
            .Select(pair => pair.Value)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
        var fileSizeBytes = new FileInfo(audioPath).Length;

        var chapters = track.Chapters
            .Where(chapter => !string.IsNullOrWhiteSpace(chapter.Title) && chapter.StartTime >= 0)
            .OrderBy(chapter => chapter.StartTime)
            .Select(chapter => new AudiobookChapter(chapter.Title.Trim(), Convert.ToInt64(chapter.StartTime)))
            .ToArray();

        var coverPath = SaveEmbeddedCover(id, track.EmbeddedPictures.FirstOrDefault()?.PictureData);
        return new AudiobookImportResult(audioPath, title, author, narrator, length, description, fileSizeBytes, coverPath, chapters);
    }

    private string? SaveEmbeddedCover(string id, byte[]? bytes)
    {
        if (bytes is not { Length: > 12 } || bytes.Length > 15 * 1024 * 1024) return null;
        var extension = DetectImageExtension(bytes);
        if (extension is null) return null;

        Directory.CreateDirectory(_coverDirectory);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id))).ToLowerInvariant();
        var path = Path.Combine(_coverDirectory, hash + extension);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static string? DetectImageExtension(byte[] data)
    {
        if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF) return ".jpg";
        if (data.Length >= 8 && data.AsSpan(0, 8).SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })) return ".png";
        if (data.Length >= 12 && data.AsSpan(0, 4).SequenceEqual("RIFF"u8) && data.AsSpan(8, 4).SequenceEqual("WEBP"u8)) return ".webp";
        return null;
    }

    private static string FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;

    private static string FormatDuration(long milliseconds)
    {
        var duration = TimeSpan.FromMilliseconds(milliseconds);
        return duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours}h {duration.Minutes:D2}m"
            : $"{duration.Minutes}m {duration.Seconds:D2}s";
    }
}
