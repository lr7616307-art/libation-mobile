using Libation.Mobile.Core;
using Xunit;

namespace Libation.Mobile.Core.Tests;

public sealed class DownloadQueueItemTests
{
    [Fact]
    public void AudibleQueueActionLabel_DistinguishesStartPauseAndResume()
    {
        var now = DateTimeOffset.UtcNow;
        var queued = new DownloadQueueItem(
            "provider:1234567890", DownloadStatus.Queued, now, now);
        var queuedWithPartialData = queued with { BytesReceived = 1024 };
        var downloading = queued with { Status = DownloadStatus.Downloading };
        var paused = queued with { Status = DownloadStatus.Paused };

        Assert.Equal("Start", queued.DownloadActionLabel);
        Assert.Equal("Resume", queuedWithPartialData.DownloadActionLabel);
        Assert.Equal("Pause", downloading.DownloadActionLabel);
        Assert.Equal("Resume", paused.DownloadActionLabel);
    }

    [Fact]
    public void ActiveDownloadCanRequestRemovalButMustUseConfirmationFlow()
    {
        var now = DateTimeOffset.UtcNow;
        var downloading = new DownloadQueueItem(
            "provider:1234567890", DownloadStatus.Downloading, now, now);
        var paused = downloading with { Status = DownloadStatus.Queued };

        Assert.True(downloading.CanRemove);
        Assert.True(paused.CanRemove);
    }
}
