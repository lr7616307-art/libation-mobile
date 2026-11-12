namespace Libation.Mobile.Audible;

/// <summary>
/// Validates Media3 cache coverage for a requested DASH resource range.
/// A bounded range is complete only when every requested byte is cached.
/// Unknown-length resources require a final content length and full coverage.
/// </summary>
public static class AudibleWidevineCacheRangeIntegrity
{
    public static bool IsComplete(
        long requestedPosition,
        long requestedLength,
        long cachedByteCount,
        long knownResourceLength)
    {
        if (requestedPosition < 0 ||
            requestedLength < -1 ||
            cachedByteCount < 0 ||
            knownResourceLength < -1)
            return false;

        if (requestedLength > 0)
            return cachedByteCount == requestedLength;

        return knownResourceLength > 0 &&
            cachedByteCount == knownResourceLength;
    }
}
