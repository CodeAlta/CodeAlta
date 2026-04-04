namespace CodeAlta.Search;

/// <summary>
/// Represents the cached snapshot state for a single project root.
/// </summary>
/// <param name="Snapshot">The cached snapshot when available.</param>
/// <param name="IsDirty">Indicates whether the snapshot should be refreshed.</param>
/// <param name="LastInvalidatedAt">Timestamp of the most recent invalidation.</param>
/// <param name="LastInvalidationReason">Reason for the most recent invalidation.</param>
public sealed record ProjectFileSnapshotCacheEntry(
    ProjectFileSnapshot? Snapshot,
    bool IsDirty,
    DateTimeOffset? LastInvalidatedAt,
    ProjectFileInvalidationReason? LastInvalidationReason);
