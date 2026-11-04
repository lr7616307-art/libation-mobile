namespace Libation.Mobile.Core;

/// <summary>
/// Synchronizes provider catalogue metadata into the local catalogue while preserving
/// manually created entries. This class does not authenticate or download audio.
/// </summary>
public sealed class LibrarySyncService(
    ILibraryCatalogProvider provider,
    ILibraryRepository repository)
{
    public async Task<int> SyncAsync(CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(repository);

        var remote = await provider.GetLibraryAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(remote);
        if (remote.Count > 5000)
            throw new InvalidDataException("The provider catalogue exceeds the 5,000-entry safety limit.");

        var mapped = new List<Audiobook>(remote.Count);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in remote)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item is null ||
                string.IsNullOrWhiteSpace(item.ProviderId) ||
                string.IsNullOrWhiteSpace(item.Title) ||
                string.IsNullOrWhiteSpace(item.Author))
                throw new InvalidDataException("The provider returned an audiobook with missing required metadata.");

            var id = "provider:" + item.ProviderId.Trim();
            if (!ids.Add(id))
                throw new InvalidDataException("The provider returned duplicate audiobook IDs.");

            mapped.Add(new Audiobook(
                id,
                item.Title.Trim(),
                item.Author.Trim(),
                item.Narrator?.Trim() ?? string.Empty,
                item.Length?.Trim() ?? string.Empty,
                "Library"));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var existing = await repository.GetBooksAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var existingProviderBooks = existing
            .Where(book => book.Id.StartsWith("provider:", StringComparison.Ordinal))
            .ToDictionary(book => book.Id, StringComparer.Ordinal);

        // Keep the user's locally edited metadata for provider IDs that still
        // exist remotely. Removed remote IDs are intentionally not retained.
        var mergedProviderBooks = mapped.Select(remoteBook =>
            existingProviderBooks.TryGetValue(remoteBook.Id, out var localBook)
                ? localBook
                : remoteBook);

        var manual = existing.Where(book =>
            !book.Id.StartsWith("provider:", StringComparison.Ordinal)
            && book.Status != "Library"
            && book.Status != "Demo");
        var merged = manual.Concat(mergedProviderBooks).ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        await repository.ReplaceAllAsync(merged, cancellationToken);
        return mapped.Count;
    }
}
