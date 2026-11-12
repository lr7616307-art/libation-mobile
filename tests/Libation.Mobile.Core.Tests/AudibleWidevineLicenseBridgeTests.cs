using AudibleApi;
using AudibleApi.Common;
using Libation.Mobile.Audible;
using Xunit;

namespace Libation.Mobile.Core.Tests;

public sealed class AudibleWidevineLicenseBridgeTests
{
    [Fact]
    public async Task RequestWidevineLicenseAsync_ForwardsExactPlatformChallengeToProvider()
    {
        const string asin = "B012345678";
        const string challenge = "platform-generated-widevine-challenge";
        const string response = "provider-widevine-license-response";
        string? observedAsin = null;
        string? observedChallenge = null;

        var source = CreateSource((requestedAsin, requestedChallenge, _) =>
        {
            observedAsin = requestedAsin;
            observedChallenge = requestedChallenge;
            return Task.FromResult(response);
        });

        var actual = await source.RequestWidevineLicenseAsync(asin, challenge);

        Assert.Equal(response, actual);
        Assert.Equal(asin, observedAsin);
        Assert.Equal(challenge, observedChallenge);
    }

    [Fact]
    public async Task RequestWidevineLicenseBytesAsync_Base64TransportsOpaquePlatformMessages()
    {
        const string asin = "B012345678";
        byte[] challenge = [0x00, 0x01, 0x7F, 0x80, 0xFE, 0xFF];
        byte[] license = [0x10, 0x20, 0x30, 0x80, 0xFE];
        string? observedChallenge = null;

        var source = CreateSource((requestedAsin, requestedChallenge, _) =>
        {
            Assert.Equal(asin, requestedAsin);
            observedChallenge = requestedChallenge;
            return Task.FromResult(Convert.ToBase64String(license));
        });

        var actual = await source.RequestWidevineLicenseBytesAsync(asin, challenge);

        Assert.Equal(Convert.ToBase64String(challenge), observedChallenge);
        Assert.Equal(license, actual);
    }

    [Fact]
    public async Task RequestWidevineLicenseAsync_RejectsInvalidAsinBeforeCallingProvider()
    {
        var called = false;
        var source = CreateSource((_, _, _) =>
        {
            called = true;
            return Task.FromResult("license");
        });

        await Assert.ThrowsAsync<ArgumentException>(() =>
            source.RequestWidevineLicenseAsync("not-an-asin", "challenge"));

        Assert.False(called);
    }

    [Fact]
    public async Task RequestWidevineLicenseAsync_RejectsEmptyChallengeBeforeCallingProvider()
    {
        var called = false;
        var source = CreateSource((_, _, _) =>
        {
            called = true;
            return Task.FromResult("license");
        });

        await Assert.ThrowsAsync<ArgumentException>(() =>
            source.RequestWidevineLicenseAsync("B012345678", " "));

        Assert.False(called);
    }

    [Fact]
    public async Task RequestWidevineLicenseAsync_RejectsEmptyProviderResponse()
    {
        var source = CreateSource((_, _, _) => Task.FromResult(" "));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            source.RequestWidevineLicenseAsync("B012345678", "challenge"));
    }

    [Fact]
    public async Task RequestWidevineLicenseAsync_ObservesCancellationBeforeProviderCall()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var called = false;
        var source = CreateSource((_, _, _) =>
        {
            called = true;
            return Task.FromResult("license");
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            source.RequestWidevineLicenseAsync("B012345678", "challenge", cancellation.Token));

        Assert.False(called);
    }

    [Fact]
    public async Task ResolveWidevineAsync_ReturnsAudibleDashManifestOnlyWhenWidevineWasGranted()
    {
        const string manifest = "https://media.example.invalid/book/manifest.mpd";
        var source = new AudibleAudioDownloadSource(
            (_, _, _, _) => Task.FromResult(new ContentLicense
            {
                StatusCode = "Granted",
                DrmType = DrmType.Widevine,
                LicenseResponse = manifest,
                ContentMetadata = new ContentMetadata
                {
                    ChapterInfo = new ChapterInfo(),
                    ContentReference = new ContentReference
                    {
                        Acr = "CR!renewal-reference",
                        Asin = "B012345678",
                        Codec = "mp4a.40.2",
                        ContentFormat = "DASH",
                        FileVersion = 7,
                        Marketplace = "AN7V1F1VY261K",
                        Sku = "AUDIBLE",
                        Tempo = "normal",
                        Version = "1"
                    }
                }
            }),
            (_, _, _) => Task.FromResult("license-response"),
            Path.Combine(Path.GetTempPath(), "vorath-widevine-tests"));

        var result = await source.ResolveWidevineAsync("B012345678");

        Assert.Equal(new Uri(manifest), result.ManifestUri);
        Assert.Equal("CR!renewal-reference", result.Acr);
        Assert.Equal(7, result.FileVersion);
    }

    [Fact]
    public async Task ResolveWidevineAsync_RejectsAudibleAdrmFallback()
    {
        var source = new AudibleAudioDownloadSource(
            (_, _, _, _) => Task.FromResult(new ContentLicense
            {
                StatusCode = "Granted",
                DrmType = DrmType.Adrm,
                LicenseResponse = "https://media.example.invalid/book/manifest.mpd"
            }),
            (_, _, _) => Task.FromResult("license-response"),
            Path.Combine(Path.GetTempPath(), "vorath-widevine-tests"));

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            source.ResolveWidevineAsync("B012345678"));
    }

    [Fact]
    public async Task ResolveWidevineAsync_RejectsNonHttpsManifest()
    {
        var source = new AudibleAudioDownloadSource(
            (_, _, _, _) => Task.FromResult(new ContentLicense
            {
                StatusCode = "Granted",
                DrmType = DrmType.Widevine,
                LicenseResponse = "http://media.example.invalid/book/manifest.mpd"
            }),
            (_, _, _) => Task.FromResult("license-response"),
            Path.Combine(Path.GetTempPath(), "vorath-widevine-tests"));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            source.ResolveWidevineAsync("B012345678"));
    }

    private static AudibleAudioDownloadSource CreateSource(
        Func<string, string, CancellationToken, Task<string>> widevineRequest)
        => new(
            (_, _, _, _) => Task.FromException<ContentLicense>(
                new InvalidOperationException("Content licence delegate is not used by this test.")),
            widevineRequest,
            Path.Combine(Path.GetTempPath(), "vorath-widevine-tests"));
}
