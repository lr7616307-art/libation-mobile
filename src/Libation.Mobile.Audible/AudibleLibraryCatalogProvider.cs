using AudibleApi;
using AudibleApi.Common;
using Libation.Mobile.Core;

namespace Libation.Mobile.Audible;

/// <summary>
/// Reads library pages from an authorized AudibleApi instance and maps catalogue
/// metadata into the mobile provider contract.
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

    /// <summary>
    /// Restores an existing encrypted Audible identity and creates a live library
    /// provider. Returns null when the user has not signed in on this installation.
    /// This method never starts a browser login or creates a new device registration.
    /// </summary>
    public static async Task<AudibleLibraryCatalogProvider?> FromSavedIdentityAsync(
        AudibleIdentitySnapshotStore snapshotStore,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshotStore);
        var api = await new AudibleApiSessionFactory(snapshotStore)
            .CreateFromSavedIdentityAsync(cancellationToken)
            .ConfigureAwait(false);
        return api is null ? null : new AudibleLibraryCatalogProvider(api);
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
            // Audible omits product title/subtitle and image metadata from the base
            // library page unless product_attrs and image sizes are requested. Contributors
            // and the two status flags are separate response groups.
            ResponseGroups = LibraryOptions.ResponseGroupOptions.Contributors
                | LibraryOptions.ResponseGroupOptions.ProductAttrs
                | LibraryOptions.ResponseGroupOptions.ProductDesc
                | LibraryOptions.ResponseGroupOptions.IsPlayable
                | LibraryOptions.ResponseGroupOptions.IsDownloaded,
            ImageSizes = LibraryOptions.ImageSizeOptions._500
        };

        var results = new List<RemoteAudiobook>();
        await foreach (var page in getPages(options).WithCancellation(cancellationToken))
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
