using System.Collections.Concurrent;
using System.Data;
using System.Runtime.CompilerServices;
using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using Microsoft.Data.Sqlite;

namespace CodeAlta.Catalog;

internal sealed class SessionJournalSqliteCache : IAgentSessionProjectionCache
{
    private const string Owner = "session_cache";
    private const string TablePrefix = "session_projection_cache";
    private const int SchemaVersion = 8;
    private const int ProjectionVersion = 1;

    // The longest preview of the last answer of a session a row keeps: the answer itself is in the history of the journal.
    internal const int MaximumSummaryPreviewLength = 500;

    // The columns of the list of sessions: short values only. What one session needs beyond them (its provider state, its last
    // state record) is in session_projection_cache_detail, read for that session alone.
    private const string ListColumns = """
        session_id, journal_path, journal_last_write_utc_ticks, journal_length, created_at_utc_ticks, updated_at_utc_ticks,
        protocol_family, provider_id, provider_key, working_directory, title, summary, model_id, reasoning_effort, agent_prompt_id,
        parent_session_id, created_by_session_id, created_by_run_id, provider_session_id, summary_json, local_provider_key,
        local_model_id, local_reasoning_effort, local_agent_prompt_id, local_permission_mode, local_title_mode, archived,
        local_archived_at_utc_ticks, local_queued_prompt_count, message_count, local_parent_session_id, created_by_json,
        local_state_cached, space_id, journal_header_json, journal_header_read
        """;

    // ListColumns, of the table named c.
    private static readonly string QualifiedListColumns = string.Join(", ",
        ListColumns.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(static column => "c." + column));
    private const string CacheCompleteMetadataKey = "session_projection_cache_complete";
    private const string CacheCompleteValue = "1";
    private const string CacheIncompleteValue = "0";

    // Which copy the file was when the rows were last known to be those of this file (ApplicationDatabase.ReadCopyNumberAsync).
    private const string CopySeenMetadataKey = "session_projection_cache_copy";

    // A number every write of the rows raises, in its own transaction: a listing that reads it with the rows knows whether a
    // writer this instance does not know of changed them since its copy in memory was made.
    private const string RowsWrittenMetadataKey = "session_projection_cache_rows_written";

    private readonly ApplicationDatabase _database;
    private readonly SemaphoreSlim _schemaGate = new(initialCount: 1, maxCount: 1);
    // The view of each journal (its header and its last view state) as last read whole, with the stamp it was read at: an
    // upsert that continues that journal (AgentSessionCacheProjection.ContinuesFrom) keeps it instead of reading it again.
    private readonly ConcurrentDictionary<string, HydratedView> _views = new(StringComparer.OrdinalIgnoreCase);
    // The upserts that wait to be written, one per session (the row key, without case), and whether a write takes them.
    private readonly object _pendingGate = new();
    private readonly Dictionary<string, PendingUpsert> _pendingUpserts = new(StringComparer.OrdinalIgnoreCase);
    private bool _flushingUpserts;
    // The generation of the file whose tables are known to exist; -1 when they have to be made or checked.
    private int _schemaGeneration = -1;

    // The two below are written with the schema gate held. The generation of the file whose rows are trusted: a
    // file that was never replaced is generation 0. And the number of times the tables were made or made again.
    private int _rowsGeneration;
    private int _schemaEpoch;

    // The rows of the list as last read, by session (copied on write: a reader enumerates the dictionary it took), for the
    // file and the tables they were read from and the number of row writes they include. A listing reads again only the
    // sessions this instance wrote since (_staleRows); a write it does not know of, other tables or another file drop the
    // copy. _rowsChanges moves at every write of this instance: a read that saw it move does not keep what it read.
    private readonly Lock _rowsGate = new();
    private Dictionary<string, SqliteSessionProjectionRow>? _rowsCopy;
    private (int Generation, int Epoch, long Written) _rowsCopyKey;
    private readonly HashSet<string> _staleRows = new(StringComparer.OrdinalIgnoreCase);
    private long _rowsChanges;

    public SessionJournalSqliteCache(CatalogOptions options)
        : this(ApplicationDatabase.Create(options ?? throw new ArgumentNullException(nameof(options))))
    {
    }

    public SessionJournalSqliteCache(ApplicationDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _database = database;
        JournalIndex = new SessionJournalIndexStore(database);
    }

    // The tables of its own owner (session_journal_index) on the same database.
    public IAgentSessionJournalIndex JournalIndex { get; }

    public string DatabasePath => _database.DatabasePath;

    // The upserts that wait for a write, and whether a write takes them, for tests.
    internal int PendingUpsertCount { get { lock (_pendingGate) return _pendingUpserts.Count; } }

    internal bool IsWritingUpserts { get { lock (_pendingGate) return _flushingUpserts; } }

    public ApplicationDatabase Database => _database;

    // Told after each journal a rebuild of the list read: how many so far, of how many.
    internal Action<int, int>? RebuildProgress { get; set; }

    // How many times an upsert read the header and the last view state of a journal, for tests.
    internal int JournalViewReadCount => Volatile.Read(ref _journalViewReadCount);

    // How many rows were read from the database, for tests.
    internal int RowReadCount => Volatile.Read(ref _rowReadCount);

    private int _rowReadCount;

    private int _journalViewReadCount;

    public async IAsyncEnumerable<AgentSessionCacheProjection> ListSessionsAsync(
        AgentSessionCacheProjectionContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        if (await IsCacheCompleteAsync(cancellationToken).ConfigureAwait(false))
        {
            var rows = await TryQuerySessionRowsAsync(sessionId: null, cancellationToken).ConfigureAwait(false);
            if (rows is not null)
            {
                // What this listing learned from journals, written back once it has listed every row: the next one
                // does not read them again.
                var writeBack = new RowWriteBack();
                foreach (var row in rows)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var projection = await ProjectRowAsync(row, context, writeBack, cancellationToken).ConfigureAwait(false);
                    if (projection is not null) yield return projection;
                }

                await WriteBackAsync(writeBack, cancellationToken).ConfigureAwait(false);
                yield break;
            }
        }

        await foreach (var projection in RebuildAndListSessionsAsync(context, cancellationToken).ConfigureAwait(false))
        {
            yield return projection;
        }
    }

    public async Task<AgentSessionCacheProjection?> GetSessionAsync(
        string sessionId,
        AgentSessionCacheProjectionContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(context);

        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        var rows = await TryQuerySessionRowsAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (rows is null)
        {
            return null;
        }

        var row = rows.Count == 0 ? null : rows[0];
        if (row is null)
        {
            return null;
        }

        var writeBack = new RowWriteBack();
        var projection = await ProjectRowAsync(row, context, writeBack, cancellationToken).ConfigureAwait(false);
        await WriteBackAsync(writeBack, cancellationToken).ConfigureAwait(false);
        return projection;
    }

    // The projection a row stands for, as the list gives it; null when its journal is gone (the row is removed) or can no
    // longer be projected. What it had to read from the journal is added to writeBack.
    private async Task<AgentSessionCacheProjection?> ProjectRowAsync(
        SqliteSessionProjectionRow row,
        AgentSessionCacheProjectionContext context,
        RowWriteBack writeBack,
        CancellationToken cancellationToken)
    {
        // One status read of the journal says both whether it is there and whether the row is its projection.
        var stamp = TryGetStamp(row.JournalPath);
        if (stamp is null)
        {
            await RemoveSessionAsync(row.Summary.SessionId, cancellationToken).ConfigureAwait(false);
            return null;
        }

        // Atomic selection commits may deliberately leave this derived cache behind.
        // Never publish a stale provider/continuation pair after a journal replacement.
        if (stamp.Value != row.Stamp)
        {
            var fresh = await context.ProjectSessionFileAsync(row.JournalPath, cancellationToken).ConfigureAwait(false);
            if (fresh is null)
            {
                return null;
            }

            // Written back with its view, so that the next listing does not project it again.
            var hydrated = await TryHydrateAsync(fresh, cancellationToken).ConfigureAwait(false);
            if (hydrated is null)
            {
                return fresh;
            }

            writeBack.Upserts.Add(QueueUpsert(hydrated, writeBack: true));
            return hydrated.ToProjection();
        }

        // A row without the count of its pending queued prompts (written before it was kept) is not read here: the list
        // reads no journal for it. The first read of the last state of the journal fills it (FillLocalStateAsync).
        if (!row.HeaderRead)
        {
            // A row written before the list kept the header of the journal: it is read once.
            var header = await TryReadHeaderJsonAsync(row.JournalPath, cancellationToken).ConfigureAwait(false);
            if (header.Read && TryGetStamp(row.JournalPath) == row.Stamp)
            {
                writeBack.Headers.Add((row, header.Json));
            }

            return row.ToProjection() with { JournalHeaderJson = header.Json };
        }

        return row.ToProjection();
    }

    public async Task UpsertSessionAsync(
        AgentSessionCacheProjection projection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(projection);

        // The journal is read before the write starts: the queue of writers is not held while a file is read.
        var hydrated = await HydrateSessionViewDataAsync(projection, cancellationToken).ConfigureAwait(false);
        await QueueUpsert(hydrated).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    // Writes what a listing learned from the journals: the projections of the journals that changed, and the headers of
    // the rows that did not have them. The rows are derived: a write that cannot be made now is made by a later listing,
    // which finds the same journals again.
    private async Task WriteBackAsync(RowWriteBack writeBack, CancellationToken cancellationToken)
    {
        try
        {
            if (writeBack.Headers.Count > 0)
            {
                await WriteAsync(
                        [.. writeBack.Headers.Select(static header => header.Row.Summary.SessionId)],
                        async (connection, token) =>
                        {
                            foreach (var (row, json) in writeBack.Headers)
                            {
                                // Only the row of the journal the header was read from: a row written meanwhile has its own.
                                await using var command = connection.CreateCommand();
                                command.CommandText = """
                                    UPDATE session_projection_cache
                                    SET journal_header_json = $journal_header_json,
                                        journal_header_read = 1
                                    WHERE session_id = $session_id
                                      AND journal_header_read = 0
                                      AND journal_last_write_utc_ticks = $journal_last_write_utc_ticks
                                      AND journal_length = $journal_length;
                                    """;
                                AddParameter(command, "$session_id", row.Summary.SessionId.Trim());
                                AddParameter(command, "$journal_header_json", json);
                                AddParameter(command, "$journal_last_write_utc_ticks", row.Stamp.LastWriteTimeUtc.Ticks);
                                AddParameter(command, "$journal_length", row.Stamp.Length);
                                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                            }
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            if (writeBack.Upserts.Count > 0)
            {
                await Task.WhenAll(writeBack.Upserts).WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (AgentSessionCacheLockedException)
        {
        }
    }

    // Queues the write of a projection read from its journal; the task ends when it is written. writeBack: the projection
    // is one a listing read, which never replaces the upsert of a writer that waits.
    private Task QueueUpsert(SqliteSessionProjection hydrated, bool writeBack = false)
    {
        Task written;
        var flush = false;
        lock (_pendingGate)
        {
            // A session has one upsert waiting at most: the one asked for last replaces it, and both callers are
            // answered when it is written.
            var key = hydrated.Projection.Summary.SessionId.Trim();
            if (_pendingUpserts.TryGetValue(key, out var pending))
            {
                if (!writeBack) pending.Projection = hydrated;
            }
            else
            {
                pending = new PendingUpsert(hydrated);
                _pendingUpserts[key] = pending;
            }

            written = pending.Completion.Task;
            if (!_flushingUpserts)
            {
                _flushingUpserts = true;
                flush = true;
            }
        }

        if (flush)
        {
            _ = FlushPendingUpsertsAsync();
        }

        return written;
    }

    // Writes the upserts that wait, all in one transaction, until none waits. While a write waits for the queue of the
    // database or runs, the upserts asked for meanwhile gather for the next one: sessions that write at the same time
    // share a connection and a commit, and nothing waits longer than it would for a write of its own.
    private async Task FlushPendingUpsertsAsync()
    {
        while (true)
        {
            PendingUpsert[] batch;
            lock (_pendingGate)
            {
                if (_pendingUpserts.Count == 0)
                {
                    _flushingUpserts = false;
                    return;
                }

                batch = [.. _pendingUpserts.Values];
                _pendingUpserts.Clear();
            }

            try
            {
                await WriteAsync(
                        [.. batch.Select(static pending => pending.Projection.Projection.Summary.SessionId)],
                        async (connection, token) =>
                        {
                            foreach (var pending in batch)
                            {
                                await UpsertSessionCoreAsync(connection, transaction: null, pending.Projection, token).ConfigureAwait(false);
                            }
                        },
                        CancellationToken.None)
                    .ConfigureAwait(false);
                foreach (var pending in batch)
                {
                    pending.Completion.TrySetResult();
                }
            }
            catch (Exception exception) when (batch.Length > 1 && exception is not (AgentSessionCacheLockedException or ObjectDisposedException))
            {
                // A database that is locked, cannot be used or is closed fails every upsert alike. What else failed is not
                // known: each is written on its own, and only the one that fails again reports it.
                foreach (var pending in batch)
                {
                    try
                    {
                        await WriteAsync(
                                [pending.Projection.Projection.Summary.SessionId],
                                (connection, token) => UpsertSessionCoreAsync(connection, transaction: null, pending.Projection, token),
                                CancellationToken.None)
                            .ConfigureAwait(false);
                        pending.Completion.TrySetResult();
                    }
                    catch (Exception failure)
                    {
                        pending.Completion.TrySetException(failure);
                    }
                }
            }
            catch (Exception exception)
            {
                foreach (var pending in batch)
                {
                    pending.Completion.TrySetException(exception);
                }
            }
        }
    }

    public async Task RemoveSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        // An upsert that still waits would write the row again after it is removed: it is answered without being written.
        PendingUpsert? dropped;
        lock (_pendingGate)
        {
            _pendingUpserts.Remove(sessionId.Trim(), out dropped);
        }

        dropped?.Completion.TrySetResult();
        foreach (var (key, view) in _views)
        {
            if (string.Equals(view.SessionId, sessionId.Trim(), StringComparison.OrdinalIgnoreCase)) _views.TryRemove(key, out _);
        }

        await WriteAsync(
                [sessionId],
                async (connection, token) =>
                {
                    await using var command = connection.CreateCommand();
                    command.CommandText = """
                        DELETE FROM session_projection_cache WHERE session_id = $session_id;
                        DELETE FROM session_projection_cache_detail WHERE session_id = $session_id;
                        """;
                    AddParameter(command, "$session_id", sessionId.Trim());
                    await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<AgentSessionCacheReconciliationResult> ReconcileAsync(
        AgentSessionCacheProjectionContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        // The rows are read from tables that are whole, and the tables are remembered: when they are made again
        // while the journals are compared with these rows, the rows that were found up to date are gone, and the
        // list is not called complete at the end.
        int epoch;
        IReadOnlyList<SqliteSessionProjectionRow>? existingRows;
        var attempt = 0;
        do
        {
            await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
            epoch = Volatile.Read(ref _schemaEpoch);
            existingRows = await TryQuerySessionRowsAsync(sessionId: null, cancellationToken).ConfigureAwait(false);
        }
        while (existingRows is null && ++attempt < 3);

        existingRows ??= [];
        if (!Directory.Exists(context.SessionsRootPath))
        {
            var removed = await PruneRowsMissingFromDiskAsync([], cancellationToken).ConfigureAwait(false);
            await MarkCacheCompleteAsync(epoch, cancellationToken).ConfigureAwait(false);
            return new AgentSessionCacheReconciliationResult(removed > 0, 0, removed);
        }

        var existingByPath = existingRows.ToDictionary(static row => NormalizePathKey(row.JournalPath), StringComparer.OrdinalIgnoreCase);
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var upserted = 0;
        var pruned = 0;
        var changed = false;

        foreach (var sessionFile in Directory.EnumerateFiles(context.SessionsRootPath, "*.jsonl", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pathKey = NormalizePathKey(sessionFile);
            seenPaths.Add(pathKey);
            var stamp = TryGetStamp(sessionFile);
            if (stamp is not null &&
                existingByPath.TryGetValue(pathKey, out var existing) &&
                existing.Stamp == stamp.Value)
            {
                continue;
            }

            var projection = await context.ProjectSessionFileAsync(sessionFile, cancellationToken).ConfigureAwait(false);
            if (projection is null)
            {
                if (existingByPath.TryGetValue(pathKey, out var staleRow))
                {
                    await RemoveSessionAsync(staleRow.Summary.SessionId, cancellationToken).ConfigureAwait(false);
                    pruned++;
                    changed = true;
                }

                continue;
            }

            await UpsertSessionAsync(projection, cancellationToken).ConfigureAwait(false);
            upserted++;
            changed = true;
        }

        pruned += await PruneRowsMissingFromDiskAsync(seenPaths, cancellationToken).ConfigureAwait(false);
        changed |= pruned > 0;
        await MarkCacheCompleteAsync(epoch, cancellationToken).ConfigureAwait(false);
        return new AgentSessionCacheReconciliationResult(changed, upserted, pruned);
    }

    // Writes the header of a journal whose first line was just checked (and written when it was missing), from the stamp the
    // journal had before to the one it has after. Only a row that was the projection of the journal before takes it: a row
    // that was not is not made to look like one, and is projected again from its journal.
    public async Task UpsertSessionViewHeaderAsync(
        string sessionId,
        string journalPath,
        SessionViewJournalHeader header,
        string? headerJson,
        AgentSessionCacheFileStamp before,
        AgentSessionCacheFileStamp after,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(journalPath);
        ArgumentNullException.ThrowIfNull(header);

        await WriteAsync(
                [sessionId],
                async (connection, token) =>
                {
                    await using var command = connection.CreateCommand();
                    command.CommandText = """
                        UPDATE session_projection_cache
                        SET journal_last_write_utc_ticks = $journal_last_write_utc_ticks,
                            journal_length = $journal_length,
                            cache_updated_utc_ticks = $cache_updated_utc_ticks,
                            kind = $kind,
                            project_ref = $project_ref,
                            space_id = COALESCE($space_id, space_id),
                            local_parent_session_id = COALESCE(local_parent_session_id, $local_parent_session_id),
                            created_by_json = COALESCE(created_by_json, $created_by_json),
                            journal_header_json = $journal_header_json,
                            journal_header_read = 1
                        WHERE session_id = $session_id
                          AND journal_last_write_utc_ticks = $before_last_write_utc_ticks
                          AND journal_length = $before_length;
                        """;
                    AddParameter(command, "$journal_header_json", headerJson);
                    AddParameter(command, "$session_id", sessionId.Trim());
                    AddParameter(command, "$journal_last_write_utc_ticks", after.LastWriteTimeUtc.Ticks);
                    AddParameter(command, "$journal_length", after.Length);
                    AddParameter(command, "$before_last_write_utc_ticks", before.LastWriteTimeUtc.Ticks);
                    AddParameter(command, "$before_length", before.Length);
                    AddParameter(command, "$cache_updated_utc_ticks", DateTimeOffset.UtcNow.UtcTicks);
                    AddParameter(command, "$kind", header.Kind.ToString());
                    AddParameter(command, "$project_ref", NormalizeOptionalText(header.ProjectRef));
                    AddParameter(command, "$space_id", NormalizeOptionalText(header.SpaceId));
                    AddParameter(command, "$local_parent_session_id", NormalizeOptionalText(header.ParentSessionId));
                    AddParameter(command, "$created_by_json", SerializeCreatedBy(header.CreatedBy));
                    await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    // Writes the state a session view just appended to its journal as the last state of its row, in one write with the stamp
    // the journal has after it: a reader never finds the row at that stamp with an older state. The header of the descriptor
    // and the header the journal starts with are written with it when they are given. A write that says the stamp the
    // journal had before it moves only a row that was the projection of the journal then: another row keeps its stamp, and
    // says no last state (it is projected again from its journal), with the other columns of the state written as they are.
    public async Task UpsertSessionViewStateAsync(SessionViewStateRowWrite write, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(write);
        ArgumentException.ThrowIfNullOrWhiteSpace(write.SessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(write.JournalPath);
        ArgumentNullException.ThrowIfNull(write.State);

        await WriteAsync(
                [write.SessionId],
                async (connection, token) =>
                {
                    // The row as it was before the write, in its transaction: whether it was the projection of the journal then.
                    var before = await ReadRowStampCoreAsync(connection, write.SessionId, token).ConfigureAwait(false);
                    if (before is null)
                    {
                        return;
                    }

                    var inSync = write.ExpectedStamp is not { } expected || before.Value == expected;
                    await using var command = connection.CreateCommand();
                    command.CommandText = $"""
                        UPDATE session_projection_cache
                        SET {(inSync ? "journal_last_write_utc_ticks = $journal_last_write_utc_ticks, journal_length = $journal_length," : string.Empty)}
                            cache_updated_utc_ticks = $cache_updated_utc_ticks,
                            {(write.Header is null ? string.Empty : "kind = $kind, project_ref = $project_ref, space_id = COALESCE($space_id, space_id),")}
                            {(write.HeaderJson is null ? string.Empty : "journal_header_json = $journal_header_json, journal_header_read = 1,")}
                            {LocalStateAssignments}
                        WHERE session_id = $session_id;
                        """;
                    var stamp = write.Stamp ?? TryGetStamp(write.JournalPath);
                    AddParameter(command, "$session_id", write.SessionId.Trim());
                    if (inSync)
                    {
                        AddParameter(command, "$journal_last_write_utc_ticks", stamp?.LastWriteTimeUtc.Ticks ?? 0L);
                        AddParameter(command, "$journal_length", stamp?.Length ?? 0L);
                    }

                    AddParameter(command, "$cache_updated_utc_ticks", DateTimeOffset.UtcNow.UtcTicks);
                    if (write.Header is { } header)
                    {
                        AddParameter(command, "$kind", header.Kind.ToString());
                        AddParameter(command, "$project_ref", NormalizeOptionalText(header.ProjectRef));
                        AddParameter(command, "$space_id", NormalizeOptionalText(header.SpaceId));
                    }

                    if (write.HeaderJson is { } headerJson)
                    {
                        AddParameter(command, "$journal_header_json", headerJson);
                    }

                    AddLocalStateParameters(command, write.State);
                    await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                    // A row that was not the projection of the journal says no last state: it is projected again.
                    await WriteLocalStateJsonCoreAsync(connection, write.SessionId, inSync ? write.StateJson : null, token).ConfigureAwait(false);
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    // The stamp of the journal a row is the projection of; null when the session has no row.
    private static async Task<AgentSessionCacheFileStamp?> ReadRowStampCoreAsync(SqliteConnection connection, string sessionId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT journal_last_write_utc_ticks, journal_length FROM session_projection_cache WHERE session_id = $session_id LIMIT 1;";
        AddParameter(command, "$session_id", sessionId.Trim());
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new AgentSessionCacheFileStamp(new DateTime(reader.GetInt64(0), DateTimeKind.Utc), reader.GetInt64(1))
            : null;
    }

    // Writes the last state record of a session (null: not known) to its detail row.
    private static async Task WriteLocalStateJsonCoreAsync(SqliteConnection connection, string sessionId, string? stateJson, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO session_projection_cache_detail (session_id, local_state_json)
            VALUES ($session_id, $local_state_json)
            ON CONFLICT(session_id) DO UPDATE SET local_state_json = excluded.local_state_json;
            """;
        AddParameter(command, "$session_id", sessionId.Trim());
        AddParameter(command, "$local_state_json", stateJson);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    // The last state of a session as its row keeps it, with what tells whether it is the one of its journal now: the stamp
    // of the journal it was written at, and the header the journal starts with. Null when the session has no row, or the
    // database cannot be read now (damaged: the caller reads the journal). withState: false for a caller that compares only the
    // stamp and the header, which does not read the state record (LocalStateJson is then null).
    internal async Task<SessionViewStateRow?> TryReadSessionStateRowAsync(string sessionId, CancellationToken cancellationToken, bool withState = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        return await TryReadAsync<SessionViewStateRow?>(
                async (connection, token) =>
                {
                    await using var command = connection.CreateCommand();
                    command.CommandText = $"""
                        SELECT c.journal_path, c.journal_last_write_utc_ticks, c.journal_length, c.journal_header_json, c.journal_header_read,
                               c.local_state_cached, {(withState ? "d.local_state_json" : "NULL AS local_state_json")}
                        FROM session_projection_cache c
                        {(withState ? "LEFT JOIN session_projection_cache_detail d ON d.session_id = c.session_id" : string.Empty)}
                        WHERE c.session_id = $session_id
                        LIMIT 1;
                        """;
                    AddParameter(command, "$session_id", sessionId.Trim());
                    await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, token).ConfigureAwait(false);
                    if (!await reader.ReadAsync(token).ConfigureAwait(false))
                    {
                        return null;
                    }

                    return new SessionViewStateRow(
                        GetRequiredString(reader, "journal_path"),
                        new AgentSessionCacheFileStamp(
                            new DateTime(GetInt64(reader, "journal_last_write_utc_ticks"), DateTimeKind.Utc),
                            GetInt64(reader, "journal_length")),
                        GetString(reader, "journal_header_json"),
                        GetInt64(reader, "journal_header_read") != 0,
                        GetInt64(reader, "local_state_cached") != 0 ? GetString(reader, "local_state_json") : null);
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    // Writes the last state of a journal, read from the journal at the given stamp, to the row of its session when the row
    // is the projection of the journal at that stamp but does not hold its state (a row written before the state was kept);
    // nothing otherwise. stateJson is the JSON null for a journal without a state.
    internal async Task FillLocalStateAsync(string sessionId, string journalPath, AgentSessionCacheFileStamp stamp, SessionViewLocalState? state,
        string stateJson, CancellationToken cancellationToken)
    {
        try
        {
            await WriteAsync(
                    [sessionId],
                    async (connection, token) =>
                    {
                        await using var command = connection.CreateCommand();
                        command.CommandText = $"""
                            UPDATE session_projection_cache
                            SET {LocalStateAssignments}
                            WHERE session_id = $session_id
                              AND journal_path = $journal_path
                              AND NOT EXISTS (SELECT 1 FROM session_projection_cache_detail d
                                              WHERE d.session_id = session_projection_cache.session_id AND d.local_state_json IS NOT NULL)
                              AND journal_last_write_utc_ticks = $journal_last_write_utc_ticks
                              AND journal_length = $journal_length;
                            """;
                        AddParameter(command, "$session_id", sessionId.Trim());
                        AddParameter(command, "$journal_path", NormalizePathKey(journalPath));
                        AddParameter(command, "$journal_last_write_utc_ticks", stamp.LastWriteTimeUtc.Ticks);
                        AddParameter(command, "$journal_length", stamp.Length);
                        AddLocalStateParameters(command, state ?? new SessionViewLocalState());
                        if (await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) > 0)
                        {
                            await WriteLocalStateJsonCoreAsync(connection, sessionId, stateJson, token).ConfigureAwait(false);
                        }
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (AgentSessionCacheLockedException)
        {
            // The row is derived: the next read of the state reads the journal again, and fills the row then.
        }
    }

    // The columns of the last state of a session, as UpsertSessionViewStateAsync and FillLocalStateAsync write them.
    private const string LocalStateAssignments = """
        local_provider_key = $local_provider_key,
        local_model_id = $local_model_id,
        local_reasoning_effort = $local_reasoning_effort,
        local_agent_prompt_id = $local_agent_prompt_id,
        local_permission_mode = $local_permission_mode,
        local_title_mode = $local_title_mode,
        archived = $archived,
        local_archived_at_utc_ticks = $local_archived_at_utc_ticks,
        local_queued_prompt_count = $local_queued_prompt_count,
        message_count = $message_count,
        local_parent_session_id = $local_parent_session_id,
        created_by_json = $created_by_json,
        local_state_cached = 1
        """;

    private static void AddLocalStateParameters(SqliteCommand command, SessionViewLocalState state)
    {
        AddParameter(command, "$local_provider_key", NormalizeOptionalText(state.ProviderKey));
        AddParameter(command, "$local_model_id", NormalizeOptionalText(state.ModelId));
        AddParameter(command, "$local_reasoning_effort", FormatReasoningEffort(state.ReasoningEffort));
        AddParameter(command, "$local_agent_prompt_id", NormalizeOptionalText(state.AgentPromptId));
        AddParameter(command, "$local_permission_mode", NormalizeOptionalText(state.PermissionMode));
        AddParameter(command, "$local_title_mode", FormatTitleMode(state.TitleMode));
        AddParameter(command, "$archived", state.Archived ? 1 : 0);
        AddParameter(command, "$local_archived_at_utc_ticks", state.Archived ? state.ArchivedAt?.UtcTicks : null);
        AddParameter(command, "$local_queued_prompt_count", state.CountPendingQueuedPrompts());
        AddParameter(command, "$message_count", state.MessageCount);
        AddParameter(command, "$local_parent_session_id", NormalizeOptionalText(state.ParentSessionId));
        AddParameter(command, "$created_by_json", SerializeCreatedBy(state.CreatedBy));
    }

    // The tables are made once for each file the database has had: a file that was replaced (or deleted) has none.
    private async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _schemaGeneration) == _database.Generation && File.Exists(_database.DatabasePath))
        {
            return;
        }

        await _schemaGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_schemaGeneration == _database.Generation && File.Exists(_database.DatabasePath))
            {
                return;
            }

            await PrepareSchemaAsync(cleared: false, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _schemaGate.Release();
        }
    }

    // A read or a write found the tables damaged: they are dropped and made again, once for all the operations
    // that met the same damage. The plugins that share the file keep their tables.
    private async Task RepairSchemaAsync(int generation, int epoch, CancellationToken cancellationToken)
    {
        await _schemaGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_schemaEpoch != epoch)
            {
                return; // Another operation made the tables again since this one read them.
            }

            await ClearTablesAsync(generation, cancellationToken).ConfigureAwait(false);
            await PrepareSchemaAsync(cleared: true, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _schemaGate.Release();
        }
    }

    // Runs with the schema gate held. cleared: the tables were just dropped.
    private async Task PrepareSchemaAsync(bool cleared, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            var before = _database.Generation;
            try
            {
                await MigrateSchemaAsync(cancellationToken).ConfigureAwait(false);

                // Tables that were made again, and a file that was replaced since the rows were written (restored
                // from an older copy, or deleted and made again), say nothing about the sessions that exist: they
                // are listed from the journals again. The mark is written, not left out: a session that is saved
                // before the next list adds a row, and rows without a mark read as a complete list.
                // A file that is a copy the rows have not seen is such a file too, whoever put it there: the service in a
                // process that ended before the list was used, or somebody by hand while the application was closed. The
                // generation, which lives in memory, knows neither.
                var generation = _database.Generation;
                var copy = await ReadCopyAsync(cancellationToken).ConfigureAwait(false);
                if (cleared || generation != _rowsGeneration || copy.Number != copy.Seen)
                {
                    await WriteCoreAsync(
                            async (connection, token) =>
                            {
                                await SetCacheCompleteCoreAsync(connection, complete: false, token).ConfigureAwait(false);
                                await SetCopySeenCoreAsync(connection, token).ConfigureAwait(false);
                            },
                            schemaOwned: true,
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                _rowsGeneration = generation;
                Volatile.Write(ref _schemaEpoch, _schemaEpoch + 1);
                Volatile.Write(ref _schemaGeneration, generation);
                return;
            }
            catch (SqliteException exception) when (attempt < 2 && ApplicationDatabase.IsDamaged(exception))
            {
                await ClearTablesAsync(before, cancellationToken).ConfigureAwait(false);
                cleared = true;
            }
            catch (ApplicationDatabaseNewerVersionException) when (attempt < 2)
            {
                // A newer build wrote these tables, in a layout this one does not know, and the user came back to
                // this build. The list is not data to protect: it is made again from the journals, in the layout
                // of this build, and the newer build migrates it again when it returns.
                await ClearTablesAsync(before, cancellationToken).ConfigureAwait(false);
                cleared = true;
            }
            catch (SqliteException) when (attempt < 2 && _database.Generation != before)
            {
                // The file was replaced after the tables were made in it: they are made in the new one.
            }
        }
    }

    private async Task MigrateSchemaAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _database.MigrateAsync(
                    Owner,
                    tablePrefix: null,
                    SchemaVersion,
                    (connection, _, _, token) => new ValueTask(EnsureSchemaCoreAsync(connection, token)),
                    recoverDamagedFile: false,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (SqliteException exception) when (ApplicationDatabase.IsLocked(exception))
        {
            throw CreateLockedException(exception);
        }
        catch (Exception exception) when (IsFileError(exception))
        {
            throw CreateFileException(exception);
        }
    }

    // The session list is rebuilt from the journals, so its tables are dropped and made again; the plugins that
    // share the file keep theirs. When the damage is not in these tables, the file itself is replaced.
    private async Task ClearTablesAsync(int generation, CancellationToken cancellationToken)
    {
        MarkRowsChanged(sessionIds: null, written: null);
        try
        {
            try
            {
                await _database.DropTablesAsync(Owner, TablePrefix, recoverDamagedFile: false, cancellationToken).ConfigureAwait(false);
            }
            catch (SqliteException again) when (ApplicationDatabase.IsDamaged(again))
            {
                await _database.RecoverDamagedFileAsync(again, generation, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (SqliteException locked) when (ApplicationDatabase.IsLocked(locked))
        {
            throw CreateLockedException(locked);
        }
        catch (Exception failure) when (IsFileError(failure))
        {
            throw CreateFileException(failure);
        }

        Volatile.Write(ref _schemaGeneration, -1);
    }

    // A write of the session list: it makes the tables when they are missing, and when SQLite finds them damaged
    // it clears them and runs the write once more.
    private Task WriteAsync(Func<SqliteConnection, CancellationToken, Task> write, CancellationToken cancellationToken)
        => WriteAsync(sessionIds: null, write, cancellationToken);

    // sessionIds: the sessions whose rows the write changes, read again by the next listing; null when it is not known (the
    // copy of the rows in memory is dropped).
    private async Task WriteAsync(IReadOnlyCollection<string>? sessionIds, Func<SqliteConnection, CancellationToken, Task> write,
        CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await WriteCoreAsync(write, schemaOwned: false, cancellationToken, sessionIds).ConfigureAwait(false);
    }

    // schemaOwned: the caller is making the schema and holds its gate, so a damage it meets is its own to handle.
    // sessionIds: as for WriteAsync; null drops the copy of the rows in memory.
    private async Task WriteCoreAsync(Func<SqliteConnection, CancellationToken, Task> write, bool schemaOwned, CancellationToken cancellationToken,
        IReadOnlyCollection<string>? sessionIds = null)
    {
        // Marked before, so that a listing that reads meanwhile does not keep what it read; and after, for one that read
        // the rows as they were before the commit.
        MarkRowsChanged(sessionIds, written: null);
        long? written = null;
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                var generation = _database.Generation;
                var epoch = Volatile.Read(ref _schemaEpoch);
                try
                {
                    written = await _database.WriteAsync<long?>(
                            Owner,
                            async (connection, token) =>
                            {
                                await write(connection, token).ConfigureAwait(false);
                                return await CountRowsWrittenCoreAsync(connection, token).ConfigureAwait(false);
                            },
                            recoverDamagedFile: false,
                            cancellationToken)
                        .ConfigureAwait(false);
                    return;
                }
                catch (SqliteException exception) when (attempt == 0 && !schemaOwned && ApplicationDatabase.IsDamaged(exception))
                {
                    await RepairSchemaAsync(generation, epoch, cancellationToken).ConfigureAwait(false);
                    await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (SqliteException exception) when (ApplicationDatabase.IsLocked(exception))
                {
                    throw CreateLockedException(exception);
                }
                catch (SqliteException) when (attempt == 0 && !schemaOwned && _database.Generation != generation)
                {
                    // The file was replaced while the write ran (another owner met the damage, or the file went): the
                    // write ran on a file that has none of these tables. They are made, and the write runs once more.
                    await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (IsFileError(exception))
                {
                    throw CreateFileException(exception);
                }
            }
        }
        finally
        {
            MarkRowsChanged(sessionIds, written);
        }
    }

    // Raises the number of row writes in the transaction of a write, and gives it.
    private static async Task<long?> CountRowsWrittenCoreAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO session_projection_cache_metadata (key, value) VALUES ($key, '1')
            ON CONFLICT(key) DO UPDATE SET value = CAST(CAST(value AS INTEGER) + 1 AS TEXT);
            SELECT CAST(value AS INTEGER) FROM session_projection_cache_metadata WHERE key = $key;
            """;
        AddParameter(command, "$key", RowsWrittenMetadataKey);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is long written ? written : null;
    }

    // A write of the rows begins (written null) or ended (written: the number of row writes it made, null when it failed or
    // is not known). The sessions it names are read again by the next listing; a write that names none drops the copy, and
    // so does one whose number says that another writer wrote in between.
    private void MarkRowsChanged(IReadOnlyCollection<string>? sessionIds, long? written)
    {
        lock (_rowsGate)
        {
            _rowsChanges++;
            if (_rowsCopy is null) return;
            if (sessionIds is null)
            {
                DropRowsCopy();
                return;
            }

            foreach (var sessionId in sessionIds) _staleRows.Add(sessionId.Trim());
            if (written is { } count)
            {
                if (count == _rowsCopyKey.Written + 1) _rowsCopyKey = _rowsCopyKey with { Written = count };
                else DropRowsCopy();
            }
        }
    }

    // Under _rowsGate.
    private void DropRowsCopy()
    {
        _rowsCopy = null;
        _staleRows.Clear();
    }

    // A read of the session list; null when SQLite finds its tables damaged: the caller lists the journals again.
    private async Task<T?> TryReadAsync<T>(Func<SqliteConnection, CancellationToken, ValueTask<T>> read, CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        var generation = _database.Generation;
        var epoch = Volatile.Read(ref _schemaEpoch);
        try
        {
            return await _database.ReadAsync(Owner, read, recoverDamagedFile: false, cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException exception) when (ApplicationDatabase.IsDamaged(exception))
        {
            await RepairSchemaAsync(generation, epoch, cancellationToken).ConfigureAwait(false);
            return default;
        }
        catch (SqliteException exception) when (ApplicationDatabase.IsLocked(exception))
        {
            throw CreateLockedException(exception);
        }
        catch (SqliteException) when (_database.Generation != generation)
        {
            // The file was replaced while the read ran: it read a file that has none of these tables.
            return default;
        }
        catch (Exception exception) when (IsFileError(exception))
        {
            throw CreateFileException(exception);
        }
    }

    private async IAsyncEnumerable<AgentSessionCacheProjection> RebuildAndListSessionsAsync(
        AgentSessionCacheProjectionContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await PrepareProgressiveRebuildAsync(cancellationToken).ConfigureAwait(false);

        // The rows are added from here on: tables that are made again before the end lost some of them.
        var epoch = Volatile.Read(ref _schemaEpoch);
        if (!Directory.Exists(context.SessionsRootPath))
        {
            await MarkCacheCompleteAsync(epoch, cancellationToken).ConfigureAwait(false);
            yield break;
        }

        // Listed first, so that what is read is counted against the number of journals.
        var sessionFiles = Directory.GetFiles(context.SessionsRootPath, "*.jsonl", SearchOption.AllDirectories);
        var progress = RebuildProgress;
        for (var index = 0; index < sessionFiles.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var projection = await context.ProjectSessionFileAsync(sessionFiles[index], cancellationToken).ConfigureAwait(false);
            progress?.Invoke(index + 1, sessionFiles.Length);
            if (projection is null)
            {
                continue;
            }

            var hydrated = await HydrateSessionViewDataAsync(projection, cancellationToken).ConfigureAwait(false);
            await UpsertHydratedSessionAsync(hydrated, cancellationToken).ConfigureAwait(false);
            yield return hydrated.ToProjection();
        }

        await MarkCacheCompleteAsync(epoch, cancellationToken).ConfigureAwait(false);
    }

    private Task PrepareProgressiveRebuildAsync(CancellationToken cancellationToken)
        => WriteAsync(
            async (connection, token) =>
            {
                await SetCacheCompleteCoreAsync(connection, complete: false, token).ConfigureAwait(false);
                await ClearSessionRowsCoreAsync(connection, token).ConfigureAwait(false);
            },
            cancellationToken);

    private Task UpsertHydratedSessionAsync(
        SqliteSessionProjection projection,
        CancellationToken cancellationToken)
        => WriteAsync(
            (connection, token) => UpsertSessionCoreAsync(connection, transaction: null, projection, token),
            cancellationToken);

    // epoch: the tables the caller listed the sessions in. The list is called complete only when they are still
    // those tables, in the file they were in: tables that were made again since (a repair, a file that was
    // replaced) lost rows the caller wrote or counted on, and the next listing reads the journals again. The schema
    // gate is held, so no repair runs between the check and the mark.
    private async Task MarkCacheCompleteAsync(int epoch, CancellationToken cancellationToken)
    {
        var generation = _database.Generation;
        var damaged = false;
        await _schemaGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_schemaEpoch != epoch || _schemaGeneration != generation || !File.Exists(_database.DatabasePath))
            {
                return;
            }

            await WriteCoreAsync(
                    (connection, token) => SetCacheCompleteCoreAsync(connection, complete: true, token),
                    schemaOwned: true,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (SqliteException exception) when (ApplicationDatabase.IsDamaged(exception))
        {
            damaged = true;
        }
        catch (SqliteException) when (_database.Generation != generation)
        {
            // The file was replaced under the mark: the new one has no mark, or the mark of its copy.
        }
        finally
        {
            _schemaGate.Release();
        }

        if (damaged)
        {
            await RepairSchemaAsync(generation, epoch, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<bool> IsCacheCompleteAsync(CancellationToken cancellationToken)
        => await TryReadAsync(
                async (connection, token) =>
                {
                    await using var command = connection.CreateCommand();
                    command.CommandText = "SELECT value FROM session_projection_cache_metadata WHERE key = $key LIMIT 1;";
                    AddParameter(command, "$key", CacheCompleteMetadataKey);
                    var value = await command.ExecuteScalarAsync(token).ConfigureAwait(false) as string;
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        return string.Equals(value, CacheCompleteValue, StringComparison.Ordinal);
                    }

                    return await SessionRowCountCoreAsync(connection, token).ConfigureAwait(false) > 0;
                },
                cancellationToken)
            .ConfigureAwait(false);

    private async Task<IReadOnlyList<SqliteSessionProjectionRow>> QuerySessionRowsAsync(
        string? sessionId,
        CancellationToken cancellationToken)
        => await TryQuerySessionRowsAsync(sessionId, cancellationToken).ConfigureAwait(false) ?? [];

    private async Task<IReadOnlyList<SqliteSessionProjectionRow>?> TryQuerySessionRowsAsync(
        string? sessionId,
        CancellationToken cancellationToken)
    {
        if (sessionId is not null)
        {
            // One row is a few pages: it is read from the database, with the state of its provider.
            return await TryReadAsync<IReadOnlyList<SqliteSessionProjectionRow>?>(
                    async (connection, token) => await QueryRowsCoreAsync(connection, [sessionId.Trim()], token, withState: true).ConfigureAwait(false),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        // The list: from the copy in memory, with only the rows this instance wrote since read again; whole when the copy is
        // not that of the rows as they are (another writer, other tables, another file).
        Dictionary<string, SqliteSessionProjectionRow>? copy;
        (int Generation, int Epoch, long Written) copyKey;
        string[] stale;
        long changes;
        var generation = _database.Generation;
        var epoch = Volatile.Read(ref _schemaEpoch);
        lock (_rowsGate)
        {
            copy = _rowsCopy;
            copyKey = _rowsCopyKey;
            stale = [.. _staleRows];
            changes = _rowsChanges;
        }

        var read = await TryReadAsync<(long Written, IReadOnlyList<SqliteSessionProjectionRow> Rows, bool Whole)?>(
                async (connection, token) =>
                {
                    // The number of row writes and the rows are read from one snapshot of the database.
                    using var transaction = connection.BeginTransaction(deferred: true);
                    var written = await ReadRowsWrittenCoreAsync(connection, token).ConfigureAwait(false);
                    if (copy is not null && copyKey == (generation, epoch, written))
                    {
                        return (written, stale.Length == 0 ? [] : await QueryRowsCoreAsync(connection, stale, token).ConfigureAwait(false), false);
                    }

                    return (written, await QueryRowsCoreAsync(connection, sessionIds: null, token).ConfigureAwait(false), true);
                },
                cancellationToken)
            .ConfigureAwait(false);
        if (read is not { } result)
        {
            return null;
        }

        Dictionary<string, SqliteSessionProjectionRow> rows;
        if (result.Whole)
        {
            rows = new Dictionary<string, SqliteSessionProjectionRow>(result.Rows.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var row in result.Rows) rows[row.Summary.SessionId.Trim()] = row;
        }
        else
        {
            rows = new Dictionary<string, SqliteSessionProjectionRow>(copy!, StringComparer.OrdinalIgnoreCase);
            foreach (var id in stale) rows.Remove(id);
            foreach (var row in result.Rows) rows[row.Summary.SessionId.Trim()] = row;
        }

        lock (_rowsGate)
        {
            // Kept only when no write of this instance began or ended meanwhile: a write that did is read again next time.
            if (_rowsChanges == changes && _database.Generation == generation && Volatile.Read(ref _schemaEpoch) == epoch)
            {
                _rowsCopy = rows;
                _rowsCopyKey = (generation, epoch, result.Written);
                _staleRows.ExceptWith(stale);
            }
        }

        return result.Whole ? result.Rows : OrderRows(rows.Values);
    }

    // The list in the order of the query of the whole list: the most recently updated first, then by session without case.
    private static List<SqliteSessionProjectionRow> OrderRows(IEnumerable<SqliteSessionProjectionRow> rows)
        => [.. rows.OrderByDescending(static row => row.Summary.UpdatedAt.UtcTicks)
            .ThenByDescending(static row => row.Summary.SessionId.Trim(), NoCaseComparer.Instance)];

    // The rows of some sessions, or of all (sessionIds null, in the order of the list).
    // withState: the rows are those of single sessions, given with the state of their provider (session_projection_cache_detail);
    // the rows of the list are given with a state that names only the session of the provider.
    private async ValueTask<IReadOnlyList<SqliteSessionProjectionRow>> QueryRowsCoreAsync(
        SqliteConnection connection,
        IReadOnlyList<string>? sessionIds,
        CancellationToken cancellationToken,
        bool withState = false)
    {
        await using var command = connection.CreateCommand();
        if (sessionIds is null)
        {
            command.CommandText = $"""
                SELECT {ListColumns}
                FROM session_projection_cache
                ORDER BY updated_at_utc_ticks DESC, session_id COLLATE NOCASE DESC;
                """;
        }
        else
        {
            var names = new string[sessionIds.Count];
            for (var index = 0; index < sessionIds.Count; index++)
            {
                names[index] = "$session_" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
                AddParameter(command, names[index], sessionIds[index].Trim());
            }

            command.CommandText = withState
                ? $"""
                    SELECT {QualifiedListColumns}, d.state_json
                    FROM session_projection_cache c
                    LEFT JOIN session_projection_cache_detail d ON d.session_id = c.session_id
                    WHERE c.session_id IN ({string.Join(", ", names)});
                    """
                : $"SELECT {ListColumns} FROM session_projection_cache WHERE session_id IN ({string.Join(", ", names)});";
        }

        var rows = new List<SqliteSessionProjectionRow>();
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            Interlocked.Increment(ref _rowReadCount);
            var row = ReadRow(reader, withState);
            if (row is not null)
            {
                rows.Add(row);
            }
        }

        return rows;
    }

    private static async Task<long> ReadRowsWrittenCoreAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT CAST(value AS INTEGER) FROM session_projection_cache_metadata WHERE key = $key;";
        AddParameter(command, "$key", RowsWrittenMetadataKey);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is long written ? written : 0;
    }

    // The order of SQLite's NOCASE: ASCII letters compared without case, every other character as it is.
    private sealed class NoCaseComparer : IComparer<string>
    {
        public static readonly NoCaseComparer Instance = new();

        public int Compare(string? x, string? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return -1;
            if (y is null) return 1;
            var length = Math.Min(x.Length, y.Length);
            for (var index = 0; index < length; index++)
            {
                var left = Lower(x[index]);
                var right = Lower(y[index]);
                if (left != right) return left.CompareTo(right);
            }

            return x.Length.CompareTo(y.Length);
        }

        private static char Lower(char value) => value is >= 'A' and <= 'Z' ? (char)(value + 32) : value;
    }

    private async Task<int> PruneRowsMissingFromDiskAsync(
        HashSet<string> journalPathsOnDisk,
        CancellationToken cancellationToken)
    {
        var rows = await QuerySessionRowsAsync(sessionId: null, cancellationToken).ConfigureAwait(false);
        var removed = 0;
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pathKey = NormalizePathKey(row.JournalPath);
            if ((journalPathsOnDisk.Count > 0 && journalPathsOnDisk.Contains(pathKey)) || File.Exists(row.JournalPath))
            {
                continue;
            }

            await RemoveSessionAsync(row.Summary.SessionId, cancellationToken).ConfigureAwait(false);
            removed++;
        }

        return removed;
    }

    private async Task<SqliteSessionProjection> HydrateSessionViewDataAsync(
        AgentSessionCacheProjection projection,
        CancellationToken cancellationToken)
    {
        var key = NormalizePathKey(projection.JournalPath);
        if (projection.ViewState is null &&
            projection.ContinuesFrom is { } continuesFrom)
        {
            // The journal is the one the view was read from, followed by records that change neither its header nor its
            // view state: the view kept in memory, or else the one its row holds, is the view of the journal now.
            if (_views.TryGetValue(key, out var view) && view.Stamp == continuesFrom)
            {
                _views.TryUpdate(key, view with { Stamp = projection.Stamp }, view);
                return new SqliteSessionProjection(projection, view.Header, view.LocalState, HeaderRead: true, view.LocalStateJson);
            }

            if (await TryReadViewOfRowAsync(projection, continuesFrom, cancellationToken).ConfigureAwait(false) is { } rowView)
            {
                _views[key] = rowView;
                return new SqliteSessionProjection(projection, rowView.Header, rowView.LocalState, HeaderRead: true, rowView.LocalStateJson);
            }
        }

        Interlocked.Increment(ref _journalViewReadCount);
        var before = TryGetStamp(projection.JournalPath);
        var (headerRead, header) = await TryReadHeaderAsync(projection.JournalPath, cancellationToken).ConfigureAwait(false);
        AgentSessionViewStateMetadata? localState = projection.ViewState;
        string? localStateJson = null;
        var stateRead = true;
        if (localState is null)
        {
            var viewState = await TryReadLatestStateAsync(projection.JournalPath, cancellationToken).ConfigureAwait(false);
            stateRead = viewState.ReadSucceeded;
            localState = viewState.ReadSucceeded
                ? ToAgentLocalState(viewState.State) ?? new AgentSessionViewStateMetadata { QueuedPromptCount = 0 }
                : null;
            localStateJson = viewState.ReadSucceeded ? SerializeLocalState(viewState.State) : null;
        }

        // Kept only for a view read whole from the journal at the stamp of the projection, and not given by the projection.
        if (projection.ViewState is null && headerRead && stateRead &&
            before == projection.Stamp && TryGetStamp(projection.JournalPath) == projection.Stamp)
        {
            _views[key] = new HydratedView(projection.Summary.SessionId, projection.Stamp, header, localState, localStateJson);
        }
        else
        {
            _views.TryRemove(key, out _);
            // The state read may be the one of a later stamp: the row does not say it is the state of this one.
            localStateJson = null;
        }

        // The header kept by the row is the one of the journal at the stamp of the projection: a journal that changed
        // since is projected again by the next listing, which reads its header again.
        return new SqliteSessionProjection(projection, header, localState, headerRead, localStateJson);
    }

    // The view the row of a session holds, when the row is the projection of its journal at the given stamp and holds its
    // header and its last state; null otherwise (the journal is read).
    private async Task<HydratedView?> TryReadViewOfRowAsync(AgentSessionCacheProjection projection, AgentSessionCacheFileStamp stamp,
        CancellationToken cancellationToken)
    {
        SessionViewStateRow? row;
        try
        {
            row = await TryReadSessionStateRowAsync(projection.Summary.SessionId, cancellationToken).ConfigureAwait(false);
        }
        catch (AgentSessionCacheLockedException)
        {
            return null;
        }

        if (row is not { HeaderRead: true, LocalStateJson: { } json } || row.Stamp != stamp
            || !string.Equals(NormalizePathKey(row.JournalPath), NormalizePathKey(projection.JournalPath), StringComparison.OrdinalIgnoreCase)
            || !TryDeserializeLocalState(json, out var state))
        {
            return null;
        }

        var header = SessionViewJournalHeader.FromJson(row.HeaderJson);
        if (row.HeaderJson is not null && header is null)
        {
            return null;
        }

        return new HydratedView(projection.Summary.SessionId, projection.Stamp, header,
            ToAgentLocalState(state) ?? new AgentSessionViewStateMetadata { QueuedPromptCount = 0 }, json);
    }

    // The last state of a journal as the row keeps it: the JSON of the state record trimmed as the journal keeps it
    // (SessionViewJournalStore.CompactForJournal: a record of an older journal holds every handled prompt whole), or the JSON
    // null for a journal without one.
    internal static string SerializeLocalState(SessionViewLocalState? state)
        => state is null
            ? "null"
            : JsonSerializer.Serialize(SessionViewJournalStore.CompactForJournal(state), SessionViewJournalJsonSerializerContext.Default.SessionViewLocalState);

    internal static bool TryDeserializeLocalState(string json, out SessionViewLocalState? state)
    {
        try
        {
            state = JsonSerializer.Deserialize(json, SessionViewJournalJsonSerializerContext.Default.SessionViewLocalState);
            return true;
        }
        catch (JsonException)
        {
            state = null;
            return false;
        }
    }

    // The view of a journal a listing projected again; null when its header or view state cannot be read or understood:
    // the listing then gives the projection without them, as it is, and writes nothing.
    private async Task<SqliteSessionProjection?> TryHydrateAsync(AgentSessionCacheProjection projection, CancellationToken cancellationToken)
    {
        try
        {
            return await HydrateSessionViewDataAsync(projection, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    private static async Task<(bool Read, SessionViewJournalHeader? Header)> TryReadHeaderAsync(string journalPath, CancellationToken cancellationToken)
    {
        try
        {
            return (true, await SessionViewJournalStore.ReadHeaderFromPathAsync(journalPath, cancellationToken).ConfigureAwait(false));
        }
        catch (IOException) when (!cancellationToken.IsCancellationRequested)
        {
            return (false, null);
        }
    }

    // The header of a journal as the row keeps it; not read when the journal cannot be read or its header not understood.
    private static async Task<(bool Read, string? Json)> TryReadHeaderJsonAsync(string journalPath, CancellationToken cancellationToken)
    {
        try
        {
            var (read, header) = await TryReadHeaderAsync(journalPath, cancellationToken).ConfigureAwait(false);
            return (read, header?.ToJson());
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or JsonException && !cancellationToken.IsCancellationRequested)
        {
            return (false, null);
        }
    }

    private static async Task<(bool ReadSucceeded, SessionViewLocalState? State)> TryReadLatestStateAsync(
        string journalPath,
        CancellationToken cancellationToken)
    {
        try
        {
            return (true, await SessionViewJournalStore.ReadLatestStateFromPathAsync(journalPath, cancellationToken).ConfigureAwait(false));
        }
        catch (IOException) when (!cancellationToken.IsCancellationRequested)
        {
            return (false, null);
        }
    }

    private static async Task EnsureSchemaCoreAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS session_projection_cache (
                    session_id TEXT NOT NULL PRIMARY KEY COLLATE NOCASE,
                    journal_path TEXT NOT NULL UNIQUE COLLATE NOCASE,
                    journal_last_write_utc_ticks INTEGER NOT NULL,
                    journal_length INTEGER NOT NULL,
                    cache_updated_utc_ticks INTEGER NOT NULL,
                    projection_version INTEGER NOT NULL,
                    created_at_utc_ticks INTEGER NOT NULL,
                    updated_at_utc_ticks INTEGER NOT NULL,
                    protocol_family TEXT,
                    provider_id TEXT,
                    provider_key TEXT,
                    working_directory TEXT,
                    title TEXT,
                    summary TEXT,
                    model_id TEXT,
                    reasoning_effort TEXT,
                    agent_prompt_id TEXT,
                    parent_session_id TEXT,
                    created_by_session_id TEXT,
                    created_by_run_id TEXT,
                    provider_session_id TEXT,
                    summary_json TEXT NOT NULL,
                    kind TEXT,
                    project_ref TEXT,
                    local_provider_key TEXT,
                    local_model_id TEXT,
                    local_reasoning_effort TEXT,
                    local_agent_prompt_id TEXT,
                    local_permission_mode TEXT,
                    local_title_mode TEXT,
                    archived INTEGER NOT NULL DEFAULT 0,
                    local_archived_at_utc_ticks INTEGER,
                    local_queued_prompt_count INTEGER,
                    message_count INTEGER,
                    local_parent_session_id TEXT,
                    created_by_json TEXT,
                    local_state_cached INTEGER NOT NULL DEFAULT 0,
                    space_id TEXT,
                    journal_header_json TEXT,
                    journal_header_read INTEGER NOT NULL DEFAULT 0
                );
                """;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var command = connection.CreateCommand())
        {
            // The larger values of a session, read for that session only: the state of its provider (state_json) and the last
            // state record of its journal, trimmed as the journal keeps it (local_state_json; null when not known).
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS session_projection_cache_detail (
                    session_id TEXT NOT NULL PRIMARY KEY COLLATE NOCASE,
                    state_json TEXT,
                    local_state_json TEXT
                );
                """;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS session_projection_cache_metadata (
                    key TEXT NOT NULL PRIMARY KEY COLLATE NOCASE,
                    value TEXT NOT NULL
                );
                """;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await AddPermissionModeColumnAsync(connection, cancellationToken).ConfigureAwait(false);
        await AddTitleModeColumnAsync(connection, cancellationToken).ConfigureAwait(false);
        await AddSpaceColumnAsync(connection, cancellationToken).ConfigureAwait(false);
        await AddArchivedAtColumnAsync(connection, cancellationToken).ConfigureAwait(false);
        await AddJournalHeaderColumnsAsync(connection, cancellationToken).ConfigureAwait(false);
        await AddQueuedPromptCountColumnAsync(connection, cancellationToken).ConfigureAwait(false);
        await MoveLargeValuesAsync(connection, cancellationToken).ConfigureAwait(false);
        await ExecuteSchemaCommandAsync(connection, "CREATE INDEX IF NOT EXISTS ix_session_projection_cache_updated ON session_projection_cache(updated_at_utc_ticks DESC);", cancellationToken).ConfigureAwait(false);
        await ExecuteSchemaCommandAsync(connection, "CREATE INDEX IF NOT EXISTS ix_session_projection_cache_working_directory ON session_projection_cache(working_directory COLLATE NOCASE);", cancellationToken).ConfigureAwait(false);
        await ExecuteSchemaCommandAsync(connection, "CREATE INDEX IF NOT EXISTS ix_session_projection_cache_project_ref ON session_projection_cache(project_ref COLLATE NOCASE);", cancellationToken).ConfigureAwait(false);
        await ExecuteSchemaCommandAsync(connection, "CREATE UNIQUE INDEX IF NOT EXISTS ux_session_projection_cache_journal_path ON session_projection_cache(journal_path COLLATE NOCASE);", cancellationToken).ConfigureAwait(false);
    }

    // A cache written before the permission mode of a session was kept has no column for it, and rows that do not
    // say it: the column is added and the rows are read again from the journals. It runs in the write transaction
    // of the migration, which holds the write lock: no other connection adds the column at the same time.
    private static async Task AddPermissionModeColumnAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('session_projection_cache') WHERE name = 'local_permission_mode';";
            if (Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) != 0)
            {
                return;
            }
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                ALTER TABLE session_projection_cache ADD COLUMN local_permission_mode TEXT;
                DELETE FROM session_projection_cache;
                """;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await SetCacheCompleteCoreAsync(connection, complete: false, cancellationToken).ConfigureAwait(false);
    }

    // A cache written before a session could choose how it is titled has no column for it. Its rows stay, without
    // a choice: the sessions of such a build made none, and a row is read again when its journal changes.
    private static async Task AddTitleModeColumnAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('session_projection_cache') WHERE name = 'local_title_mode';";
            if (Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) != 0)
            {
                return;
            }
        }

        await ExecuteSchemaCommandAsync(connection, "ALTER TABLE session_projection_cache ADD COLUMN local_title_mode TEXT;", cancellationToken).ConfigureAwait(false);
    }

    // A cache written before sessions had a space has no column for it. Its rows are read again from the journals:
    // a build without the column may have cached a session whose header records a space, and the row of such a
    // session would say none until its journal changes. It runs in the write transaction of the migration.
    private static async Task AddSpaceColumnAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('session_projection_cache') WHERE name = 'space_id';";
            if (Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) != 0)
            {
                return;
            }
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                ALTER TABLE session_projection_cache ADD COLUMN space_id TEXT;
                DELETE FROM session_projection_cache;
                """;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await SetCacheCompleteCoreAsync(connection, complete: false, cancellationToken).ConfigureAwait(false);
    }

    // A cache written before the time a session was archived was kept has no column for it. Its rows stay, without
    // a time: a session archived by such a build has none, and a row is read again when its journal changes.
    private static async Task AddArchivedAtColumnAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('session_projection_cache') WHERE name = 'local_archived_at_utc_ticks';";
            if (Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) != 0)
            {
                return;
            }
        }

        await ExecuteSchemaCommandAsync(connection, "ALTER TABLE session_projection_cache ADD COLUMN local_archived_at_utc_ticks INTEGER;", cancellationToken).ConfigureAwait(false);
    }

    // A cache written before the list kept the header of each journal has no column for it. Its rows stay, with a header
    // not read: the next listing reads the first line of each of their journals once, and writes what it read.
    private static async Task AddJournalHeaderColumnsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('session_projection_cache') WHERE name = 'journal_header_read';";
            if (Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) != 0)
            {
                return;
            }
        }

        await ExecuteSchemaCommandAsync(connection, """
            ALTER TABLE session_projection_cache ADD COLUMN journal_header_json TEXT;
            ALTER TABLE session_projection_cache ADD COLUMN journal_header_read INTEGER NOT NULL DEFAULT 0;
            """, cancellationToken).ConfigureAwait(false);
    }

    // A cache written before the count of the pending queued prompts of a session was kept has no column for it. Its rows
    // stay, with a count that is not known: the list reads no journal for it, and the first read of the last state of a
    // journal writes it (FillLocalStateAsync), as a row is written again when its journal changes.
    private static async Task AddQueuedPromptCountColumnAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('session_projection_cache') WHERE name = 'local_queued_prompt_count';";
            if (Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) != 0)
            {
                return;
            }
        }

        await ExecuteSchemaCommandAsync(connection, "ALTER TABLE session_projection_cache ADD COLUMN local_queued_prompt_count INTEGER;", cancellationToken).ConfigureAwait(false);
    }

    // Tables written before version 8 kept the state of the provider (state_json) and, from version 7, the last state record
    // of the journal (local_state_json) in the rows of the list, and the whole last answer twice (summary, and in
    // summary_json). The states move to session_projection_cache_detail, the last state trimmed as the journal keeps it; the
    // answer is cut to its preview and left out of summary_json; the columns are dropped. Rows without a last state (before
    // version 7) stay without it: the first read of the state of such a session reads its journal once and fills it
    // (FillLocalStateAsync). It runs in the write transaction of the migration.
    private static async Task MoveLargeValuesAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT name FROM pragma_table_info('session_projection_cache');";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                columns.Add(reader.GetString(0));
            }
        }

        var hasState = columns.Contains("state_json");
        var hasLocalState = columns.Contains("local_state_json");
        if (!hasState && !hasLocalState)
        {
            return;
        }

        var rows = new List<(string SessionId, string? Summary, string? SummaryJson, string? State, string? LocalState)>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = $"""
                SELECT session_id, summary, summary_json,
                       {(hasState ? "state_json" : "NULL")},
                       {(hasLocalState ? "CASE WHEN local_state_cached = 1 THEN local_state_json END" : "NULL")}
                FROM session_projection_cache;
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add((reader.GetString(0), NullableString(reader, 1), NullableString(reader, 2), NullableString(reader, 3), NullableString(reader, 4)));
            }
        }

        foreach (var row in rows)
        {
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    INSERT INTO session_projection_cache_detail (session_id, state_json, local_state_json)
                    VALUES ($session_id, $state_json, $local_state_json)
                    ON CONFLICT(session_id) DO UPDATE SET state_json = excluded.state_json, local_state_json = excluded.local_state_json;
                    """;
                AddParameter(command, "$session_id", row.SessionId);
                AddParameter(command, "$state_json", row.State);
                AddParameter(command, "$local_state_json", row.LocalState is null ? null : TrimLocalStateJson(row.LocalState));
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "UPDATE session_projection_cache SET summary = $summary, summary_json = $summary_json WHERE session_id = $session_id;";
                AddParameter(command, "$session_id", row.SessionId);
                AddParameter(command, "$summary", SummaryPreview(row.Summary));
                AddParameter(command, "$summary_json", row.SummaryJson is null ? string.Empty : SummaryJsonWithoutAnswer(row.SummaryJson));
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        if (hasState)
        {
            await ExecuteSchemaCommandAsync(connection, "ALTER TABLE session_projection_cache DROP COLUMN state_json;", cancellationToken).ConfigureAwait(false);
        }

        if (hasLocalState)
        {
            await ExecuteSchemaCommandAsync(connection, "ALTER TABLE session_projection_cache DROP COLUMN local_state_json;", cancellationToken).ConfigureAwait(false);
        }

        static string? NullableString(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    // A last state record as the row keeps it, trimmed as the journal keeps it; null when it cannot be read (the first read
    // of the state of the session then reads its journal).
    private static string? TrimLocalStateJson(string json)
        => TryDeserializeLocalState(json, out var state) ? SerializeLocalState(state) : null;

    // The preview of the last answer of a session the row keeps: its first MaximumSummaryPreviewLength characters.
    internal static string? SummaryPreview(string? summary)
    {
        var text = NormalizeOptionalText(summary);
        if (text is null || text.Length <= MaximumSummaryPreviewLength)
        {
            return text;
        }

        var length = MaximumSummaryPreviewLength - 1;
        if (char.IsHighSurrogate(text[length - 1]))
        {
            length--;
        }

        return string.Concat(text.AsSpan(0, length).TrimEnd(), "…");
    }

    // The summary of a session as summary_json keeps it: without the last answer, which the summary column keeps as a preview.
    private static string SummaryJsonWithoutAnswer(string json)
    {
        try
        {
            return JsonSerializer.Deserialize(json, AgentJsonSerializerContext.Default.AgentSessionSummary) is { } summary
                ? JsonSerializer.Serialize(summary with { Summary = null }, AgentJsonSerializerContext.Default.AgentSessionSummary)
                : json;
        }
        catch (JsonException)
        {
            return json;
        }
    }

    private static string? FormatTitleMode(string? name)
        => AgentSessionTitles.ParseMode(name) is { } mode ? AgentSessionTitles.NameOf(mode) : null;

    private static async Task ExecuteSchemaCommandAsync(
        SqliteConnection connection,
        string commandText,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ClearSessionRowsCoreAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM session_projection_cache; DELETE FROM session_projection_cache_detail;";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> SessionRowCountCoreAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM session_projection_cache;";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
    }

    // Which copy the file is, and which copy it was when the rows were last known to be its own. Runs with the schema gate
    // held, as a part of making the schema: a damage it meets is handled there.
    private async Task<(int Number, int Seen)> ReadCopyAsync(CancellationToken cancellationToken)
        => await _database.ReadAsync(
                Owner,
                async (connection, token) =>
                {
                    var number = await ApplicationDatabase.ReadCopyNumberAsync(connection, token).ConfigureAwait(false);
                    await using var command = connection.CreateCommand();
                    command.CommandText = "SELECT value FROM session_projection_cache_metadata WHERE key = $key LIMIT 1;";
                    AddParameter(command, "$key", CopySeenMetadataKey);
                    var seen = await command.ExecuteScalarAsync(token).ConfigureAwait(false) as string;
                    return (number, int.TryParse(seen, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : 0);
                },
                recoverDamagedFile: false,
                cancellationToken)
            .ConfigureAwait(false);

    // Records, in the write that marks the list incomplete, which copy the file is: the rows are those of this file from now on.
    private static async Task SetCopySeenCoreAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var number = await ApplicationDatabase.ReadCopyNumberAsync(connection, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO session_projection_cache_metadata (key, value)
            VALUES ($key, $value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """;
        AddParameter(command, "$key", CopySeenMetadataKey);
        AddParameter(command, "$value", number.ToString(System.Globalization.CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task SetCacheCompleteCoreAsync(
        SqliteConnection connection,
        bool complete,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO session_projection_cache_metadata (key, value)
            VALUES ($key, $value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """;
        AddParameter(command, "$key", CacheCompleteMetadataKey);
        AddParameter(command, "$value", complete ? CacheCompleteValue : CacheIncompleteValue);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task UpsertSessionCoreAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        SqliteSessionProjection projection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO session_projection_cache (
                session_id,
                journal_path,
                journal_last_write_utc_ticks,
                journal_length,
                cache_updated_utc_ticks,
                projection_version,
                created_at_utc_ticks,
                updated_at_utc_ticks,
                protocol_family,
                provider_id,
                provider_key,
                working_directory,
                title,
                summary,
                model_id,
                reasoning_effort,
                agent_prompt_id,
                parent_session_id,
                created_by_session_id,
                created_by_run_id,
                provider_session_id,
                summary_json,
                kind,
                project_ref,
                local_provider_key,
                local_model_id,
                local_reasoning_effort,
                local_agent_prompt_id,
                local_permission_mode,
                local_title_mode,
                archived,
                local_archived_at_utc_ticks,
                local_queued_prompt_count,
                message_count,
                local_parent_session_id,
                created_by_json,
                local_state_cached,
                space_id,
                journal_header_json,
                journal_header_read)
            VALUES (
                $session_id,
                $journal_path,
                $journal_last_write_utc_ticks,
                $journal_length,
                $cache_updated_utc_ticks,
                $projection_version,
                $created_at_utc_ticks,
                $updated_at_utc_ticks,
                $protocol_family,
                $provider_id,
                $provider_key,
                $working_directory,
                $title,
                $summary,
                $model_id,
                $reasoning_effort,
                $agent_prompt_id,
                $parent_session_id,
                $created_by_session_id,
                $created_by_run_id,
                $provider_session_id,
                $summary_json,
                $kind,
                $project_ref,
                $local_provider_key,
                $local_model_id,
                $local_reasoning_effort,
                $local_agent_prompt_id,
                $local_permission_mode,
                $local_title_mode,
                $archived,
                $local_archived_at_utc_ticks,
                $local_queued_prompt_count,
                $message_count,
                $local_parent_session_id,
                $created_by_json,
                $local_state_cached,
                $space_id,
                $journal_header_json,
                $journal_header_read)
            ON CONFLICT(session_id) DO UPDATE SET
                journal_path = excluded.journal_path,
                journal_last_write_utc_ticks = excluded.journal_last_write_utc_ticks,
                journal_length = excluded.journal_length,
                cache_updated_utc_ticks = excluded.cache_updated_utc_ticks,
                projection_version = excluded.projection_version,
                created_at_utc_ticks = excluded.created_at_utc_ticks,
                updated_at_utc_ticks = excluded.updated_at_utc_ticks,
                protocol_family = excluded.protocol_family,
                provider_id = excluded.provider_id,
                provider_key = excluded.provider_key,
                working_directory = excluded.working_directory,
                title = excluded.title,
                summary = excluded.summary,
                model_id = excluded.model_id,
                reasoning_effort = excluded.reasoning_effort,
                agent_prompt_id = excluded.agent_prompt_id,
                parent_session_id = excluded.parent_session_id,
                created_by_session_id = excluded.created_by_session_id,
                created_by_run_id = excluded.created_by_run_id,
                provider_session_id = excluded.provider_session_id,
                summary_json = excluded.summary_json,
                kind = excluded.kind,
                project_ref = excluded.project_ref,
                space_id = excluded.space_id,
                journal_header_json = excluded.journal_header_json,
                journal_header_read = excluded.journal_header_read,
                local_provider_key = CASE WHEN excluded.local_state_cached = 1 THEN excluded.local_provider_key ELSE session_projection_cache.local_provider_key END,
                local_model_id = CASE WHEN excluded.local_state_cached = 1 THEN excluded.local_model_id ELSE session_projection_cache.local_model_id END,
                local_reasoning_effort = CASE WHEN excluded.local_state_cached = 1 THEN excluded.local_reasoning_effort ELSE session_projection_cache.local_reasoning_effort END,
                local_agent_prompt_id = CASE WHEN excluded.local_state_cached = 1 THEN excluded.local_agent_prompt_id ELSE session_projection_cache.local_agent_prompt_id END,
                local_permission_mode = CASE WHEN excluded.local_state_cached = 1 THEN excluded.local_permission_mode ELSE session_projection_cache.local_permission_mode END,
                local_title_mode = CASE WHEN excluded.local_state_cached = 1 THEN excluded.local_title_mode ELSE session_projection_cache.local_title_mode END,
                archived = CASE WHEN excluded.local_state_cached = 1 THEN excluded.archived ELSE session_projection_cache.archived END,
                local_archived_at_utc_ticks = CASE WHEN excluded.local_state_cached = 1 THEN excluded.local_archived_at_utc_ticks ELSE session_projection_cache.local_archived_at_utc_ticks END,
                local_queued_prompt_count = CASE WHEN excluded.local_state_cached = 1 THEN excluded.local_queued_prompt_count ELSE session_projection_cache.local_queued_prompt_count END,
                message_count = CASE WHEN excluded.local_state_cached = 1 THEN excluded.message_count ELSE session_projection_cache.message_count END,
                local_parent_session_id = CASE WHEN excluded.local_state_cached = 1 THEN excluded.local_parent_session_id ELSE session_projection_cache.local_parent_session_id END,
                created_by_json = CASE WHEN excluded.local_state_cached = 1 THEN excluded.created_by_json ELSE session_projection_cache.created_by_json END,
                local_state_cached = CASE WHEN excluded.local_state_cached = 1 THEN 1 ELSE session_projection_cache.local_state_cached END;

            -- A projection whose view state could not be read says nothing of the last state at its stamp.
            INSERT INTO session_projection_cache_detail (session_id, state_json, local_state_json)
            VALUES ($session_id, $state_json, $local_state_json)
            ON CONFLICT(session_id) DO UPDATE SET state_json = excluded.state_json, local_state_json = excluded.local_state_json;
            """;

        var summary = projection.Projection.Summary;
        var state = projection.Projection.State;
        var localState = projection.LocalState;
        AddParameter(command, "$session_id", summary.SessionId);
        AddParameter(command, "$journal_path", Path.GetFullPath(projection.Projection.JournalPath));
        AddParameter(command, "$journal_last_write_utc_ticks", projection.Projection.Stamp.LastWriteTimeUtc.Ticks);
        AddParameter(command, "$journal_length", projection.Projection.Stamp.Length);
        AddParameter(command, "$cache_updated_utc_ticks", DateTimeOffset.UtcNow.UtcTicks);
        AddParameter(command, "$projection_version", ProjectionVersion);
        AddParameter(command, "$created_at_utc_ticks", summary.CreatedAt.UtcTicks);
        AddParameter(command, "$updated_at_utc_ticks", summary.UpdatedAt.UtcTicks);
        AddParameter(command, "$protocol_family", NormalizeOptionalText(summary.ProtocolFamily));
        AddParameter(command, "$provider_id", NormalizeOptionalText(summary.ProviderId.Value));
        AddParameter(command, "$provider_key", NormalizeOptionalText(summary.ProviderKey));
        AddParameter(command, "$working_directory", NormalizeOptionalText(summary.WorkingDirectory));
        AddParameter(command, "$title", NormalizeOptionalText(summary.Title));
        AddParameter(command, "$summary", SummaryPreview(summary.Summary));
        AddParameter(command, "$model_id", NormalizeOptionalText(summary.ModelId));
        AddParameter(command, "$reasoning_effort", FormatReasoningEffort(summary.ReasoningEffort));
        AddParameter(command, "$agent_prompt_id", NormalizeOptionalText(summary.AgentPromptId));
        AddParameter(command, "$parent_session_id", NormalizeOptionalText(summary.ParentSessionId));
        AddParameter(command, "$created_by_session_id", NormalizeOptionalText(summary.CreatedBySessionId));
        AddParameter(command, "$created_by_run_id", NormalizeOptionalText(summary.CreatedByRunId?.Value));
        AddParameter(command, "$provider_session_id", NormalizeOptionalText(state?.ProviderSessionId));
        // The answer is kept once, as the preview of the summary column.
        AddParameter(command, "$summary_json", JsonSerializer.Serialize(summary with { Summary = null }, AgentJsonSerializerContext.Default.AgentSessionSummary));
        AddParameter(command, "$state_json", state is null ? null : JsonSerializer.Serialize(state, AgentJsonSerializerContext.Default.AgentSessionState));
        AddParameter(command, "$kind", projection.Header?.Kind.ToString());
        AddParameter(command, "$project_ref", NormalizeOptionalText(projection.Header?.ProjectRef));
        AddParameter(command, "$local_provider_key", NormalizeOptionalText(localState?.ProviderKey));
        AddParameter(command, "$local_model_id", NormalizeOptionalText(localState?.ModelId));
        AddParameter(command, "$local_reasoning_effort", FormatReasoningEffort(localState?.ReasoningEffort));
        AddParameter(command, "$local_agent_prompt_id", NormalizeOptionalText(localState?.AgentPromptId));
        AddParameter(command, "$local_permission_mode", NormalizeOptionalText(localState?.PermissionMode));
        AddParameter(command, "$local_title_mode", localState?.TitleMode is { } titleMode ? AgentSessionTitles.NameOf(titleMode) : null);
        AddParameter(command, "$archived", localState?.Archived == true ? 1 : 0);
        AddParameter(command, "$local_archived_at_utc_ticks", localState?.Archived == true ? localState.ArchivedAt?.UtcTicks : null);
        AddParameter(command, "$local_queued_prompt_count", localState?.QueuedPromptCount);
        AddParameter(command, "$message_count", localState?.MessageCount);
        AddParameter(command, "$local_parent_session_id", NormalizeOptionalText(localState?.ParentSessionId));
        AddParameter(command, "$created_by_json", NormalizeOptionalText(localState?.CreatedByJson));
        AddParameter(command, "$local_state_cached", localState is null ? 0 : 1);
        AddParameter(command, "$space_id", NormalizeOptionalText(projection.Header?.SpaceId ?? projection.Projection.SpaceId));
        AddParameter(command, "$journal_header_json", projection.HeaderJson);
        AddParameter(command, "$journal_header_read", projection.HeaderRead ? 1 : 0);
        AddParameter(command, "$local_state_json", localState is null ? null : projection.LocalStateJson);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    // withState: the reader has the state_json of the provider (a lookup of one session); otherwise the state names only the
    // session of the provider, which is all a listing gives of it.
    private static SqliteSessionProjectionRow? ReadRow(SqliteDataReader reader, bool withState)
    {
        var summaryJson = GetString(reader, "summary_json");
        if (string.IsNullOrWhiteSpace(summaryJson))
        {
            return null;
        }

        AgentSessionSummary? summary;
        try
        {
            // summary_json leaves out the answer, which the summary column keeps as a preview.
            summary = JsonSerializer.Deserialize(summaryJson, AgentJsonSerializerContext.Default.AgentSessionSummary) is { } read
                ? read with { Summary = GetString(reader, "summary") }
                : null;
        }
        catch (JsonException)
        {
            summary = null;
        }

        if (summary is null)
        {
            summary = new AgentSessionSummary
            {
                SessionId = GetRequiredString(reader, "session_id"),
                ProviderId = new ModelProviderId(GetString(reader, "provider_id") ?? GetString(reader, "provider_key") ?? string.Empty),
                ProtocolFamily = GetString(reader, "protocol_family") ?? string.Empty,
                ProviderKey = GetString(reader, "provider_key") ?? string.Empty,
                ModelId = GetString(reader, "model_id"),
                ReasoningEffort = ParseReasoningEffort(GetString(reader, "reasoning_effort")),
                AgentPromptId = GetString(reader, "agent_prompt_id"),
                WorkingDirectory = GetString(reader, "working_directory"),
                Title = GetString(reader, "title"),
                Summary = GetString(reader, "summary"),
                ParentSessionId = GetString(reader, "parent_session_id"),
                CreatedBySessionId = GetString(reader, "created_by_session_id"),
                CreatedByRunId = CreateRunId(GetString(reader, "created_by_run_id")),
                CreatedAt = FromUtcTicks(GetInt64(reader, "created_at_utc_ticks")),
                UpdatedAt = FromUtcTicks(GetInt64(reader, "updated_at_utc_ticks")),
            };
        }

        AgentSessionState? state = null;
        var stateJson = withState ? GetString(reader, "state_json") : null;
        if (!string.IsNullOrWhiteSpace(stateJson))
        {
            try
            {
                state = JsonSerializer.Deserialize(stateJson, AgentJsonSerializerContext.Default.AgentSessionState);
            }
            catch (JsonException)
            {
                state = null;
            }
        }

        if (state is null && !string.IsNullOrWhiteSpace(GetString(reader, "provider_session_id")))
        {
            state = new AgentSessionState
            {
                SessionId = summary.SessionId,
                ProtocolFamily = summary.ProtocolFamily,
                ProviderKey = summary.ProviderKey,
                ProviderSessionId = GetString(reader, "provider_session_id"),
                UpdatedAt = summary.UpdatedAt,
            };
        }

        AgentSessionViewStateMetadata? localState = null;
        if (GetInt64(reader, "local_state_cached") != 0)
        {
            localState = new AgentSessionViewStateMetadata(
                ProviderKey: GetString(reader, "local_provider_key"),
                ModelId: GetString(reader, "local_model_id"),
                ReasoningEffort: ParseReasoningEffort(GetString(reader, "local_reasoning_effort")),
                AgentPromptId: GetString(reader, "local_agent_prompt_id"),
                Archived: GetInt64(reader, "archived") != 0,
                MessageCount: GetNullableInt32(reader, "message_count"),
                ParentSessionId: GetString(reader, "local_parent_session_id"),
                CreatedByJson: GetString(reader, "created_by_json"))
            {
                PermissionMode = GetString(reader, "local_permission_mode"),
                TitleMode = AgentSessionTitles.ParseMode(GetString(reader, "local_title_mode")),
                ArchivedAt = GetNullableInt64(reader, "local_archived_at_utc_ticks") is { } archivedAt ? FromUtcTicks(archivedAt) : null,
                QueuedPromptCount = GetNullableInt32(reader, "local_queued_prompt_count"),
            };
        }

        return new SqliteSessionProjectionRow(
            GetRequiredString(reader, "journal_path"),
            new AgentSessionCacheFileStamp(
                new DateTime(GetInt64(reader, "journal_last_write_utc_ticks"), DateTimeKind.Utc),
                GetInt64(reader, "journal_length")),
            summary,
            state,
            localState,
            GetString(reader, "space_id"),
            GetString(reader, "journal_header_json"),
            GetInt64(reader, "journal_header_read") != 0);
    }

    private static void AddParameter(SqliteCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    private static AgentSessionViewStateMetadata? ToAgentLocalState(SessionViewLocalState? state)
    {
        if (state is null)
        {
            return null;
        }

        return new AgentSessionViewStateMetadata(
            ProviderKey: NormalizeOptionalText(state.ProviderKey),
            ModelId: NormalizeOptionalText(state.ModelId),
            ReasoningEffort: state.ReasoningEffort,
            AgentPromptId: NormalizeOptionalText(state.AgentPromptId),
            Archived: state.Archived,
            MessageCount: state.MessageCount,
            ParentSessionId: NormalizeOptionalText(state.ParentSessionId),
            CreatedByJson: SerializeCreatedBy(state.CreatedBy))
        {
            PermissionMode = NormalizeOptionalText(state.PermissionMode),
            TitleMode = AgentSessionTitles.ParseMode(state.TitleMode),
            ArchivedAt = state.Archived ? state.ArchivedAt : null,
            QueuedPromptCount = state.CountPendingQueuedPrompts(),
        };
    }

    private static string? SerializeCreatedBy(AltaActorProvenance? createdBy)
        => createdBy is null
            ? null
            : JsonSerializer.Serialize(createdBy, SessionViewJournalJsonSerializerContext.Default.AltaActorProvenance);

    private static string? FormatReasoningEffort(AgentReasoningEffort? effort)
        => effort?.ToString();

    private static AgentReasoningEffort? ParseReasoningEffort(string? value)
        => Enum.TryParse<AgentReasoningEffort>(value, ignoreCase: true, out var result) ? result : null;

    private static AgentRunId? CreateRunId(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : new AgentRunId(value.Trim());

    private static DateTimeOffset FromUtcTicks(long ticks)
        => new(ticks, TimeSpan.Zero);

    private static string? NormalizeOptionalText(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string NormalizePathKey(string path)
        => Path.GetFullPath(path);

    private static AgentSessionCacheFileStamp? TryGetStamp(string path)
    {
        var fileInfo = new FileInfo(path);
        return fileInfo.Exists
            ? new AgentSessionCacheFileStamp(fileInfo.LastWriteTimeUtc, fileInfo.Length)
            : null;
    }

    private AgentSessionCacheLockedException CreateLockedException(SqliteException exception)
        => new($"The CodeAlta application database is locked: {_database.DatabasePath}", exception);

    // The file of the database could not be made, moved or replaced: a damaged file that another process holds
    // and that cannot be moved aside, a folder that cannot be written. For the callers of the list it is the same
    // as a lock that outlasts the timeout: the database cannot be used now, and can be later. The message says what
    // the system said, not what the cause may be.
    private static bool IsFileError(Exception exception)
        => exception is IOException or UnauthorizedAccessException;

    private AgentSessionCacheLockedException CreateFileException(Exception exception)
        => new($"The file of the CodeAlta application database cannot be used now: {_database.DatabasePath}. {exception.Message}", exception);

    private static string? GetString(SqliteDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static string GetRequiredString(SqliteDataReader reader, string name)
        => GetString(reader, name) ?? string.Empty;

    private static long GetInt64(SqliteDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? 0L : reader.GetInt64(ordinal);
    }

    private static int? GetNullableInt32(SqliteDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);
    }

    private static long? GetNullableInt64(SqliteDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);
    }

    // What a listing read from journals and writes back: the upserts it queued, and the headers of rows that had none.
    private sealed class RowWriteBack
    {
        public List<Task> Upserts { get; } = [];

        public List<(SqliteSessionProjectionRow Row, string? HeaderJson)> Headers { get; } = [];
    }

    private sealed class PendingUpsert(SqliteSessionProjection projection)
    {
        public SqliteSessionProjection Projection { get; set; } = projection;

        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    // LocalStateJson: the last state of the journal as the row keeps it, or null when it is not known.
    private sealed record HydratedView(
        string SessionId,
        AgentSessionCacheFileStamp Stamp,
        SessionViewJournalHeader? Header,
        AgentSessionViewStateMetadata? LocalState,
        string? LocalStateJson);

    // HeaderRead: the header of the journal was read, and Header is it (null when the journal has none). LocalStateJson:
    // the last state of the journal at the stamp of the projection, as the row keeps it; null when it is not known.
    private sealed record SqliteSessionProjection(
        AgentSessionCacheProjection Projection,
        SessionViewJournalHeader? Header,
        AgentSessionViewStateMetadata? LocalState,
        bool HeaderRead,
        string? LocalStateJson = null)
    {
        public string? HeaderJson { get; } = HeaderRead ? Header?.ToJson() : null;

        public AgentSessionCacheProjection ToProjection()
            => Projection with
            {
                ViewState = LocalState,
                SpaceId = NormalizeOptionalText(Header?.SpaceId) ?? Projection.SpaceId,
                JournalHeaderJson = HeaderJson,
            };
    }

    private sealed record SqliteSessionProjectionRow(
        string JournalPath,
        AgentSessionCacheFileStamp Stamp,
        AgentSessionSummary Summary,
        AgentSessionState? State,
        AgentSessionViewStateMetadata? LocalState,
        string? SpaceId,
        string? HeaderJson,
        bool HeaderRead)
    {
        public AgentSessionCacheProjection ToProjection()
            => new(JournalPath, Stamp, Summary, State, LocalState) { SpaceId = SpaceId, JournalHeaderJson = HeaderJson };
    }
}

/// <summary>
/// The last state of a session as the row of the list of sessions keeps it.
/// </summary>
/// <param name="JournalPath">The journal of the session.</param>
/// <param name="Stamp">The stamp of the journal the row is the projection of.</param>
/// <param name="HeaderJson">The header the journal starts with, as <see cref="SessionViewJournalHeader.ToJson"/> writes it; null when it has none or it is not known.</param>
/// <param name="HeaderRead">Whether <paramref name="HeaderJson"/> is known.</param>
/// <param name="LocalStateJson">The last state record of the journal (the JSON null for a journal without one); null when it is not known.</param>
internal sealed record SessionViewStateRow(
    string JournalPath,
    AgentSessionCacheFileStamp Stamp,
    string? HeaderJson,
    bool HeaderRead,
    string? LocalStateJson);

/// <summary>
/// The last state a session view wrote to its journal, as its row takes it.
/// </summary>
/// <param name="SessionId">The session.</param>
/// <param name="JournalPath">Its journal.</param>
/// <param name="State">The state.</param>
/// <param name="StateJson">The state record as the journal holds it.</param>
/// <param name="Stamp">The stamp of the journal just after the write; null to take the one it has now.</param>
internal sealed record SessionViewStateRowWrite(
    string SessionId,
    string JournalPath,
    SessionViewLocalState State,
    string StateJson,
    AgentSessionCacheFileStamp? Stamp)
{
    /// <summary>Gets the header of the descriptor the state was written with, for the columns the list filters by; null to keep them.</summary>
    public SessionViewJournalHeader? Header { get; init; }

    /// <summary>Gets the header the journal starts with, as <see cref="SessionViewJournalHeader.ToJson"/> writes it; null to keep the one of the row.</summary>
    public string? HeaderJson { get; init; }

    /// <summary>
    /// Gets the stamp the journal had just before the write: only a row that was the projection of the journal at that
    /// stamp takes the new stamp and the state record. Null for a row that takes them in any case.
    /// </summary>
    public AgentSessionCacheFileStamp? ExpectedStamp { get; init; }
}
