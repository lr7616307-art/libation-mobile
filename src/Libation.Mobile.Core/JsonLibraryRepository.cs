using System.Text.Json;

namespace Libation.Mobile.Core;

/// <summary>
/// JSON-backed local catalogue. The caller supplies an app-private file path.
/// Writes replace the catalogue atomically within the same directory.
/// </summary>
public sealed class JsonLibraryRepository : ILibraryRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        WriteIndented = true
    };

    private readonly string _filePath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonLibraryRepository(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = Path.GetFullPath(filePath);
    }

    public async Task<IReadOnlyList<Audiobook>> GetBooksAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await ReadBooksAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task EnsureInitializedAsync(
        IEnumerable<Audiobook> initialBooks,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(initialBooks);
        var snapshot = initialBooks.ToArray();
        ValidateBooks(snapshot);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            // File existence, not catalogue count, distinguishes first launch
            // from a deliberately empty catalogue restored or synced by the user.
            if (!File.Exists(_filePath))
                await WriteBooksAsync(snapshot, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ReplaceAllAsync(
        IEnumerable<Audiobook> books,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(books);
        var snapshot = books.ToArray();
        ValidateBooks(snapshot);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await WriteBooksAsync(snapshot, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UpsertAsync(
        Audiobook book,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(book);
        ValidateBooks([book]);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var books = (await ReadBooksAsync(cancellationToken)).ToList();
            var index = books.FindIndex(existing =>
                string.Equals(existing.Id, book.Id, StringComparison.Ordinal));
            if (index >= 0)
                books[index] = book;
            else
                books.Add(book);

            await WriteBooksAsync(books, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> RemoveAsync(
        string audiobookId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(audiobookId);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var books = (await ReadBooksAsync(cancellationToken)).ToList();
            var removed = books.RemoveAll(book =>
                string.Equals(book.Id, audiobookId, StringComparison.Ordinal)) > 0;
            if (removed)
                await WriteBooksAsync(books, cancellationToken);

            return removed;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IReadOnlyList<Audiobook>> ReadBooksAsync(
        CancellationToken cancellationToken)
    {
        if (!File.Exists(_filePath))
            return [];

        await using var stream = new FileStream(
            _filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 4096, useAsync: true);

        return await JsonSerializer.DeserializeAsync<List<Audiobook>>(
                   stream, JsonOptions, cancellationToken) ?? [];
    }

    private async Task WriteBooksAsync(
        IReadOnlyCollection<Audiobook> books,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_filePath)
            ?? throw new InvalidOperationException("The catalogue path has no parent directory.");
        Directory.CreateDirectory(directory);

        var temporaryPath = _filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(
                temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 4096, useAsync: true))
            {
                await JsonSerializer.SerializeAsync(
                    stream, books, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, _filePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static void ValidateBooks(IReadOnlyCollection<Audiobook> books)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var book in books)
        {
            if (book is null)
                throw new ArgumentException("The catalogue cannot contain null entries.", nameof(books));
            ArgumentException.ThrowIfNullOrWhiteSpace(book.Id);
            if (!ids.Add(book.Id))
                throw new ArgumentException($"Duplicate audiobook ID: {book.Id}", nameof(books));
        }
    }
}
