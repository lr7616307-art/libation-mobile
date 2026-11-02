namespace Libation.Mobile.Core;

public interface ILibraryRepository
{
    Task<IReadOnlyList<Audiobook>> GetBooksAsync(CancellationToken cancellationToken = default);
}
