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
    private const int SchemaVersion = 1;
    private const int ProjectionVersion = 1;
    private const string CacheCompleteMetadataKey = "session_projection_cache_complete";
    private const string CacheCompleteValue = "1";
    private const string CacheIncompleteValue = "0";

    // Which copy the file was when the rows were last known to be those of this file (ApplicationDatabase.ReadCopyNumberAsync).
    private const string CopySeenMetadataKey = "session_projection_cache_copy";

    private readonly ApplicationDatabase _database;
    private readonly SemaphoreSlim _schemaGate = new(initialCount: 1, maxCount: 1);
    // The generation of the file whose tables are known to exist; -1 when they have to be made or checked.
    private int _schemaGeneration = -1;

    // The two below are written with the schema gate held. The generation of the file whose rows are trusted: a
    // file that was never replaced is generation 0. And the number of times the tables were made or made again.
    private int _rowsGeneration;
    private int _schemaEpoch;

    public SessionJournalSqliteCache(CatalogOptions options)
        : this(ApplicationDatabase.Create(options ?? throw new ArgumentNullException(nameof(options))))
    {
    }

    public SessionJournalSqliteCache(ApplicationDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _database = database;
    }

    public string DatabasePath => _database.DatabasePath;

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
                foreach (var row in rows)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!File.Exists(row.JournalPath))
                    {
                        await RemoveSessionAsync(row.Summary.SessionId, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    // Atomic selection commits may deliberately leave this derived cache behind.
                    // Never publish a stale provider/continuation pair after a journal replacement.
                    if (TryGetStamp(row.JournalPath) != row.Stamp)
                    {
                        var fresh = await context.ProjectSessionFileAsync(row.JournalPath, cancellationToken).ConfigureAwait(false);
                        if (fresh is not null) yield return fresh;
                    }
                    else yield return row.ToProjection();
                }

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

        if (!File.Exists(row.JournalPath))
        {
            await RemoveSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
            return null;
        }

        return TryGetStamp(row.JournalPath) == row.Stamp ? row.ToProjection()
            : await context.ProjectSessionFileAsync(row.JournalPath, cancellationToken).ConfigureAwait(false);
    }

    public async Task UpsertSessionAsync(
        AgentSessionCacheProjection projection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(projection);

        // The journal is read before the write starts: the queue of writers is not held while a file is read.
        var hydrated = await HydrateSessionViewDataAsync(projection, cancellationToken).ConfigureAwait(false);
        await WriteAsync(
                (connection, token) => UpsertSessionCoreAsync(connection, transaction: null, hydrated, token),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task RemoveSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        await WriteAsync(
                async (connection, token) =>
                {
                    await using var command = connection.CreateCommand();
                    command.CommandText = "DELETE FROM session_projection_cache WHERE session_id = $session_id;";
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

    public async Task UpsertSessionViewHeaderAsync(
        string sessionId,
        string journalPath,
        SessionViewJournalHeader header,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(journalPath);
        ArgumentNullException.ThrowIfNull(header);

        await WriteAsync(
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
                            local_parent_session_id = COALESCE(local_parent_session_id, $local_parent_session_id),
                            created_by_json = COALESCE(created_by_json, $created_by_json)
                        WHERE session_id = $session_id;
                        """;
                    var stamp = TryGetStamp(journalPath);
                    AddParameter(command, "$session_id", sessionId.Trim());
                    AddParameter(command, "$journal_last_write_utc_ticks", stamp?.LastWriteTimeUtc.Ticks ?? 0L);
                    AddParameter(command, "$journal_length", stamp?.Length ?? 0L);
                    AddParameter(command, "$cache_updated_utc_ticks", DateTimeOffset.UtcNow.UtcTicks);
                    AddParameter(command, "$kind", header.Kind.ToString());
                    AddParameter(command, "$project_ref", NormalizeOptionalText(header.ProjectRef));
                    AddParameter(command, "$local_parent_session_id", NormalizeOptionalText(header.ParentSessionId));
                    AddParameter(command, "$created_by_json", SerializeCreatedBy(header.CreatedBy));
                    await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task UpsertSessionViewStateAsync(
        string sessionId,
        string journalPath,
        SessionViewLocalState state,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(journalPath);
        ArgumentNullException.ThrowIfNull(state);

        await WriteAsync(
                async (connection, token) =>
                {
                    await using var command = connection.CreateCommand();
                    command.CommandText = """
                        UPDATE session_projection_cache
                        SET journal_last_write_utc_ticks = $journal_last_write_utc_ticks,
                            journal_length = $journal_length,
                            cache_updated_utc_ticks = $cache_updated_utc_ticks,
                            local_provider_key = $local_provider_key,
                            local_model_id = $local_model_id,
                            local_reasoning_effort = $local_reasoning_effort,
                            local_agent_prompt_id = $local_agent_prompt_id,
                            local_permission_mode = $local_permission_mode,
                            archived = $archived,
                            message_count = $message_count,
                            local_parent_session_id = $local_parent_session_id,
                            created_by_json = $created_by_json,
                            local_state_cached = 1
                        WHERE session_id = $session_id;
                        """;
                    var stamp = TryGetStamp(journalPath);
                    AddParameter(command, "$session_id", sessionId.Trim());
                    AddParameter(command, "$journal_last_write_utc_ticks", stamp?.LastWriteTimeUtc.Ticks ?? 0L);
                    AddParameter(command, "$journal_length", stamp?.Length ?? 0L);
                    AddParameter(command, "$cache_updated_utc_ticks", DateTimeOffset.UtcNow.UtcTicks);
                    AddParameter(command, "$local_provider_key", NormalizeOptionalText(state.ProviderKey));
                    AddParameter(command, "$local_model_id", NormalizeOptionalText(state.ModelId));
                    AddParameter(command, "$local_reasoning_effort", FormatReasoningEffort(state.ReasoningEffort));
                    AddParameter(command, "$local_agent_prompt_id", NormalizeOptionalText(state.AgentPromptId));
                    AddParameter(command, "$local_permission_mode", NormalizeOptionalText(state.PermissionMode));
                    AddParameter(command, "$archived", state.Archived ? 1 : 0);
                    AddParameter(command, "$message_count", state.MessageCount);
                    AddParameter(command, "$local_parent_session_id", NormalizeOptionalText(state.ParentSessionId));
                    AddParameter(command, "$created_by_json", SerializeCreatedBy(state.CreatedBy));
                    await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                },
                cancellationToken)
            .ConfigureAwait(false);
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
    private async Task WriteAsync(Func<SqliteConnection, CancellationToken, Task> write, CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await WriteCoreAsync(write, schemaOwned: false, cancellationToken).ConfigureAwait(false);
    }

    // schemaOwned: the caller is making the schema and holds its gate, so a damage it meets is its own to handle.
    private async Task WriteCoreAsync(Func<SqliteConnection, CancellationToken, Task> write, bool schemaOwned, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            var generation = _database.Generation;
            var epoch = Volatile.Read(ref _schemaEpoch);
            try
            {
                await _database.WriteAsync<object?>(
                        Owner,
                        async (connection, token) =>
                        {
                            await write(connection, token).ConfigureAwait(false);
                            return null;
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

        foreach (var sessionFile in Directory.EnumerateFiles(context.SessionsRootPath, "*.jsonl", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var projection = await context.ProjectSessionFileAsync(sessionFile, cancellationToken).ConfigureAwait(false);
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

    private Task<IReadOnlyList<SqliteSessionProjectionRow>?> TryQuerySessionRowsAsync(
        string? sessionId,
        CancellationToken cancellationToken)
        => TryReadAsync<IReadOnlyList<SqliteSessionProjectionRow>?>(
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText = sessionId is null
                    ? """
                        SELECT *
                        FROM session_projection_cache
                        ORDER BY updated_at_utc_ticks DESC, session_id COLLATE NOCASE DESC;
                        """
                    : """
                        SELECT *
                        FROM session_projection_cache
                        WHERE session_id = $session_id
                        LIMIT 1;
                        """;
                if (sessionId is not null)
                {
                    AddParameter(command, "$session_id", sessionId.Trim());
                }

                var rows = new List<SqliteSessionProjectionRow>();
                await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, token).ConfigureAwait(false);
                while (await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    var row = ReadRow(reader);
                    if (row is not null)
                    {
                        rows.Add(row);
                    }
                }

                return rows;
            },
            cancellationToken);

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
        var header = await TryReadHeaderAsync(projection.JournalPath, cancellationToken).ConfigureAwait(false);
        AgentSessionViewStateMetadata? localState = projection.ViewState;
        if (localState is null)
        {
            var viewState = await TryReadLatestStateAsync(projection.JournalPath, cancellationToken).ConfigureAwait(false);
            localState = viewState.ReadSucceeded
                ? ToAgentLocalState(viewState.State) ?? new AgentSessionViewStateMetadata()
                : null;
        }

        return new SqliteSessionProjection(projection, header, localState);
    }

    private static async Task<SessionViewJournalHeader?> TryReadHeaderAsync(string journalPath, CancellationToken cancellationToken)
    {
        try
        {
            return await SessionViewJournalStore.ReadHeaderFromPathAsync(journalPath, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
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
                    state_json TEXT,
                    kind TEXT,
                    project_ref TEXT,
                    local_provider_key TEXT,
                    local_model_id TEXT,
                    local_reasoning_effort TEXT,
                    local_agent_prompt_id TEXT,
                    local_permission_mode TEXT,
                    archived INTEGER NOT NULL DEFAULT 0,
                    message_count INTEGER,
                    local_parent_session_id TEXT,
                    created_by_json TEXT,
                    local_state_cached INTEGER NOT NULL DEFAULT 0
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
        command.CommandText = "DELETE FROM session_projection_cache;";
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
                state_json,
                kind,
                project_ref,
                local_provider_key,
                local_model_id,
                local_reasoning_effort,
                local_agent_prompt_id,
                local_permission_mode,
                archived,
                message_count,
                local_parent_session_id,
                created_by_json,
                local_state_cached)
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
                $state_json,
                $kind,
                $project_ref,
                $local_provider_key,
                $local_model_id,
                $local_reasoning_effort,
                $local_agent_prompt_id,
                $local_permission_mode,
                $archived,
                $message_count,
                $local_parent_session_id,
                $created_by_json,
                $local_state_cached)
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
                state_json = excluded.state_json,
                kind = excluded.kind,
                project_ref = excluded.project_ref,
                local_provider_key = CASE WHEN excluded.local_state_cached = 1 THEN excluded.local_provider_key ELSE session_projection_cache.local_provider_key END,
                local_model_id = CASE WHEN excluded.local_state_cached = 1 THEN excluded.local_model_id ELSE session_projection_cache.local_model_id END,
                local_reasoning_effort = CASE WHEN excluded.local_state_cached = 1 THEN excluded.local_reasoning_effort ELSE session_projection_cache.local_reasoning_effort END,
                local_agent_prompt_id = CASE WHEN excluded.local_state_cached = 1 THEN excluded.local_agent_prompt_id ELSE session_projection_cache.local_agent_prompt_id END,
                local_permission_mode = CASE WHEN excluded.local_state_cached = 1 THEN excluded.local_permission_mode ELSE session_projection_cache.local_permission_mode END,
                archived = CASE WHEN excluded.local_state_cached = 1 THEN excluded.archived ELSE session_projection_cache.archived END,
                message_count = CASE WHEN excluded.local_state_cached = 1 THEN excluded.message_count ELSE session_projection_cache.message_count END,
                local_parent_session_id = CASE WHEN excluded.local_state_cached = 1 THEN excluded.local_parent_session_id ELSE session_projection_cache.local_parent_session_id END,
                created_by_json = CASE WHEN excluded.local_state_cached = 1 THEN excluded.created_by_json ELSE session_projection_cache.created_by_json END,
                local_state_cached = CASE WHEN excluded.local_state_cached = 1 THEN 1 ELSE session_projection_cache.local_state_cached END;
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
        AddParameter(command, "$summary", NormalizeOptionalText(summary.Summary));
        AddParameter(command, "$model_id", NormalizeOptionalText(summary.ModelId));
        AddParameter(command, "$reasoning_effort", FormatReasoningEffort(summary.ReasoningEffort));
        AddParameter(command, "$agent_prompt_id", NormalizeOptionalText(summary.AgentPromptId));
        AddParameter(command, "$parent_session_id", NormalizeOptionalText(summary.ParentSessionId));
        AddParameter(command, "$created_by_session_id", NormalizeOptionalText(summary.CreatedBySessionId));
        AddParameter(command, "$created_by_run_id", NormalizeOptionalText(summary.CreatedByRunId?.Value));
        AddParameter(command, "$provider_session_id", NormalizeOptionalText(state?.ProviderSessionId));
        AddParameter(command, "$summary_json", JsonSerializer.Serialize(summary, AgentJsonSerializerContext.Default.AgentSessionSummary));
        AddParameter(command, "$state_json", state is null ? null : JsonSerializer.Serialize(state, AgentJsonSerializerContext.Default.AgentSessionState));
        AddParameter(command, "$kind", projection.Header?.Kind.ToString());
        AddParameter(command, "$project_ref", NormalizeOptionalText(projection.Header?.ProjectRef));
        AddParameter(command, "$local_provider_key", NormalizeOptionalText(localState?.ProviderKey));
        AddParameter(command, "$local_model_id", NormalizeOptionalText(localState?.ModelId));
        AddParameter(command, "$local_reasoning_effort", FormatReasoningEffort(localState?.ReasoningEffort));
        AddParameter(command, "$local_agent_prompt_id", NormalizeOptionalText(localState?.AgentPromptId));
        AddParameter(command, "$local_permission_mode", NormalizeOptionalText(localState?.PermissionMode));
        AddParameter(command, "$archived", localState?.Archived == true ? 1 : 0);
        AddParameter(command, "$message_count", localState?.MessageCount);
        AddParameter(command, "$local_parent_session_id", NormalizeOptionalText(localState?.ParentSessionId));
        AddParameter(command, "$created_by_json", NormalizeOptionalText(localState?.CreatedByJson));
        AddParameter(command, "$local_state_cached", localState is null ? 0 : 1);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static SqliteSessionProjectionRow? ReadRow(SqliteDataReader reader)
    {
        var summaryJson = GetString(reader, "summary_json");
        if (string.IsNullOrWhiteSpace(summaryJson))
        {
            return null;
        }

        AgentSessionSummary? summary;
        try
        {
            summary = JsonSerializer.Deserialize(summaryJson, AgentJsonSerializerContext.Default.AgentSessionSummary);
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
        var stateJson = GetString(reader, "state_json");
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
            };
        }

        return new SqliteSessionProjectionRow(
            GetRequiredString(reader, "journal_path"),
            new AgentSessionCacheFileStamp(
                new DateTime(GetInt64(reader, "journal_last_write_utc_ticks"), DateTimeKind.Utc),
                GetInt64(reader, "journal_length")),
            summary,
            state,
            localState);
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

    private sealed record SqliteSessionProjection(
        AgentSessionCacheProjection Projection,
        SessionViewJournalHeader? Header,
        AgentSessionViewStateMetadata? LocalState)
    {
        public AgentSessionCacheProjection ToProjection()
            => Projection with { ViewState = LocalState };
    }

    private sealed record SqliteSessionProjectionRow(
        string JournalPath,
        AgentSessionCacheFileStamp Stamp,
        AgentSessionSummary Summary,
        AgentSessionState? State,
        AgentSessionViewStateMetadata? LocalState)
    {
        public AgentSessionCacheProjection ToProjection()
            => new(JournalPath, Stamp, Summary, State, LocalState);
    }
}
