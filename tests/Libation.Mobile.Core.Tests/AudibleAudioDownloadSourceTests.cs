using AudibleApi.Common;
using Libation.Mobile.Audible;
using Libation.Mobile.Core;
using Xunit;

namespace Libation.Mobile.Core.Tests;

public sealed class AudibleAudioDownloadSourceTests
{
    [Theory]
    [InlineData(DrmType.Mpeg, AudioProtectionKind.Unprotected, ".mp3")]
    [InlineData(DrmType.Adrm, AudioProtectionKind.AudibleAdrm, ".aaxc")]
    [InlineData(DrmType.Widevine, AudioProtectionKind.Widevine, ".protected")]
    [InlineData((DrmType)999, AudioProtectionKind.UnknownProtected, ".protected")]
    public async Task ResolveAsync_MapsGrantedLicenceAndUsesProviderUrl(
        DrmType drmType, AudioProtectionKind expectedProtection, string expectedExtension)
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
            Assert.Equal(Path.Combine(directory, "B012345678" + expectedExtension), result.DestinationPath);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task ResolveAsync_PreservesNonSecretLicenceLifecycleMetadataInUtc()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var expires = new DateTimeOffset(2027, 1, 2, 3, 4, 5, TimeSpan.FromHours(2));
            var refresh = expires.AddDays(-2);
            var removal = expires.AddDays(30);
            var license = CreateLicense(DrmType.Adrm, "https://cdn.example/audio");
            license.AccessExpiryDate = expires;
            license.RefreshDate = refresh;
            license.RemovalDate = removal;
            license.ContentMetadata!.ContentReference.ContentFormat = "AAXC";

            var source = new AudibleAudioDownloadSource((_, _) => Task.FromResult(license), directory);
            var result = await source.ResolveAsync("B012345678");

            Assert.Equal(expires.ToUniversalTime(), result.Protection.LicenceExpiresAtUtc);
            Assert.Equal(refresh.ToUniversalTime(), result.Protection.LicenceRefreshAtUtc);
            Assert.Equal(removal.ToUniversalTime(), result.Protection.LicenceRemovalAtUtc);
            Assert.Equal("AAXC", result.Protection.ContentType);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task ResolveAsync_LeavesMissingLifecycleDatesNull()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var source = new AudibleAudioDownloadSource(
                (_, _) => Task.FromResult(CreateLicense(DrmType.Adrm, "https://cdn.example/audio")),
                directory);
            var result = await source.ResolveAsync("B012345678");

            Assert.Null(result.Protection.LicenceExpiresAtUtc);
            Assert.Null(result.Protection.LicenceRefreshAtUtc);
            Assert.Null(result.Protection.LicenceRemovalAtUtc);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task ResolveAsync_RejectsGrantedLicenceWithoutContentMetadata()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var license = CreateLicense(DrmType.Adrm, "https://cdn.example/audio");
            license.ContentMetadata = null;
            var source = new AudibleAudioDownloadSource((_, _) => Task.FromResult(license), directory);

            await Assert.ThrowsAsync<InvalidDataException>(() => source.ResolveAsync("B012345678"));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task ResolveAsync_RejectsGrantedLicenceWithoutContentReference()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var license = CreateLicense(DrmType.Adrm, "https://cdn.example/audio");
            license.ContentMetadata!.ContentReference = null!;
            var source = new AudibleAudioDownloadSource((_, _) => Task.FromResult(license), directory);

            await Assert.ThrowsAsync<InvalidDataException>(() => source.ResolveAsync("B012345678"));
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
            ChapterInfo = new ChapterInfo(),
            ContentReference = new ContentReference
            {
                Acr = "",
                Asin = "B012345678",
                Codec = "",
                ContentFormat = "",
                Marketplace = "",
                Sku = "",
                Tempo = "",
                Version = ""
            },
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
