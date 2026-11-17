using Libation.Mobile.Core;
using Xunit;

namespace Libation.Mobile.Core.Tests;

public sealed class AudiblePlayableActionTests
{
    private static Audiobook CreateBook() => new(
        "provider:1234567890",
        "Example audiobook",
        "Example author",
        "Example narrator",
        "1h",
        "Not started");

    [Fact]
    public void UncachedAudibleTitle_OffersAuthorizedOfflinePlaybackDownload()
    {
        var book = CreateBook();

        Assert.Equal("⇩  Download & play offline", book.DownloadActionLabel);
    }

    [Fact]
    public void PreviouslySavedAaxBackup_DoesNotBlockAuthorizedOfflinePlaybackAttempt()
    {
        var book = CreateBook() with
        {
            ProtectedAudioFileName = "Example audiobook.aax",
            ProtectedAudioFolderName = "Downloads",
            ProtectedAudioProtection = "AudibleAdrm"
        };

        Assert.Equal("⇩  Download & play offline", book.DownloadActionLabel);
        Assert.Contains("not playable in a normal audio app", book.AudibleAvailabilityLabel,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void VerifiedWidevineOfflineTitle_OffersInAppPlayback()
    {
        var book = CreateBook() with { IsWidevineOfflineAvailable = true };

        Assert.Equal("▶  Play offline", book.DownloadActionLabel);
        Assert.Contains("authorized playback", book.AudibleAvailabilityLabel,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExistingUnprotectedAudio_StillOffersLocalPlayback()
    {
        var book = CreateBook() with { AudioFilePath = "/private/example.m4b" };

        Assert.Equal("▶  Play", book.DownloadActionLabel);
    }
}
