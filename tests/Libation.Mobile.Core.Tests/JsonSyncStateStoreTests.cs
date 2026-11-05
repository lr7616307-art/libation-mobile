using Libation.Mobile.Core;
using Xunit;

namespace Libation.Mobile.Core.Tests;

public sealed class JsonSyncStateStoreTests
{
    [Fact]
    public async Task LastSuccessfulSync_IsPersistedAndRestored()
    {
        var directory = Path.Combine(Path.GetTempPath(), "vorath-sync-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "sync-state.json");
            var timestamp = new DateTimeOffset(2026, 11, 5, 12, 30, 0, TimeSpan.Zero);
            await new JsonSyncStateStore(path).SaveLastSuccessfulSyncAsync(timestamp);

            var restored = await new JsonSyncStateStore(path).GetLastSuccessfulSyncAsync();

            Assert.Equal(timestamp, restored);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task MissingSyncState_ReturnsNull()
    {
        var path = Path.Combine(Path.GetTempPath(), "vorath-sync-" + Guid.NewGuid().ToString("N"), "state.json");
        Assert.Null(await new JsonSyncStateStore(path).GetLastSuccessfulSyncAsync());
    }
}
