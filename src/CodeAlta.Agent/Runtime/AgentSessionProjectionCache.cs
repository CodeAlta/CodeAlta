namespace CodeAlta.Agent.Runtime;

/// <summary>
/// Provides durable cache operations for session-listing projections derived from JSONL session journals.
/// </summary>
public interface IAgentSessionProjectionCache
{
    /// <summary>
    /// Lists cached session projections, progressively rebuilding from journals when the cache is missing or recoverably corrupt.
    /// </summary>
    /// <param name="context">Projection refresh context.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Cached session projections ordered by most recent update first.</returns>
    /// <exception cref="AgentSessionCacheLockedException">Thrown when the cache database is locked.</exception>
    IAsyncEnumerable<AgentSessionCacheProjection> ListSessionsAsync(
        AgentSessionCacheProjectionContext context,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a cached session projection by session identifier when one has been materialized.
    /// </summary>
    /// <param name="sessionId">Session identifier.</param>
    /// <param name="context">Projection refresh context.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The projection when present; otherwise <see langword="null" />.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="sessionId" /> is empty.</exception>
    /// <exception cref="AgentSessionCacheLockedException">Thrown when the cache database is locked.</exception>
    Task<AgentSessionCacheProjection?> GetSessionAsync(
        string sessionId,
        AgentSessionCacheProjectionContext context,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Upserts a projection after the authoritative journal file has been written.
    /// </summary>
    /// <param name="projection">Projection to persist.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="projection" /> is <see langword="null" />.</exception>
    /// <exception cref="AgentSessionCacheLockedException">Thrown when the cache database is locked.</exception>
    Task UpsertSessionAsync(
        AgentSessionCacheProjection projection,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a cached projection by session identifier.
    /// </summary>
    /// <param name="sessionId">Session identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="sessionId" /> is empty.</exception>
    /// <exception cref="AgentSessionCacheLockedException">Thrown when the cache database is locked.</exception>
    Task RemoveSessionAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reconciles cached projections with external journal additions, changes, and deletions.
    /// </summary>
    /// <param name="context">Projection refresh context.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Reconciliation statistics.</returns>
    /// <exception cref="AgentSessionCacheLockedException">Thrown when the cache database is locked.</exception>
    Task<AgentSessionCacheReconciliationResult> ReconcileAsync(
        AgentSessionCacheProjectionContext context,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Describes services needed to project JSONL session journals into cache rows.
/// </summary>
public sealed class AgentSessionCacheProjectionContext
{
    /// <summary>
    /// Initializes a new instance of the <see cref="AgentSessionCacheProjectionContext" /> class.
    /// </summary>
    /// <param name="sessionsRootPath">Root directory that contains session journals.</param>
    /// <param name="projectSessionFileAsync">Function that projects one journal file into a cache projection.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="sessionsRootPath" /> is empty.</exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="projectSessionFileAsync" /> is <see langword="null" />.</exception>
    public AgentSessionCacheProjectionContext(
        string sessionsRootPath,
        Func<string, CancellationToken, Task<AgentSessionCacheProjection?>> projectSessionFileAsync)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionsRootPath);
        ArgumentNullException.ThrowIfNull(projectSessionFileAsync);

        SessionsRootPath = sessionsRootPath;
        ProjectSessionFileAsync = projectSessionFileAsync;
    }

    /// <summary>
    /// Gets the root directory that contains session journals.
    /// </summary>
    public string SessionsRootPath { get; }

    /// <summary>
    /// Gets the function that projects one journal file into a cache projection.
    /// </summary>
    public Func<string, CancellationToken, Task<AgentSessionCacheProjection?>> ProjectSessionFileAsync { get; }
}

/// <summary>
/// Describes one cacheable session projection derived from a JSONL journal.
/// </summary>
/// <param name="JournalPath">Absolute path to the authoritative session journal.</param>
/// <param name="Stamp">Journal file stamp observed while projecting.</param>
/// <param name="Summary">Projected session summary.</param>
/// <param name="State">Projected session state.</param>
/// <param name="ViewState">Optional projected session-view state. An empty instance represents a cache hit with no view-state values.</param>
public sealed record AgentSessionCacheProjection(
    string JournalPath,
    AgentSessionCacheFileStamp Stamp,
    AgentSessionSummary Summary,
    AgentSessionState? State,
    AgentSessionViewStateMetadata? ViewState = null);

/// <summary>
/// Describes a journal file stamp used to validate cached projections.
/// </summary>
/// <param name="LastWriteTimeUtc">Journal last-write time in UTC.</param>
/// <param name="Length">Journal length in bytes.</param>
public readonly record struct AgentSessionCacheFileStamp(DateTime LastWriteTimeUtc, long Length);

/// <summary>
/// Describes the result of reconciling cached session projections with journals on disk.
/// </summary>
/// <param name="Changed">Whether visible cached metadata changed.</param>
/// <param name="Upserted">Number of cache rows inserted or updated.</param>
/// <param name="Pruned">Number of cache rows removed.</param>
public sealed record AgentSessionCacheReconciliationResult(bool Changed, int Upserted, int Pruned);

/// <summary>
/// The exception thrown when a local session cache database is busy or locked.
/// </summary>
public sealed class AgentSessionCacheLockedException : InvalidOperationException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="AgentSessionCacheLockedException" /> class.
    /// </summary>
    public AgentSessionCacheLockedException()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="AgentSessionCacheLockedException" /> class.
    /// </summary>
    /// <param name="message">Error message.</param>
    public AgentSessionCacheLockedException(string? message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="AgentSessionCacheLockedException" /> class.
    /// </summary>
    /// <param name="message">Error message.</param>
    /// <param name="innerException">Inner exception.</param>
    public AgentSessionCacheLockedException(string? message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
