using AudibleApi.Common;
using Libation.Mobile.Audible;
using Libation.Mobile.Core;

namespace Libation.Mobile.Core.Tests;

public sealed class AudibleAudioDownloadSourceTests
{
    [Theory]
    [InlineData(DrmType.Mpeg, AudioProtectionKind.Unprotected)]
    [InlineData(DrmType.Adrm, AudioProtectionKind.AudibleAdrm)]
    [InlineData(DrmType.Widevine, AudioProtectionKind.Widevine)]
    public async Task ResolveAsync_MapsGrantedLicenceAndUsesProviderUrl(
        DrmType drmType, AudioProtectionKind expectedProtection)
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var source = new AudibleAudioDownloadSource(
                (_, _) => Task.FromResult(CreateLicense(drmType, "https://cdn.example/audio")),
                directory);
            var result = await source.ResolveAsync("B012345678");

            Assert.Equal(new Uri("https://cdn.example/audio"), result.DownloadUri);
            Assert.Equal(expectedProtection, result.Protection.Kind);
            Assert.Equal(Path.Combine(directory, "B012345678.aaxc"), result.DestinationPath);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData("http://cdn.example/audio")]
    [InlineData("file:///private/audio")]
    [InlineData("")]
    public async Task ResolveAsync_RejectsNonHttpsOrMissingOfflineUrl(string offlineUrl)
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var source = new AudibleAudioDownloadSource(
                (_, _) => Task.FromResult(CreateLicense(DrmType.Mpeg, offlineUrl)), directory);
            await Assert.ThrowsAsync<InvalidDataException>(() => source.ResolveAsync("B012345678"));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task ResolveAsync_RejectsDeniedLicence()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var license = CreateLicense(DrmType.Adrm, "https://cdn.example/audio");
            license.StatusCode = "Denied";
            var source = new AudibleAudioDownloadSource((_, _) => Task.FromResult(license), directory);
            await Assert.ThrowsAsync<InvalidOperationException>(() => source.ResolveAsync("B012345678"));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData("")]
    [InlineData("../B012345678")]
    [InlineData("short")]
    [InlineData("B012345678?query=secret")]
    public async Task ResolveAsync_RejectsInvalidAsinBeforeCallingProvider(string asin)
    {
        var directory = CreateTemporaryDirectory();
        var called = false;
        try
        {
            var source = new AudibleAudioDownloadSource((_, _) =>
            {
                called = true;
                return Task.FromResult(CreateLicense(DrmType.Mpeg, "https://cdn.example/audio"));
            }, directory);
            await Assert.ThrowsAsync<ArgumentException>(() => source.ResolveAsync(asin));
            Assert.False(called);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static ContentLicense CreateLicense(DrmType drmType, string offlineUrl) => new()
    {
        StatusCode = "Granted",
        DrmType = drmType,
        ContentMetadata = new ContentMetadata
        {
            ContentUrl = new ContentUrl { OfflineUrl = offlineUrl }
        }
    };

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "audible-source-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
