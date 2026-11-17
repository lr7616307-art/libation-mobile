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
    public void AudibleDownloadState_DoesNotReplaceAvailabilityWithDownloadedStatus()
    {
        var book = CreateBook() with
        {
            IsPlayableOnAudible = true,
            IsDownloadedOnAudible = true
        };

        Assert.Equal("AVAILABLE", book.AudibleAvailabilityShortLabel);
    }

    [Fact]
    public void AudibleAvailability_IsShownAsAvailableOrUnavailableOnly()
    {
        var available = CreateBook() with { IsPlayableOnAudible = true };
        var unavailable = CreateBook() with { IsPlayableOnAudible = false };

        Assert.Equal("AVAILABLE", available.AudibleAvailabilityShortLabel);
        Assert.Equal("UNAVAILABLE", unavailable.AudibleAvailabilityShortLabel);
    }

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
