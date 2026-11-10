using System.Security.Cryptography;
using System.Text;

namespace Libation.Mobile.Core;

/// <summary>Copies user-selected, ordinary audio files into app-managed storage.</summary>
public sealed class LocalAudioImportService
{
    public const long DefaultMaximumFileBytes = 2L * 1024 * 1024 * 1024;
    private const int BufferSize = 64 * 1024;
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".m4a", ".m4b", ".aac", ".flac", ".ogg", ".opus", ".wav",
        ".wma", ".aiff", ".aif", ".alac", ".ape", ".wv", ".mka", ".mp4", ".3gp", ".amr"
    };

    private readonly string _storageDirectory;
    private readonly long _maximumFileBytes;

    public LocalAudioImportService(string storageDirectory, long maximumFileBytes = DefaultMaximumFileBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageDirectory);
        if (maximumFileBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maximumFileBytes));
        _storageDirectory = Path.GetFullPath(storageDirectory);
        _maximumFileBytes = maximumFileBytes;
    }

    public async Task<string> ImportAsync(
        string audiobookId,
        string sourceFileName,
        Stream source,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(audiobookId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceFileName);
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead) throw new ArgumentException("The selected audio stream is not readable.", nameof(source));

        var extension = Path.GetExtension(sourceFileName);
        if (!SupportedExtensions.Contains(extension))
            throw new NotSupportedException("Choose an MP3, M4A, M4B, AAC, FLAC, OGG, OPUS or WAV audio file.");

        Directory.CreateDirectory(_storageDirectory);
        var idHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(audiobookId))).ToLowerInvariant();
        var destination = Path.Combine(_storageDirectory, idHash + extension.ToLowerInvariant());
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        long total = 0;
        var buffer = new byte[BufferSize];

        try
        {
            await using (var output = new FileStream(
                temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                BufferSize, useAsync: true))
            {
                while (true)
                {
                    var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                    if (read == 0) break;
                    total = checked(total + read);
                    if (total > _maximumFileBytes)
                        throw new InvalidDataException($"Audio file exceeds the {_maximumFileBytes} byte import limit.");
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }

                if (total == 0) throw new InvalidDataException("The selected audio file is empty.");
                await output.FlushAsync(cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: true);
            return destination;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public static bool IsSupportedFileName(string? fileName)
        => !string.IsNullOrWhiteSpace(fileName)
           && SupportedExtensions.Contains(Path.GetExtension(fileName));
}
