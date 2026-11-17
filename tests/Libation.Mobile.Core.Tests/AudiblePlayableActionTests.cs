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
    public void UncachedAudibleTitle_OffersProtectedDownloadActionAndCompactIcon()
    {
        var book = CreateBook();

        Assert.Equal("⇩", book.DownloadActionIcon);
        Assert.Equal("Download protected Audible audio", book.DownloadActionLabel);
    }

    [Fact]
    public void PreviouslySavedAaxBackup_IsShownAsSavedAndNotPlayable()
    {
        var book = CreateBook() with
        {
            ProtectedAudioFileName = "Example audiobook.aax",
            ProtectedAudioFolderName = "Downloads",
            ProtectedAudioProtection = "AudibleAdrm"
        };

        Assert.Equal("✓", book.DownloadActionIcon);
        Assert.Equal("Protected backup already saved", book.DownloadActionLabel);
        Assert.Contains("not playable in a normal audio app", book.AudibleAvailabilityLabel,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExistingWidevineOfflineCache_IsProtectedButPlaybackIsDisabled()
    {
        var book = CreateBook() with { IsWidevineOfflineAvailable = true };

        Assert.Equal("⇩", book.DownloadActionIcon);
        Assert.Equal("Download protected Audible audio", book.DownloadActionLabel);
        Assert.Contains("playback is currently disabled", book.AudibleAvailabilityLabel,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExistingUnprotectedAudio_StillOffersLocalPlayback()
    {
        var book = CreateBook() with { AudioFilePath = "/private/example.m4b" };

        Assert.Equal("▶  Play", book.DownloadActionLabel);
    }
}
