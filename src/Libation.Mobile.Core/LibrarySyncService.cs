namespace Libation.Mobile.Core;

/// <summary>
/// Synchronizes provider catalogue metadata into the local catalogue while preserving
/// manually created entries. Cover artwork is best-effort and never blocks metadata sync.
/// </summary>
public sealed class LibrarySyncService
{
    private const int MaximumCoverDownloadsPerSync = 100;
    private readonly ILibraryCatalogProvider _provider;
    private readonly ILibraryRepository _repository;
    private readonly CoverArtDownloadService? _coverArtDownloader;

    public LibrarySyncService(ILibraryCatalogProvider provider, ILibraryRepository repository, CoverArtDownloadService? coverArtDownloader = null)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        if (coverArtDownloader is not null)
            _coverArtDownloader = coverArtDownloader;
        else if (OperatingSystem.IsAndroid())
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "covers");
            _coverArtDownloader = new CoverArtDownloadService(new HttpClient { Timeout = TimeSpan.FromSeconds(12) }, directory);
        }
    }

    public async Task<int> SyncAsync(CancellationToken cancellationToken = default)
    {
        var remote = await _provider.GetLibraryAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(remote);
        if (remote.Count > 5000)
            throw new InvalidDataException("The provider catalogue exceeds the 5,000-entry safety limit.");

        var mapped = new List<Audiobook>(remote.Count);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in remote)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item is null || string.IsNullOrWhiteSpace(item.ProviderId) ||
                string.IsNullOrWhiteSpace(item.Title) || string.IsNullOrWhiteSpace(item.Author))
                throw new InvalidDataException("The provider returned an audiobook with missing required metadata.");

            var id = "provider:" + item.ProviderId.Trim();
            if (!ids.Add(id)) throw new InvalidDataException("The provider returned duplicate audiobook IDs.");
            mapped.Add(new Audiobook(id, item.Title.Trim(), item.Author.Trim(), item.Narrator?.Trim() ?? string.Empty,
                item.Length?.Trim() ?? string.Empty, "Library")
            {
                Series = item.Series?.Trim() ?? string.Empty,
                Genre = item.Genre?.Trim() ?? string.Empty,
                Description = item.Description?.Trim() ?? string.Empty,
                CoverImageUrl = NormalizeCoverUrl(item.CoverImageUrl),
                IsPlayableOnAudible = item.IsPlayableOnAudible,
                IsDownloadedOnAudible = item.IsDownloadedOnAudible
            });
        }

        cancellationToken.ThrowIfCancellationRequested();
        var existing = await _repository.GetBooksAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var existingProviderBooks = existing.Where(book => book.Id.StartsWith("provider:", StringComparison.Ordinal))
            .ToDictionary(book => book.Id, StringComparer.Ordinal);

        var mergedProviderBooks = mapped.Select(remoteBook =>
            existingProviderBooks.TryGetValue(remoteBook.Id, out var localBook)
                ? remoteBook with
                {
                    // Preserve cached artwork and non-placeholder user corrections, but
                    // let newly available provider fields replace old null sentinels.
                    Title = IsMissingProviderText(localBook.Title) ? remoteBook.Title : localBook.Title,
                    Author = IsMissingProviderText(localBook.Author) ? remoteBook.Author : localBook.Author,
                    Narrator = IsMissingProviderText(localBook.Narrator) ? remoteBook.Narrator : localBook.Narrator,
                    Length = IsMissingProviderText(localBook.Length) ? remoteBook.Length : localBook.Length,
                    Series = IsMissingProviderText(localBook.Series) ? remoteBook.Series : localBook.Series,
                    Genre = IsMissingProviderText(localBook.Genre) ? remoteBook.Genre : localBook.Genre,
                    Description = IsMissingProviderText(localBook.Description) ? remoteBook.Description : localBook.Description,
                    CoverImageUrl = string.IsNullOrWhiteSpace(remoteBook.CoverImageUrl) ? localBook.CoverImageUrl : remoteBook.CoverImageUrl,
                    CoverImagePath = localBook.CoverImagePath
                }
                : remoteBook).ToArray();

        if (_coverArtDownloader is not null)
        {
            var downloaded = 0;
            for (var i = 0; i < mergedProviderBooks.Length && downloaded < MaximumCoverDownloadsPerSync; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var book = mergedProviderBooks[i];
                if (!string.IsNullOrWhiteSpace(book.CoverImagePath) || string.IsNullOrWhiteSpace(book.CoverImageUrl))
                    continue;
                downloaded++;
                try
                {
                    var path = await _coverArtDownloader.DownloadAsync(book.Id, book.CoverImageUrl, cancellationToken);
                    if (!string.IsNullOrWhiteSpace(path)) mergedProviderBooks[i] = book with { CoverImagePath = path };
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (HttpRequestException) { /* Optional artwork can be retried on a later sync. */ }
                catch (InvalidDataException) { /* Ignore invalid or oversized provider artwork. */ }
                catch (TaskCanceledException) { /* A per-image timeout must not fail catalogue sync. */ }
            }
        }

        var manual = existing.Where(book => !book.Id.StartsWith("provider:", StringComparison.Ordinal)
            && book.Status != "Library" && book.Status != "Demo");
        var merged = manual.Concat(mergedProviderBooks).ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        await _repository.ReplaceAllAsync(merged, cancellationToken);
        return mapped.Count;
    }

    private static bool IsMissingProviderText(string? value)
        => string.IsNullOrWhiteSpace(value)
            || string.Equals(value.Trim(), "[null]", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value.Trim(), "null", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeCoverUrl(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(uri.Host, "m.media-amazon.com", StringComparison.OrdinalIgnoreCase))
            return string.Empty;
        return uri.AbsoluteUri;
    }
}
