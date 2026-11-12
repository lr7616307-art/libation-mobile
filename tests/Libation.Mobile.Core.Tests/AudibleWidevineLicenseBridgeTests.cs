using AudibleApi;
using AudibleApi.Common;
using Libation.Mobile.Audible;

namespace Libation.Mobile.Core.Tests;

public sealed class AudibleWidevineLicenseBridgeTests
{
    [Fact]
    public async Task RequestWidevineLicenseAsync_ForwardsExactPlatformChallengeWithoutLoggingOrPersistingIt()
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

    private static AudibleAudioDownloadSource CreateSource(
        Func<string, string, CancellationToken, Task<string>> widevineRequest)
        => new(
            (_, _, _, _) => Task.FromException<ContentLicense>(
                new InvalidOperationException("Content licence delegate is not used by this test.")),
            widevineRequest,
            Path.Combine(Path.GetTempPath(), "vorath-widevine-tests"));
}
