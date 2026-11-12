using System.Text.Json;

namespace Libation.Mobile.Core;

public enum DownloadFolderOrganization
{
    Flat,
    AuthorBook,
    AuthorSeriesBook
}

public sealed record DownloadSettings(
    string? DirectoryBookmark = null,
    string? DirectoryName = null,
    DownloadFolderOrganization FolderOrganization = DownloadFolderOrganization.AuthorBook)
{
    public bool HasSelectedDirectory => !string.IsNullOrWhiteSpace(DirectoryBookmark);

    public string DirectoryLabel => HasSelectedDirectory
        ? (string.IsNullOrWhiteSpace(DirectoryName) ? "Selected folder" : DirectoryName)
        : "Not selected — choose a folder";

    public string FolderOrganizationLabel => FolderOrganization switch
    {
        DownloadFolderOrganization.Flat => "All books in one folder",
        DownloadFolderOrganization.AuthorBook => "Author / Book",
        DownloadFolderOrganization.AuthorSeriesBook => "Author / Series / Book",
        _ => "Author / Book"
    };

    public string FolderOrganizationExample => FolderOrganization switch
    {
        DownloadFolderOrganization.Flat => "Downloads/Book Title.m4b",
        DownloadFolderOrganization.AuthorBook => "Downloads/Author/Book Title/Book Title.m4b",
        DownloadFolderOrganization.AuthorSeriesBook => "Downloads/Author/Series/Book Title/Book Title.m4b",
        _ => "Downloads/Author/Book Title/Book Title.m4b"
    };

    public DownloadSettings Normalize()
    {
        var organization = Enum.IsDefined(FolderOrganization)
            ? FolderOrganization
            : DownloadFolderOrganization.AuthorBook;
        var name = string.IsNullOrWhiteSpace(DirectoryName)
            ? null
            : DirectoryName.Trim();
        var bookmark = string.IsNullOrWhiteSpace(DirectoryBookmark)
            ? null
            : DirectoryBookmark.Trim();
        return this with
        {
            DirectoryBookmark = bookmark,
            DirectoryName = name,
            FolderOrganization = organization
        };
    }
}

public interface IDownloadSettingsStore
{
    Task<DownloadSettings> GetAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(DownloadSettings settings, CancellationToken cancellationToken = default);
}

/// <summary>
/// Stores only the Android Storage Access Framework bookmark and output organization.
/// The bookmark preserves the user's granted folder access across app launches.
/// </summary>
public sealed class JsonDownloadSettingsStore : IDownloadSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _filePath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonDownloadSettingsStore(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = Path.GetFullPath(filePath);
    }

    public async Task<DownloadSettings> GetAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(_filePath))
                return new DownloadSettings();

            await using var stream = new FileStream(
                _filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
            var settings = await JsonSerializer.DeserializeAsync<DownloadSettings>(
                stream, JsonOptions, cancellationToken);
            return (settings ?? new DownloadSettings()).Normalize();
        }
        catch (JsonException)
        {
            // A damaged preferences file must not make the library unavailable.
            return new DownloadSettings();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(DownloadSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var normalized = settings.Normalize();

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var directory = Path.GetDirectoryName(_filePath)
                ?? throw new InvalidOperationException("Download settings path has no parent directory.");
            Directory.CreateDirectory(directory);
            var temporaryPath = _filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";

            try
            {
                await using (var stream = new FileStream(
                    temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true))
                {
                    await JsonSerializer.SerializeAsync(stream, normalized, JsonOptions, cancellationToken);
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
        finally
        {
            _gate.Release();
        }
    }
}
