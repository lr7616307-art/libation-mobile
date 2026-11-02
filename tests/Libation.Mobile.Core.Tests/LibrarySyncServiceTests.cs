using Libation.Mobile.Core;
using Xunit;

namespace Libation.Mobile.Core.Tests;

public sealed class LibrarySyncServiceTests
{
    [Fact]
    public async Task Sync_ReplacesProviderEntriesAndPreservesManualEntries()
    {
        using var directory = new TemporaryDirectory();
        var repository = new JsonLibraryRepository(Path.Combine(directory.Path, "library.json"));
        await repository.ReplaceAllAsync([
            new Audiobook("provider:old", "Old title", "Author", "", "", "Library"),
            new Audiobook("manual:1", "My book", "Me", "", "", "Manually added"),
            new Audiobook("demo:1", "Demo", "Demo author", "", "", "Demo")
        ]);
        var service = new LibrarySyncService(
            new FakeProvider([new RemoteAudiobook("aud-1", "  New title ", " Author ", " Narrator ", "8h")]),
            repository);

        var count = await service.SyncAsync();
        var saved = await repository.GetBooksAsync();

        Assert.Equal(1, count);
        Assert.Contains(saved, x => x.Id == "manual:1");
        Assert.Contains(saved, x => x.Id == "provider:aud-1" && x.Title == "New title");
        Assert.DoesNotContain(saved, x => x.Id == "provider:old");
        Assert.DoesNotContain(saved, x => x.Id == "demo:1");
    }

    [Fact]
    public async Task Sync_RejectsDuplicateProviderIdsWithoutReplacingLocalCatalogue()
    {
        using var directory = new TemporaryDirectory();
        var repository = new JsonLibraryRepository(Path.Combine(directory.Path, "library.json"));
        await repository.ReplaceAllAsync([new Audiobook("manual:1", "Keep", "Author", "", "", "Manually added")]);
        var service = new LibrarySyncService(new FakeProvider([
            new RemoteAudiobook("same", "One", "Author", "", ""),
            new RemoteAudiobook("same", "Two", "Author", "", "")
        ]), repository);

        await Assert.ThrowsAsync<InvalidDataException>(() => service.SyncAsync());
        Assert.Contains((await repository.GetBooksAsync()), x => x.Id == "manual:1");
    }

    [Fact]
    public async Task Sync_RejectsMissingRequiredMetadata()
    {
        using var directory = new TemporaryDirectory();
        var repository = new JsonLibraryRepository(Path.Combine(directory.Path, "library.json"));
        var service = new LibrarySyncService(new FakeProvider([new RemoteAudiobook("", "Title", "Author", "", "")]), repository);

        await Assert.ThrowsAsync<InvalidDataException>(() => service.SyncAsync());
        Assert.Empty(await repository.GetBooksAsync());
    }

    [Fact]
    public async Task Sync_RejectsOversizedCatalogueWithoutReplacingLocalCatalogue()
    {
        using var directory = new TemporaryDirectory();
        var repository = new JsonLibraryRepository(Path.Combine(directory.Path, "library.json"));
        await repository.ReplaceAllAsync([new Audiobook("manual:1", "Keep", "Author", "", "", "Manually added")]);
        var oversized = Enumerable.Range(0, 5001)
            .Select(i => new RemoteAudiobook($"id-{i}", $"Title {i}", "Author", "", ""))
            .ToArray();
        var service = new LibrarySyncService(new FakeProvider(oversized), repository);

        await Assert.ThrowsAsync<InvalidDataException>(() => service.SyncAsync());
        Assert.Contains((await repository.GetBooksAsync()), x => x.Id == "manual:1");
    }

    [Fact]
    public async Task Sync_PropagatesProviderFailureWithoutReplacingLocalCatalogue()
    {
        using var directory = new TemporaryDirectory();
        var repository = new JsonLibraryRepository(Path.Combine(directory.Path, "library.json"));
        await repository.ReplaceAllAsync([new Audiobook("manual:1", "Keep", "Author", "", "", "Manually added")]);
        var service = new LibrarySyncService(new ThrowingProvider(), repository);

        await Assert.ThrowsAsync<HttpRequestException>(() => service.SyncAsync());
        Assert.Contains((await repository.GetBooksAsync()), x => x.Id == "manual:1");
    }

    [Fact]
    public async Task Sync_HonorsCancellationBeforeWriting()
    {
        using var directory = new TemporaryDirectory();
        var repository = new JsonLibraryRepository(Path.Combine(directory.Path, "library.json"));
        await repository.ReplaceAllAsync([new Audiobook("manual:1", "Keep", "Author", "", "", "Manually added")]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var service = new LibrarySyncService(new FakeProvider([
            new RemoteAudiobook("new", "New", "Author", "", "")
        ]), repository);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.SyncAsync(cancellation.Token));
        Assert.Contains((await repository.GetBooksAsync()), x => x.Id == "manual:1");
    }

    private sealed class FakeProvider(IReadOnlyList<RemoteAudiobook> books) : ILibraryCatalogProvider
    {
        public Task<IReadOnlyList<RemoteAudiobook>> GetLibraryAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(books);
        }
    }

    private sealed class ThrowingProvider : ILibraryCatalogProvider
    {
        public Task<IReadOnlyList<RemoteAudiobook>> GetLibraryAsync(CancellationToken cancellationToken = default)
            => throw new HttpRequestException("Simulated provider transport failure.");
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "LibationMobileSyncTests", Guid.NewGuid().ToString("N"));
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, true); }
    }
}
