namespace CodeAlta.Agent.Runtime;

/// <summary>
/// Keeps what normal operation needs to know of the journal of each session without reading it: the positions of its
/// turns, its latest notes and the usage of its requests to the model, as of a length and a last-write time of the
/// journal. The session store writes it after each append (the journal first, then the index) and reads it instead of
/// the journal; a journal that changed otherwise is read once, from where the index ends, and written back.
/// </summary>
/// <remarks>
/// The index is derived from the journals, which stay authoritative: an index that is missing, damaged or of an older
/// format is rebuilt from them, a session at a time, when it is first needed. An index that cannot be used now because
/// its database is locked throws <see cref="AgentSessionCacheLockedException"/>.
/// </remarks>
public interface IAgentSessionJournalIndex
{
    /// <summary>Reads the row of the journal of a session.</summary>
    /// <param name="sessionId">The session: the name of its journal file, without extension.</param>
    /// <param name="cancellationToken">Stops the read.</param>
    /// <returns>The row; null when the index has none for the session.</returns>
    /// <exception cref="ArgumentException"><paramref name="sessionId"/> is blank.</exception>
    /// <exception cref="AgentSessionCacheLockedException">The database is locked.</exception>
    Task<AgentSessionJournalIndexRow?> ReadRowAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>Reads the row of a session and every turn of it, in journal order, from one snapshot of the index.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="cancellationToken">Stops the read.</param>
    /// <returns>The row and its turns; null when the index has no row for the session.</returns>
    /// <exception cref="ArgumentException"><paramref name="sessionId"/> is blank.</exception>
    /// <exception cref="AgentSessionCacheLockedException">The database is locked.</exception>
    Task<AgentSessionJournalIndexTurns?> ReadTurnsAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>Reads the turns of a session that some runs started.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="runIds">The runs.</param>
    /// <param name="cancellationToken">Stops the read.</param>
    /// <returns>The turns the index has of those runs, in no order.</returns>
    /// <exception cref="ArgumentException"><paramref name="sessionId"/> is blank.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="runIds"/> is null.</exception>
    /// <exception cref="AgentSessionCacheLockedException">The database is locked.</exception>
    Task<IReadOnlyList<AgentSessionJournalIndexTurn>> ReadTurnsOfRunsAsync(string sessionId, IReadOnlyCollection<string> runIds,
        CancellationToken cancellationToken = default);

    /// <summary>Reads the row of a session and its usage records, in journal order, from one snapshot of the index.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="cancellationToken">Stops the read.</param>
    /// <returns>The row and the usage records that report an operation; null when the index has no row for the session.</returns>
    /// <exception cref="ArgumentException"><paramref name="sessionId"/> is blank.</exception>
    /// <exception cref="AgentSessionCacheLockedException">The database is locked.</exception>
    Task<AgentSessionJournalIndexUsages?> ReadUsageAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes what an indexing of a journal found, in one transaction, when the row of the session is still the one it
    /// started from.
    /// </summary>
    /// <param name="update">The new row, the turns it added or changed and the usage records it found.</param>
    /// <param name="cancellationToken">Stops the write before it starts.</param>
    /// <returns>False when the row of the session is no longer <see cref="AgentSessionJournalIndexUpdate.Expected"/>: nothing was written.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="update"/> is null.</exception>
    /// <exception cref="AgentSessionCacheLockedException">The database is locked.</exception>
    Task<bool> WriteAsync(AgentSessionJournalIndexUpdate update, CancellationToken cancellationToken = default);

    /// <summary>Removes everything the index has of a session.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="cancellationToken">Stops the write before it starts.</param>
    /// <returns>A task that completes once removed.</returns>
    /// <exception cref="ArgumentException"><paramref name="sessionId"/> is blank.</exception>
    /// <exception cref="AgentSessionCacheLockedException">The database is locked.</exception>
    Task RemoveAsync(string sessionId, CancellationToken cancellationToken = default);
}

/// <summary>Where a record is in a journal; <see cref="Length"/> is 0 when there is none.</summary>
/// <param name="Offset">The byte offset of the record.</param>
/// <param name="Length">The length of the record with its line end.</param>
public readonly record struct AgentJournalRecordPosition(long Offset, int Length);

/// <summary>What the index knows of the journal of a session, as of one length and last-write time of it.</summary>
public sealed record AgentSessionJournalIndexRow
{
    /// <summary>The version of what the indexing records; a row of another version is indexed again.</summary>
    public const int CurrentFormatVersion = 1;

    /// <summary>Gets the session: the name of its journal file, without extension.</summary>
    public required string SessionId { get; init; }

    /// <summary>Gets the full path of the journal the row was read from.</summary>
    public required string JournalPath { get; init; }

    /// <summary>Gets the length of the journal the row covers, in bytes.</summary>
    public required long Length { get; init; }

    /// <summary>Gets the last-write time of the journal the row covers, in UTC ticks.</summary>
    public required long LastWriteUtcTicks { get; init; }

    /// <summary>Gets the exclusive byte offset of the end of the last complete record: where the indexing goes on.</summary>
    public required long EndOffset { get; init; }

    /// <summary>Gets the bytes that end at <see cref="EndOffset"/> (at most 256), to recognize the same journal once it grew.</summary>
    public required byte[] Guard { get; init; }

    /// <summary>Gets a value indicating whether the journal is UTF-8; false for a journal with another byte order mark.</summary>
    public bool Supported { get; init; } = true;

    /// <summary>Gets the version of what the indexing recorded.</summary>
    public int FormatVersion { get; init; } = CurrentFormatVersion;

    /// <summary>Gets the last summary snapshot before <see cref="EndOffset"/>.</summary>
    public AgentJournalRecordPosition Summary { get; init; }

    /// <summary>Gets the last state snapshot before <see cref="EndOffset"/>.</summary>
    public AgentJournalRecordPosition State { get; init; }

    /// <summary>Gets the last view-state snapshot before <see cref="EndOffset"/>.</summary>
    public AgentJournalRecordPosition ViewState { get; init; }

    /// <summary>Gets the number of turns.</summary>
    public int TurnCount { get; init; }

    /// <summary>
    /// Gets the number of records before <see cref="EndOffset"/> that are events of the history: every record but the
    /// blank lines and the four metadata snapshots (summary, state, CodeAlta header and view state).
    /// </summary>
    public long HistoryCount { get; init; }

    /// <summary>Gets the run of the record of the last turn that was indexed last; null when there is no turn.</summary>
    public string? LastRunId { get; init; }

    /// <summary>Gets the last notes record, as the journal holds it; null when the journal holds none.</summary>
    public string? NotesJson { get; init; }

    /// <summary>Gets the byte offset of the last notes record; null when the journal holds none.</summary>
    public long? NotesOffset { get; init; }

    /// <summary>
    /// Gets the byte offset of the first record that does not end the way the serializer writes one (a line cut by a
    /// crash, or text that is no record); null when there is none.
    /// </summary>
    public long? MalformedOffset { get; init; }

    /// <summary>
    /// Gets the code of the first usage record that could not be read (<c>record_too_large</c>, <c>corrupt_record</c>);
    /// null when every usage record was read. A sum of the usage of a session with one is never made.
    /// </summary>
    public string? UsageError { get; init; }
}

/// <summary>One turn of a session as the index keeps it.</summary>
/// <param name="Turn">The turn, with its number and positions.</param>
/// <param name="Summary">The last summary snapshot before the start of the turn.</param>
/// <param name="State">The last state snapshot before the start of the turn.</param>
/// <param name="ViewState">The last view-state snapshot before the start of the turn.</param>
public sealed record AgentSessionJournalIndexTurn(
    AgentSessionTurn Turn,
    AgentJournalRecordPosition Summary,
    AgentJournalRecordPosition State,
    AgentJournalRecordPosition ViewState);

/// <summary>The row of a session and its turns, read from one snapshot of the index.</summary>
/// <param name="Row">The row.</param>
/// <param name="Turns">The turns, in journal order.</param>
public sealed record AgentSessionJournalIndexTurns(AgentSessionJournalIndexRow Row, IReadOnlyList<AgentSessionJournalIndexTurn> Turns);

/// <summary>The row of a session and its usage records, read from one snapshot of the index.</summary>
/// <param name="Row">The row.</param>
/// <param name="Usage">The usage records, in journal order.</param>
public sealed record AgentSessionJournalIndexUsages(AgentSessionJournalIndexRow Row, IReadOnlyList<AgentSessionJournalIndexUsage> Usage);

/// <summary>The usage of one request to the model as the index keeps it.</summary>
/// <param name="Offset">The byte offset of the usage record.</param>
/// <param name="RunId">The run of the request; null for a record that names none.</param>
/// <param name="TimestampUtcTicks">The time of the record, in UTC ticks.</param>
/// <param name="OperationJson">The usage the provider reported, serialized as JSON.</param>
public sealed record AgentSessionJournalIndexUsage(long Offset, string? RunId, long TimestampUtcTicks, string OperationJson);

/// <summary>What one indexing of a journal writes.</summary>
/// <param name="Row">The new row of the session.</param>
/// <param name="Turns">The turns that were added or changed.</param>
/// <param name="Usage">The usage records that were found, after those the index already had.</param>
/// <param name="Replace">True when the indexing read the journal from its start: the turns and usage records the index had are removed first.</param>
/// <param name="Expected">The row the indexing started from (its length and last-write time); null to write whatever the index has.</param>
public sealed record AgentSessionJournalIndexUpdate(
    AgentSessionJournalIndexRow Row,
    IReadOnlyList<AgentSessionJournalIndexTurn> Turns,
    IReadOnlyList<AgentSessionJournalIndexUsage> Usage,
    bool Replace,
    AgentSessionCacheFileStamp? Expected);
