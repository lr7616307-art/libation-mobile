using Libation.Mobile.Core;
using Xunit;

namespace Libation.Mobile.Core.Tests;

public sealed class AudibleDownloadDisplayStateTests
{
    [Fact]
    public void ProtectedAaxBackup_IsNotLabelledAsPlayableOrAsWidevineDash()
    {
        var book = new Audiobook(
            "provider:1234567890",
            "Example audiobook",
            "Example author",
            "Example narrator",
            "1h",
            "Not started")
        {
            ProtectedAudioFileName = "Example audiobook.aax",
            ProtectedAudioFolderName = "Downloads",
            ProtectedAudioProtection = "AudibleAdrm"
        };

        Assert.Equal("PROTECTED BACKUP ONLY", book.AudibleAvailabilityShortLabel);
        Assert.Contains("not a normal playable audio file", book.AudibleAvailabilityLabel,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Protected backup saved", book.DownloadActionLabel);
        Assert.DoesNotContain("Widevine", book.AudibleAvailabilityLabel, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WidevineOfflineState_ReportsProtectedAuthorizedPlaybackNotPlainExport()
    {
        var book = new Audiobook(
            "provider:1234567890",
            "Example audiobook",
            "Example author",
            "Example narrator",
            "1h",
            "Not started")
        {
            IsWidevineOfflineAvailable = true
        };

        Assert.Equal("OFFLINE PLAYBACK READY", book.AudibleAvailabilityShortLabel);
        Assert.Contains("authorized playback", book.AudibleAvailabilityLabel,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnprotectedLocalAudio_IsTheOnlyStateThatReportsReadyToPlay()
    {
        var book = new Audiobook(
            "provider:1234567890",
            "Example audiobook",
            "Example author",
            "Example narrator",
            "1h",
            "Not started")
        {
            AudioFilePath = "/private/example.mp3"
        };

        Assert.Equal("READY TO PLAY", book.AudibleAvailabilityShortLabel);
        Assert.Equal("▶  Play", book.DownloadActionLabel);
    }
}
