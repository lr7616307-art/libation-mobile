namespace Libation.Mobile.Core;

public interface ILibraryRepository
{
    Task<IReadOnlyList<Audiobook>> GetBooksAsync(CancellationToken cancellationToken = default);
    Task ReplaceAllAsync(IEnumerable<Audiobook> books, CancellationToken cancellationToken = default);
    Task UpsertAsync(Audiobook book, CancellationToken cancellationToken = default);
    Task<bool> RemoveAsync(string audiobookId, CancellationToken cancellationToken = default);
}
