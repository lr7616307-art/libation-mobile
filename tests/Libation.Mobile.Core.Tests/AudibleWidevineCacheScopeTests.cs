using Libation.Mobile.Audible;
using Xunit;

namespace Libation.Mobile.Core.Tests;

public sealed class AudibleWidevineCacheScopeTests
{
    private const string TitleScope = "vorath-v1-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public void CreateCacheScope_ProducesDistinctBoundedScopesPerContentVersion()
    {
        var firstVersion = AudibleWidevineCacheIdentity.CreateContentVersionKey(
            "CR!book-version", 1);
        var secondVersion = AudibleWidevineCacheIdentity.CreateContentVersionKey(
            "CR!book-version", 2);

        var firstScope = AudibleWidevineCacheIdentity.CreateCacheScope(TitleScope, firstVersion);
        var secondScope = AudibleWidevineCacheIdentity.CreateCacheScope(TitleScope, secondVersion);

        Assert.NotEqual(firstScope, secondScope);
        Assert.StartsWith(TitleScope + "-", firstScope, StringComparison.Ordinal);
        Assert.StartsWith(TitleScope + "-", secondScope, StringComparison.Ordinal);
        Assert.InRange(firstScope.Length, 1, 128);
        Assert.InRange(secondScope.Length, 1, 128);
        Assert.DoesNotContain(":", firstScope, StringComparison.Ordinal);
        Assert.DoesNotContain(":", secondScope, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateCacheScope_UsesStableUnversionedScopeWhenProviderMetadataIsMissing()
    {
        var scope = AudibleWidevineCacheIdentity.CreateCacheScope(
            TitleScope, AudibleWidevineCacheIdentity.UnversionedContentKey);

        Assert.Equal(TitleScope + "-u", scope);
    }

    [Fact]
    public void CreateCacheScope_RejectsInvalidTitleAndContentScopes()
    {
        var version = AudibleWidevineCacheIdentity.CreateContentVersionKey("CR!book", 1);

        Assert.Throws<ArgumentException>(() =>
            AudibleWidevineCacheIdentity.CreateCacheScope("", version));
        Assert.Throws<ArgumentException>(() =>
            AudibleWidevineCacheIdentity.CreateCacheScope("invalid:scope", version));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            AudibleWidevineCacheIdentity.CreateCacheScope(new string('a', 129), version));
        Assert.Throws<ArgumentException>(() =>
            AudibleWidevineCacheIdentity.CreateCacheScope(TitleScope, "invalid"));
    }

    [Fact]
    public void IsContentCacheKey_MatchesOnlyTheExactVersionScope()
    {
        var firstVersion = AudibleWidevineCacheIdentity.CreateCacheScope(
            TitleScope, AudibleWidevineCacheIdentity.CreateContentVersionKey("CR!book", 1));
        var secondVersion = AudibleWidevineCacheIdentity.CreateCacheScope(
            TitleScope, AudibleWidevineCacheIdentity.CreateContentVersionKey("CR!book", 2));

        Assert.True(AudibleWidevineCacheIdentity.IsContentCacheKey(
            firstVersion, firstVersion + ":segment:abc"));
        Assert.False(AudibleWidevineCacheIdentity.IsContentCacheKey(
            firstVersion, secondVersion + ":segment:abc"));
        Assert.False(AudibleWidevineCacheIdentity.IsContentCacheKey(
            firstVersion, firstVersion + "x:segment:abc"));
        Assert.False(AudibleWidevineCacheIdentity.IsContentCacheKey(firstVersion, ""));
    }

    [Fact]
    public void IsTitleCacheScope_MatchesOnlyExactTitlePrefixAndVersionDelimiter()
    {
        var first = AudibleWidevineCacheIdentity.CreateCacheScope(
            TitleScope, AudibleWidevineCacheIdentity.CreateContentVersionKey("CR!book", 1));
        var otherTitle = "vorath-v1-bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb-u";

        Assert.True(AudibleWidevineCacheIdentity.IsTitleCacheScope(TitleScope, TitleScope));
        Assert.True(AudibleWidevineCacheIdentity.IsTitleCacheScope(TitleScope, first));
        Assert.False(AudibleWidevineCacheIdentity.IsTitleCacheScope(TitleScope, TitleScope + "b-u"));
        Assert.False(AudibleWidevineCacheIdentity.IsTitleCacheScope(TitleScope, otherTitle));
        Assert.False(AudibleWidevineCacheIdentity.IsTitleCacheScope(TitleScope, ""));
    }
}
