namespace Libation.Mobile.Audible;

/// <summary>
/// Describes how an app-private Media3 cache index should be reconciled with
/// the cache's actual live keys after a process interruption.
/// </summary>
public sealed record AudibleWidevineCacheIndexReconciliation(
    IReadOnlySet<string> RetainedKeys,
    IReadOnlySet<string> StaleKeys,
    IReadOnlySet<string> RecoveredOrphanKeys);

/// <summary>
/// Validates Media3 cache coverage and reconciles title-scoped cache indexes.
/// This pure helper is shared with regression tests so interrupted-download
/// recovery decisions do not depend on an Android runtime.
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

    /// <summary>
    /// Keeps only live keys belonging to the requested title. Returns stale
    /// index entries for removal and live-but-unindexed keys for safe recovery.
    /// Orphan keys must be re-observed through a DataSpec before they can count
    /// as complete because their expected byte ranges are not known.
    /// </summary>
    public static AudibleWidevineCacheIndexReconciliation ReconcileCacheIndex(
        string titleScope,
        IEnumerable<string> indexedKeys,
        IEnumerable<string> liveCacheKeys)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(titleScope);
        ArgumentNullException.ThrowIfNull(indexedKeys);
        ArgumentNullException.ThrowIfNull(liveCacheKeys);
        if (titleScope.Length > 128 || titleScope.Contains(':'))
            throw new ArgumentException("The protected cache title scope is invalid.", nameof(titleScope));

        var prefix = titleScope + ":";
        var indexed = indexedKeys
            .Where(key => !string.IsNullOrWhiteSpace(key) && key.StartsWith(prefix, StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);
        var live = liveCacheKeys
            .Where(key => !string.IsNullOrWhiteSpace(key) && key.StartsWith(prefix, StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);

        var stale = indexed.Where(key => !live.Contains(key)).ToHashSet(StringComparer.Ordinal);
        var orphan = live.Where(key => !indexed.Contains(key)).ToHashSet(StringComparer.Ordinal);
        var retained = indexed.Where(live.Contains).Concat(orphan).ToHashSet(StringComparer.Ordinal);

        return new AudibleWidevineCacheIndexReconciliation(retained, stale, orphan);
    }
}
