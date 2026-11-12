using Libation.Mobile.Audible;
using Xunit;

namespace Libation.Mobile.Core.Tests;

public sealed class AudibleWidevineCacheRangeIntegrityTests
{
    [Fact]
    public void IsComplete_AcceptsFullyCachedKnownRange()
    {
        Assert.True(AudibleWidevineCacheRangeIntegrity.IsComplete(
            requestedPosition: 1024,
            requestedLength: 4096,
            cachedByteCount: 4096,
            knownResourceLength: -1));
    }

    [Theory]
    [InlineData(4095)]
    [InlineData(0)]
    [InlineData(4097)]
    public void IsComplete_RejectsKnownRangeWithIncorrectCachedByteCount(long cachedBytes)
    {
        Assert.False(AudibleWidevineCacheRangeIntegrity.IsComplete(
            requestedPosition: 1024,
            requestedLength: 4096,
            cachedByteCount: cachedBytes,
            knownResourceLength: -1));
    }

    [Fact]
    public void IsComplete_AcceptsUnknownLengthOnlyWhenWholeResourceIsCached()
    {
        Assert.True(AudibleWidevineCacheRangeIntegrity.IsComplete(
            requestedPosition: 0,
            requestedLength: -1,
            cachedByteCount: 8192,
            knownResourceLength: 8192));
    }

    [Theory]
    [InlineData(8191, 8192)]
    [InlineData(0, 8192)]
    [InlineData(8192, -1)]
    [InlineData(1, 0)]
    public void IsComplete_RejectsUnknownLengthWithoutFullKnownCoverage(
        long cachedBytes,
        long knownLength)
    {
        Assert.False(AudibleWidevineCacheRangeIntegrity.IsComplete(
            requestedPosition: 0,
            requestedLength: -1,
            cachedByteCount: cachedBytes,
            knownResourceLength: knownLength));
    }

    [Theory]
    [InlineData(-1, 10, 10, 10)]
    [InlineData(0, -2, 10, 10)]
    [InlineData(0, 10, -1, 10)]
    [InlineData(0, -1, 10, -1)]
    public void IsComplete_RejectsInvalidRangeMetadata(
        long position,
        long length,
        long cachedBytes,
        long contentLength)
    {
        Assert.False(AudibleWidevineCacheRangeIntegrity.IsComplete(
            position, length, cachedBytes, contentLength));
    }
    [Fact]
    public void ReconcileCacheIndex_RemovesStaleKeysAndPreservesOnlyLiveKeysForThisTitle()
    {
        const string scope = "vorath-v1-account-title";
        var result = AudibleWidevineCacheRangeIntegrity.ReconcileCacheIndex(
            scope,
            [
                scope + ":manifest:old",
                scope + ":segment:keep",
                "another-title:segment:keep"
            ],
            [
                scope + ":segment:keep",
                scope + ":manifest:new",
                "another-title:segment:keep"
            ]);

        Assert.Equal(new[] { scope + ":manifest:new", scope + ":segment:keep" },
            result.RetainedKeys.OrderBy(key => key, StringComparer.Ordinal));
        Assert.Equal(new[] { scope + ":manifest:old" }, result.StaleKeys);
        Assert.Equal(new[] { scope + ":manifest:new" }, result.RecoveredOrphanKeys);
    }

    [Fact]
    public void ReconcileCacheIndex_RecoversInterruptedWriteOrphansButIdentifiesThemAsUnindexed()
    {
        const string scope = "vorath-v1-account-title";
        var result = AudibleWidevineCacheRangeIntegrity.ReconcileCacheIndex(
            scope,
            [scope + ":manifest:cached"],
            [
                scope + ":manifest:cached",
                scope + ":segment:written-before-index-crash"
            ]);

        Assert.Contains(scope + ":segment:written-before-index-crash", result.RetainedKeys);
        Assert.Contains(scope + ":segment:written-before-index-crash", result.RecoveredOrphanKeys);
        Assert.Empty(result.StaleKeys);
    }

    [Fact]
    public void ReconcileCacheIndex_RejectsInvalidScopeAndNullInputs()
    {
        Assert.Throws<ArgumentException>(() =>
            AudibleWidevineCacheRangeIntegrity.ReconcileCacheIndex("invalid:scope", [], []));
        Assert.Throws<ArgumentNullException>(() =>
            AudibleWidevineCacheRangeIntegrity.ReconcileCacheIndex("valid-scope", null!, []));
        Assert.Throws<ArgumentNullException>(() =>
            AudibleWidevineCacheRangeIntegrity.ReconcileCacheIndex("valid-scope", [], null!));
    }

}
