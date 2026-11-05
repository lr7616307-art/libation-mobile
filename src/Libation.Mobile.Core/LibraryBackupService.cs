using System.Text.Json;

namespace Libation.Mobile.Core;

/// <summary>Creates and validates portable JSON backups of the local catalogue.</summary>
public static class LibraryBackupService
{
    private const int CurrentFormatVersion = 1;
    private const int MaximumEntries = 5000;
    private const long MaximumBackupBytes = 10 * 1024 * 1024;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = false,
        WriteIndented = true
    };

    private sealed record BackupDocument(int FormatVersion, DateTime CreatedAtUtc, List<Audiobook> Books);

    public static async Task ExportAsync(
        Stream destination,
        IEnumerable<Audiobook> books,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(books);
        if (!destination.CanWrite) throw new ArgumentException("The backup destination must be writable.", nameof(destination));

        // Local file paths are device-specific and the backup intentionally excludes audio/artwork.
        // Never export absolute paths that would be stale or disclose private storage locations.
        var snapshot = books.Select(CreatePortableBook).ToList();
        Validate(snapshot);
        var document = new BackupDocument(CurrentFormatVersion, DateTime.UtcNow, snapshot);
        await JsonSerializer.SerializeAsync(destination, document, Options, cancellationToken);
        await destination.FlushAsync(cancellationToken);
    }

    public static async Task<IReadOnlyList<Audiobook>> ImportAsync(
        Stream source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead) throw new ArgumentException("The backup source must be readable.", nameof(source));

        await using var bounded = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (bounded.Length + read > MaximumBackupBytes)
                throw new InvalidDataException("The backup exceeds the 10 MB size limit.");
            await bounded.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        bounded.Position = 0;
        BackupDocument? document;
        try
        {
            document = await JsonSerializer.DeserializeAsync<BackupDocument>(bounded, Options, cancellationToken);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The selected file is not a valid Libation Mobile backup.", ex);
        }

        if (document is null)
            throw new InvalidDataException("The backup file is empty.");
        if (document.FormatVersion != CurrentFormatVersion)
            throw new InvalidDataException($"Unsupported backup format version: {document.FormatVersion}.");
        if (document.Books is null || document.Books.Count == 0)
            throw new InvalidDataException("The backup contains no catalogue entries.");
        Validate(document.Books);
        // Also sanitize imported backups: never trust another device's local filesystem paths.
        return document.Books.Select(CreatePortableBook).ToList().AsReadOnly();
    }

    private static Audiobook CreatePortableBook(Audiobook book)
        => book with
        {
            AudioFilePath = null,
            CoverImagePath = null,
            PlaybackPositionMilliseconds = 0
        };

    private static void Validate(IReadOnlyCollection<Audiobook> books)
    {
        if (books.Count == 0) throw new InvalidDataException("A backup must contain at least one catalogue entry.");
        if (books.Count > MaximumEntries) throw new InvalidDataException($"A backup cannot contain more than {MaximumEntries} entries.");

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var book in books)
        {
            if (book is null ||
                string.IsNullOrWhiteSpace(book.Id) ||
                string.IsNullOrWhiteSpace(book.Title) ||
                string.IsNullOrWhiteSpace(book.Author))
                throw new InvalidDataException("Every backup entry must have an ID, title and author.");
            if (!ids.Add(book.Id))
                throw new InvalidDataException($"The backup contains duplicate audiobook ID: {book.Id}.");
        }
    }
}
