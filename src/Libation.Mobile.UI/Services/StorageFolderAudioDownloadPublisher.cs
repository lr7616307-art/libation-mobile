using Avalonia.Platform.Storage;
using Libation.Mobile.Core;

namespace Libation.Mobile.UI.Services;

/// <summary>
/// Publishes a fully downloaded, user-authorized audio file into the Android
/// Storage Access Framework folder selected in Settings and attaches a private
/// playable copy to the local catalogue.
/// </summary>
public sealed class StorageFolderAudioDownloadPublisher : IAudioDownloadCompletionHandler, IProtectedAudioDownloadCompletionHandler
{
    private readonly Func<IStorageProvider?> _storageProviderFactory;
    private readonly IDownloadSettingsStore _settingsStore;
    private readonly ILibraryRepository _repository;
    private readonly LocalAudioImportService _audioImportService;

    public StorageFolderAudioDownloadPublisher(
        Func<IStorageProvider?> storageProviderFactory,
        IDownloadSettingsStore settingsStore,
        ILibraryRepository repository,
        LocalAudioImportService audioImportService)
    {
        _storageProviderFactory = storageProviderFactory ?? throw new ArgumentNullException(nameof(storageProviderFactory));
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _audioImportService = audioImportService ?? throw new ArgumentNullException(nameof(audioImportService));
    }

    public async Task PublishAsync(
        DownloadQueueItem item,
        string stagingFilePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingFilePath);

        var book = (await _repository.GetBooksAsync(cancellationToken))
            .FirstOrDefault(entry => string.Equals(entry.Id, item.AudiobookId, StringComparison.Ordinal));
        if (book is null)
            throw new InvalidOperationException("The audiobook is no longer in the local catalogue.");

        var staging = new FileInfo(stagingFilePath);
        if (!staging.Exists || staging.Length == 0)
            throw new InvalidDataException("The downloaded audio file is missing or empty.");

        var extension = staging.Extension.ToLowerInvariant();
        if (!LocalAudioImportService.IsSupportedFileName("audio" + extension))
            throw new InvalidDataException("The downloaded file format is not supported by the VØRATH player.");

        var settings = (await _settingsStore.GetAsync(cancellationToken)).Normalize();
        if (!settings.HasSelectedDirectory)
            throw new InvalidOperationException("Choose a download folder in Settings before starting downloads.");

        var storageProvider = _storageProviderFactory();
        if (storageProvider is null)
            throw new InvalidOperationException("Android storage access is unavailable. Reopen the app and retry.");

        var root = await storageProvider.OpenFolderBookmarkAsync(settings.DirectoryBookmark!);
        if (root is null)
            throw new UnauthorizedAccessException("The saved download folder permission expired. Choose the folder again in Settings.");

        var openedFolders = new List<IStorageFolder> { root };
        IStorageFile? outputFile = null;
        var published = false;
        try
        {
            IStorageFolder destination = root;
            foreach (var segment in GetOrganizationSegments(book, settings.FolderOrganization))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var next = await destination.GetFolderAsync(segment);
                next ??= await destination.CreateFolderAsync(segment);
                if (next is null)
                    throw new IOException("Could not create a folder in the selected download location.");
                openedFolders.Add(next);
                destination = next;
            }

            var idSuffix = SafeSegment(book.Id);
            if (idSuffix.Length > 10) idSuffix = idSuffix[..10];
            var fileName = $"{SafeSegment(book.Title)} [{idSuffix}]{extension}";
            outputFile = await destination.CreateFileAsync(fileName);
            if (outputFile is null)
                throw new IOException("Could not create the audiobook file in the selected folder.");

            try
            {
                await using var input = new FileStream(
                    stagingFilePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                    64 * 1024, useAsync: true);
                await using var output = await outputFile.OpenWriteAsync();
                await input.CopyToAsync(output, 64 * 1024, cancellationToken);
                await output.FlushAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch
            {
                try { await outputFile.DeleteAsync(); }
                catch (Exception) { /* Preserve the original copy failure. */ }
                throw;
            }

            // VØRATH's current player uses app-private file paths. Keep the selected
            // folder copy as the user's export and add a private playable copy too.
            await using var localInput = new FileStream(
                stagingFilePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                64 * 1024, useAsync: true);
            var localPath = await _audioImportService.ImportAsync(
                book.Id, fileName, localInput, cancellationToken);

            await _repository.UpsertAsync(book with
            {
                AudioFilePath = localPath,
                FileSizeBytes = staging.Length,
                Status = "Downloaded"
            }, cancellationToken);
            published = true;
        }
        finally
        {
            outputFile?.Dispose();
            for (var index = openedFolders.Count - 1; index >= 0; index--)
                openedFolders[index].Dispose();
        }

        if (!published)
            throw new IOException("The audiobook could not be published to the selected folder.");
    }

    public async Task PublishProtectedAsync(
        DownloadQueueItem item,
        string stagingFilePath,
        AudioProtectionDescriptor protection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingFilePath);
        ArgumentNullException.ThrowIfNull(protection);
        if (!protection.RequiresProtectedMediaPipeline)
            throw new ArgumentException("The protected publisher requires a protected media licence.", nameof(protection));

        var book = (await _repository.GetBooksAsync(cancellationToken))
            .FirstOrDefault(entry => string.Equals(entry.Id, item.AudiobookId, StringComparison.Ordinal));
        if (book is null || !book.IsProviderCatalogueEntry)
            throw new InvalidOperationException("Protected Audible files can only be attached to Audible catalogue entries.");

        var staging = new FileInfo(stagingFilePath);
        if (!staging.Exists || staging.Length == 0)
            throw new InvalidDataException("The protected Audible file is missing or empty.");

        var extension = staging.Extension.ToLowerInvariant();
        if (extension is not (".aaxc" or ".protected"))
            throw new InvalidDataException("Audible returned an unexpected protected media container.");

        var settings = (await _settingsStore.GetAsync(cancellationToken)).Normalize();
        if (!settings.HasSelectedDirectory)
            throw new InvalidOperationException("Choose a download folder in Settings before starting downloads.");

        var storageProvider = _storageProviderFactory();
        if (storageProvider is null)
            throw new InvalidOperationException("Android storage access is unavailable. Reopen the app and retry.");

        var root = await storageProvider.OpenFolderBookmarkAsync(settings.DirectoryBookmark!);
        if (root is null)
            throw new UnauthorizedAccessException("The saved download folder permission expired. Choose the folder again in Settings.");

        var openedFolders = new List<IStorageFolder> { root };
        IStorageFile? outputFile = null;
        var published = false;
        try
        {
            IStorageFolder destination = root;
            foreach (var segment in GetOrganizationSegments(book, settings.FolderOrganization))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var next = await destination.GetFolderAsync(segment);
                next ??= await destination.CreateFolderAsync(segment);
                if (next is null)
                    throw new IOException("Could not create a folder in the selected download location.");
                openedFolders.Add(next);
                destination = next;
            }

            var idSuffix = SafeSegment(book.Id);
            if (idSuffix.Length > 10) idSuffix = idSuffix[..10];
            var fileName = $"{SafeSegment(book.Title)} [{idSuffix}]{extension}";
            outputFile = await destination.CreateFileAsync(fileName);
            if (outputFile is null)
                throw new IOException("Could not create the protected audiobook file in the selected folder.");

            try
            {
                await using var input = new FileStream(
                    stagingFilePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                    64 * 1024, useAsync: true);
                await using var output = await outputFile.OpenWriteAsync();
                await input.CopyToAsync(output, 64 * 1024, cancellationToken);
                await output.FlushAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch
            {
                try { await outputFile.DeleteAsync(); }
                catch (Exception) { /* Preserve the original copy failure. */ }
                throw;
            }

            // Do not attach protected bytes to AudioFilePath or the normal player.
            // Only the selected-folder backup name and non-secret format metadata
            // are kept in the catalogue.
            await _repository.UpsertAsync(book with
            {
                ProtectedAudioFileName = fileName,
                ProtectedAudioFolderName = settings.DirectoryName ?? "Selected folder",
                ProtectedAudioProtection = protection.Kind.ToString(),
                FileSizeBytes = staging.Length
            }, cancellationToken);
            published = true;
        }
        finally
        {
            outputFile?.Dispose();
            for (var index = openedFolders.Count - 1; index >= 0; index--)
                openedFolders[index].Dispose();
        }

        if (!published)
            throw new IOException("The protected Audible audiobook could not be published to the selected folder.");
    }

    private static IEnumerable<string> GetOrganizationSegments(
        Audiobook book,
        DownloadFolderOrganization organization)
    {
        if (organization == DownloadFolderOrganization.Flat)
            return [];

        var author = SafeSegment(book.Author);
        var title = SafeSegment(book.Title);
        if (organization == DownloadFolderOrganization.AuthorSeriesBook)
            return [author, SafeSegment(string.IsNullOrWhiteSpace(book.Series) ? "Other" : book.Series), title];

        return [author, title];
    }

    private static string SafeSegment(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var cleaned = new string((value ?? string.Empty)
            .Select(character => char.IsControl(character) || invalid.Contains(character) ||
                                 character is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|'
                ? '_'
                : character)
            .ToArray())
            .Trim()
            .TrimEnd('.');

        if (string.IsNullOrWhiteSpace(cleaned))
            cleaned = "Unknown";
        return cleaned.Length > 80 ? cleaned[..80].TrimEnd() : cleaned;
    }
}
