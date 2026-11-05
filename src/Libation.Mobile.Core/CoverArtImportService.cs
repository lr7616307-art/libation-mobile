using System.Security.Cryptography;
using System.Text;

namespace Libation.Mobile.Core;

/// <summary>Copies user-selected cover images into app-managed private storage.</summary>
public sealed class CoverArtImportService
{
    public const long DefaultMaximumFileBytes = 15L * 1024 * 1024;
    private const int BufferSize = 32 * 1024;
    private readonly string _storageDirectory;
    private readonly long _maximumFileBytes;

    public CoverArtImportService(string storageDirectory, long maximumFileBytes = DefaultMaximumFileBytes)
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
        if (!source.CanRead) throw new ArgumentException("The selected image stream is not readable.", nameof(source));

        var extension = Path.GetExtension(sourceFileName).ToLowerInvariant();
        if (extension is not ".jpg" and not ".jpeg" and not ".png" and not ".webp")
            throw new NotSupportedException("Choose a JPG, PNG or WebP cover image.");

        Directory.CreateDirectory(_storageDirectory);
        var idHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(audiobookId))).ToLowerInvariant();
        var destination = Path.Combine(_storageDirectory, idHash + extension);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        long total = 0;
        var buffer = new byte[BufferSize];
        var header = new byte[12];
        var headerLength = 0;

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
                        throw new InvalidDataException($"Cover image exceeds the {_maximumFileBytes} byte import limit.");
                    if (headerLength < header.Length)
                    {
                        var copy = Math.Min(read, header.Length - headerLength);
                        buffer.AsSpan(0, copy).CopyTo(header.AsSpan(headerLength));
                        headerLength += copy;
                    }
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }

                if (total == 0) throw new InvalidDataException("The selected cover image is empty.");
                if (!MatchesImageSignature(extension, header.AsSpan(0, headerLength)))
                    throw new InvalidDataException("The selected file does not match its JPG, PNG or WebP extension.");
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
           && Path.GetExtension(fileName).ToLowerInvariant() is ".jpg" or ".jpeg" or ".png" or ".webp";

    private static bool MatchesImageSignature(string extension, ReadOnlySpan<byte> header)
        => extension switch
        {
            ".jpg" or ".jpeg" => header.Length >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF,
            ".png" => header.Length >= 8 && header[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }),
            ".webp" => header.Length >= 12 && header[..4].SequenceEqual("RIFF"u8) && header[8..12].SequenceEqual("WEBP"u8),
            _ => false
        };
}
