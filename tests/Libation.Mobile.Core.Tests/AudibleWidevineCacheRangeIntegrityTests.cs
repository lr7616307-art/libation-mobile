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
}
