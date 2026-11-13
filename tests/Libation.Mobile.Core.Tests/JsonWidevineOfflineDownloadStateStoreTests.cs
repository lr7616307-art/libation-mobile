using Libation.Mobile.Core;
using Xunit;

namespace Libation.Mobile.Core.Tests;

public sealed class JsonWidevineOfflineDownloadStateStoreTests
{
    [Fact]
    public async Task MarkDownloading_PersistsOnlySafeRecoveryMetadata()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "widevine-state.json");
        var store = new JsonWidevineOfflineDownloadStateStore(path);

        await store.MarkDownloadingAsync("B012345678", "Example audiobook");
        await store.MarkProgressAsync("B012345678", 512, 1024);

        var restored = await new JsonWidevineOfflineDownloadStateStore(path).GetAsync();

        Assert.NotNull(restored);
        Assert.Equal("B012345678", restored.Asin);
        Assert.Equal("Example audiobook", restored.Title);
        Assert.Equal(WidevineOfflineDownloadStatus.Downloading, restored.Status);
        Assert.Equal(512, restored.BytesDownloaded);
        Assert.Equal(1024, restored.ContentLength);
        Assert.Equal(50d, restored.Percentage);
        var json = await File.ReadAllTextAsync(path);
        Assert.DoesNotContain("https://", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("license", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MarkPaused_PersistsStateButDoesNotRequestAutomaticRecovery()
    {
        using var directory = new TemporaryDirectory();
        var store = new JsonWidevineOfflineDownloadStateStore(Path.Combine(directory.Path, "state.json"));
        await store.MarkDownloadingAsync("B012345678", "Example audiobook");
        await store.MarkProgressAsync("B012345678", 900, 1800);

        await store.MarkPausedAsync("B012345678");

        var state = await store.GetAsync();
        Assert.NotNull(state);
        Assert.Equal(WidevineOfflineDownloadStatus.Paused, state.Status);
        Assert.True(state.CanResume);
        Assert.False(state.ShouldAutoRecover);
        Assert.Equal(900, state.BytesDownloaded);
    }

    [Fact]
    public async Task ProcessDeathState_IsMarkedForAutomaticRecovery()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "state.json");
        await new JsonWidevineOfflineDownloadStateStore(path)
            .MarkDownloadingAsync("B012345678", "Example audiobook");

        var recovered = await new JsonWidevineOfflineDownloadStateStore(path).GetAsync();

        Assert.NotNull(recovered);
        Assert.True(recovered.ShouldAutoRecover);
    }

    [Fact]
    public async Task CompletedAndFailedStatesAreNotAutomaticallyRestarted()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "state.json");
        var store = new JsonWidevineOfflineDownloadStateStore(path);
        await store.MarkDownloadingAsync("B012345678", "Example audiobook");
        await store.MarkFailedAsync("B012345678", "Audible authorization unavailable.");

        var failed = await store.GetAsync();
        Assert.Equal(WidevineOfflineDownloadStatus.Failed, failed!.Status);
        Assert.False(failed.ShouldAutoRecover);

        await store.MarkDownloadingAsync("B012345678", "Example audiobook");
        await store.MarkCompletedAsync("B012345678");
        var completed = await store.GetAsync();
        Assert.Equal(WidevineOfflineDownloadStatus.Completed, completed!.Status);
        Assert.False(completed.ShouldAutoRecover);
    }

    [Fact]
    public async Task SwitchingTitlesDoesNotCarryProgressIntoNewDownload()
    {
        using var directory = new TemporaryDirectory();
        var store = new JsonWidevineOfflineDownloadStateStore(Path.Combine(directory.Path, "state.json"));
        await store.MarkDownloadingAsync("B012345678", "First title");
        await store.MarkProgressAsync("B012345678", 700, 1400);

        await store.MarkDownloadingAsync("B087654321", "Second title");

        var state = await store.GetAsync();
        Assert.Equal("B087654321", state!.Asin);
        Assert.Equal(0, state.BytesDownloaded);
        Assert.Null(state.ContentLength);
    }

    [Fact]
    public async Task ProgressIsMonotonicAndBoundedByKnownLength()
    {
        using var directory = new TemporaryDirectory();
        var store = new JsonWidevineOfflineDownloadStateStore(Path.Combine(directory.Path, "state.json"));
        await store.MarkDownloadingAsync("B012345678", "Example audiobook");
        await store.MarkProgressAsync("B012345678", 800, 1000);
        await store.MarkProgressAsync("B012345678", 300, 500);
        await store.MarkProgressAsync("B012345678", 5000, 1000);

        var state = await store.GetAsync();
        Assert.Equal(1000, state!.BytesDownloaded);
        Assert.Equal(1000, state.ContentLength);
    }

    [Fact]
    public async Task InvalidIdentityIsRejected()
    {
        using var directory = new TemporaryDirectory();
        var store = new JsonWidevineOfflineDownloadStateStore(Path.Combine(directory.Path, "state.json"));

        await Assert.ThrowsAsync<ArgumentException>(
            () => store.MarkDownloadingAsync("not-an-asin", "Example audiobook"));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "LibationMobileWidevineRecoveryTests", Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
