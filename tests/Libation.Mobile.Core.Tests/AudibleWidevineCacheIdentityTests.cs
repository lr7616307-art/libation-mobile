using Libation.Mobile.Audible;
using Xunit;

namespace Libation.Mobile.Core.Tests;

public sealed class AudibleWidevineCacheIdentityTests
{
    private const string Acr = "CR!audible-content-reference";

    [Fact]
    public void CreateResourceFingerprint_ReusesResourceAcrossRotatingSignedUrls()
    {
        var contentVersion = AudibleWidevineCacheIdentity.CreateContentVersionKey(Acr, 17);
        var first = new Uri("https://cdn.example.invalid/audio/segment.m4s?track=audio&X-Amz-Signature=one&X-Amz-Expires=60");
        var refreshed = new Uri("https://cdn.example.invalid/audio/segment.m4s?X-Amz-Expires=300&track=audio&X-Amz-Signature=two");

        Assert.Equal(
            AudibleWidevineCacheIdentity.CreateResourceFingerprint(first, contentVersion),
            AudibleWidevineCacheIdentity.CreateResourceFingerprint(refreshed, contentVersion));
    }

    [Fact]
    public void CreateResourceFingerprint_PreservesStableQueryParameters()
    {
        var contentVersion = AudibleWidevineCacheIdentity.CreateContentVersionKey(Acr, 17);
        var first = new Uri("https://cdn.example.invalid/audio/segment.m4s?track=audio&token=one");
        var differentTrack = new Uri("https://cdn.example.invalid/audio/segment.m4s?track=video&token=two");

        Assert.NotEqual(
            AudibleWidevineCacheIdentity.CreateResourceFingerprint(first, contentVersion),
            AudibleWidevineCacheIdentity.CreateResourceFingerprint(differentTrack, contentVersion));
    }

    [Fact]
    public void CreateResourceFingerprint_DoesNotMixDifferentContentVersions()
    {
        var uri = new Uri("https://cdn.example.invalid/audio/segment.m4s?token=rotating");
        var firstVersion = AudibleWidevineCacheIdentity.CreateContentVersionKey(Acr, 17);
        var nextVersion = AudibleWidevineCacheIdentity.CreateContentVersionKey(Acr, 18);
        var otherContent = AudibleWidevineCacheIdentity.CreateContentVersionKey("CR!different-content", 17);

        Assert.NotEqual(
            AudibleWidevineCacheIdentity.CreateResourceFingerprint(uri, firstVersion),
            AudibleWidevineCacheIdentity.CreateResourceFingerprint(uri, nextVersion));
        Assert.NotEqual(
            AudibleWidevineCacheIdentity.CreateResourceFingerprint(uri, firstVersion),
            AudibleWidevineCacheIdentity.CreateResourceFingerprint(uri, otherContent));
    }

    [Fact]
    public void CreateResourceFingerprint_UnversionedContentKeepsEntireQuery()
    {
        var first = new Uri("https://cdn.example.invalid/audio/segment.m4s?token=one");
        var refreshed = new Uri("https://cdn.example.invalid/audio/segment.m4s?token=two");

        Assert.NotEqual(
            AudibleWidevineCacheIdentity.CreateResourceFingerprint(
                first, AudibleWidevineCacheIdentity.UnversionedContentKey),
            AudibleWidevineCacheIdentity.CreateResourceFingerprint(
                refreshed, AudibleWidevineCacheIdentity.UnversionedContentKey));
    }

    [Fact]
    public void CreateContentVersionKey_RequiresACompleteVersionPair()
    {
        Assert.Throws<ArgumentException>(() =>
            AudibleWidevineCacheIdentity.CreateContentVersionKey(Acr, null));
        Assert.Throws<ArgumentException>(() =>
            AudibleWidevineCacheIdentity.CreateContentVersionKey(null, 17));
        Assert.Throws<ArgumentException>(() =>
            AudibleWidevineCacheIdentity.CreateContentVersionKey(Acr, -1));
    }

    [Theory]
    [InlineData("u")]
    [InlineData("v-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void IsValidContentVersionKey_AcceptsOnlyTheDefinedShapes(string value)
        => Assert.True(AudibleWidevineCacheIdentity.IsValidContentVersionKey(value));

    [Theory]
    [InlineData("")]
    [InlineData("v-ABCDEF")]
    [InlineData("v-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("version-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void IsValidContentVersionKey_RejectsMalformedValues(string value)
        => Assert.False(AudibleWidevineCacheIdentity.IsValidContentVersionKey(value));

    [Theory]
    [InlineData("http://cdn.example.invalid/segment.m4s")]
    [InlineData("https://user:pass@cdn.example.invalid/segment.m4s")]
    public void NormalizeResourceUri_RejectsUnsafeUris(string value)
        => Assert.Throws<ArgumentException>(() =>
            AudibleWidevineCacheIdentity.NormalizeResourceUri(new Uri(value), true));
}
