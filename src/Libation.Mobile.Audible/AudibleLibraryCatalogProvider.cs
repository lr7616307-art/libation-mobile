using AudibleApi;
using AudibleApi.Common;
using Libation.Mobile.Core;

namespace Libation.Mobile.Audible;

/// <summary>
/// Reads library pages from an already-authorized AudibleApi instance and maps catalogue
/// metadata into the mobile provider contract. Authentication and identity persistence are
/// intentionally owned by the caller.
/// </summary>
public sealed class AudibleLibraryCatalogProvider : ILibraryCatalogProvider
{
    private const int PageSize = 50;
    private const int MaxConcurrentPages = 2;
    private const int MaximumLibraryItems = 5000;

    private readonly Func<LibraryOptions, IAsyncEnumerable<Item[]>> getPages;

    public AudibleLibraryCatalogProvider(Api api)
        : this(CreatePageReader(api))
    {
    }

    /// <summary>Creates a provider around a page reader; useful for deterministic tests and alternate hosts.</summary>
    public AudibleLibraryCatalogProvider(
        Func<LibraryOptions, IAsyncEnumerable<Item[]>> getPages)
    {
        this.getPages = getPages ?? throw new ArgumentNullException(nameof(getPages));
    }

    private static Func<LibraryOptions, IAsyncEnumerable<Item[]>> CreatePageReader(Api api)
    {
        ArgumentNullException.ThrowIfNull(api);
        return options => api.GetLibraryItemsPagesAsync(options, PageSize, MaxConcurrentPages);
    }

    public async Task<IReadOnlyList<RemoteAudiobook>> GetLibraryAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var options = new LibraryOptions
        {
            // Contributor fields are explicitly requested; core title/runtime fields are
            // part of the library response itself.
            ResponseGroups = LibraryOptions.ResponseGroupOptions.Contributors
        };

        var results = new List<RemoteAudiobook>();
        await foreach (var page in getPages(options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (page is null)
                continue;

            if (page.Length > MaximumLibraryItems - results.Count)
                throw new InvalidDataException(
                    "The Audible library exceeds the 5,000-entry mobile safety limit.");

            foreach (var item in page)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (item is null)
                    continue;

                results.Add(AudibleItemMapper.Map(item));
            }
        }

        return results;
    }
}
