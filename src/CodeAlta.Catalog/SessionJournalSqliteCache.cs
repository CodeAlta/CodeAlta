using System.Data;
using System.Runtime.CompilerServices;
using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using Microsoft.Data.Sqlite;

namespace CodeAlta.Catalog;

internal sealed class SessionJournalSqliteCache : IAgentSessionProjectionCache
{
    private const int SqliteBusy = 5;
    private const int SqliteLocked = 6;
    private const int SqliteCorrupt = 11;
    private const int SqliteNotADatabase = 26;
    private const int ProjectionVersion = 1;
    private const string CacheCompleteMetadataKey = "session_projection_cache_complete";
    private const string CacheCompleteValue = "1";
    private const string CacheIncompleteValue = "0";

    private readonly string _databasePath;
    private readonly SemaphoreSlim _gate = new(initialCount: 1, maxCount: 1);
    private bool _schemaReady;

    public SessionJournalSqliteCache(CatalogOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _databasePath = options.SessionCacheDatabasePath;
    }

    public string DatabasePath => _databasePath;

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

                    yield return row.ToProjection();
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

        return row.ToProjection();
    }

    public async Task UpsertSessionAsync(
        AgentSessionCacheProjection projection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(projection);

        await ExecuteWriteWithRecoveryAsync(
                async () =>
                {
                    var hydrated = await HydrateSessionViewDataAsync(projection, cancellationToken).ConfigureAwait(false);
                    await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
                    await EnsureSchemaCoreAsync(connection, cancellationToken).ConfigureAwait(false);
                    await UpsertSessionCoreAsync(connection, transaction: null, hydrated, cancellationToken).ConfigureAwait(false);
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task RemoveSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        await ExecuteWriteWithRecoveryAsync(
                async () =>
                {
                    await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
                    await EnsureSchemaCoreAsync(connection, cancellationToken).ConfigureAwait(false);
                    await using var command = connection.CreateCommand();
                    command.CommandText = "DELETE FROM session_projection_cache WHERE session_id = $session_id;";
                    AddParameter(command, "$session_id", sessionId.Trim());
                    await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<AgentSessionCacheReconciliationResult> ReconcileAsync(
        AgentSessionCacheProjectionContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);

        if (!Directory.Exists(context.SessionsRootPath))
        {
            var removed = await PruneRowsMissingFromDiskAsync([], cancellationToken).ConfigureAwait(false);
            await MarkCacheCompleteAsync(cancellationToken).ConfigureAwait(false);
            return new AgentSessionCacheReconciliationResult(removed > 0, 0, removed);
        }

        var existingRows = await QuerySessionRowsAsync(sessionId: null, cancellationToken).ConfigureAwait(false);
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
        await MarkCacheCompleteAsync(cancellationToken).ConfigureAwait(false);
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

        await ExecuteWriteWithRecoveryAsync(
                async () =>
                {
                    await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
                    await EnsureSchemaCoreAsync(connection, cancellationToken).ConfigureAwait(false);
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
                    await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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

        await ExecuteWriteWithRecoveryAsync(
                async () =>
                {
                    await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
                    await EnsureSchemaCoreAsync(connection, cancellationToken).ConfigureAwait(false);
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
                    AddParameter(command, "$archived", state.Archived ? 1 : 0);
                    AddParameter(command, "$message_count", state.MessageCount);
                    AddParameter(command, "$local_parent_session_id", NormalizeOptionalText(state.ParentSessionId));
                    AddParameter(command, "$created_by_json", SerializeCreatedBy(state.CreatedBy));
                    await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_schemaReady && File.Exists(_databasePath))
            {
                return;
            }

            try
            {
                await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
                await EnsureSchemaCoreAsync(connection, cancellationToken).ConfigureAwait(false);
                _schemaReady = true;
            }
            catch (SqliteException ex) when (IsRecoverableCorruption(ex))
            {
                await RecreateDatabaseFileAsync(cancellationToken).ConfigureAwait(false);
                await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
                await EnsureSchemaCoreAsync(connection, cancellationToken).ConfigureAwait(false);
                _schemaReady = true;
            }
        }
        catch (SqliteException ex) when (IsLocked(ex))
        {
            throw CreateLockedException(ex);
        }
        finally
        {
            _gate.Release();
            SqliteConnection.ClearAllPools();
        }
    }

    private async IAsyncEnumerable<AgentSessionCacheProjection> RebuildAndListSessionsAsync(
        AgentSessionCacheProjectionContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await PrepareProgressiveRebuildAsync(cancellationToken).ConfigureAwait(false);
        if (!Directory.Exists(context.SessionsRootPath))
        {
            await MarkCacheCompleteAsync(cancellationToken).ConfigureAwait(false);
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

        await MarkCacheCompleteAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task PrepareProgressiveRebuildAsync(CancellationToken cancellationToken)
        => await ExecuteWriteWithRecoveryAsync(
                async () =>
                {
                    await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
                    await EnsureSchemaCoreAsync(connection, cancellationToken).ConfigureAwait(false);
                    await SetCacheCompleteCoreAsync(connection, complete: false, cancellationToken).ConfigureAwait(false);
                    await ClearSessionRowsCoreAsync(connection, cancellationToken).ConfigureAwait(false);
                },
                cancellationToken)
            .ConfigureAwait(false);

    private async Task UpsertHydratedSessionAsync(
        SqliteSessionProjection projection,
        CancellationToken cancellationToken)
        => await ExecuteWriteWithRecoveryAsync(
                async () =>
                {
                    await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
                    await EnsureSchemaCoreAsync(connection, cancellationToken).ConfigureAwait(false);
                    await UpsertSessionCoreAsync(connection, transaction: null, projection, cancellationToken).ConfigureAwait(false);
                },
                cancellationToken)
            .ConfigureAwait(false);

    private Task MarkCacheCompleteAsync(CancellationToken cancellationToken)
        => ExecuteWriteWithRecoveryAsync(
            async () =>
            {
                await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
                await EnsureSchemaCoreAsync(connection, cancellationToken).ConfigureAwait(false);
                await SetCacheCompleteCoreAsync(connection, complete: true, cancellationToken).ConfigureAwait(false);
            },
            cancellationToken);

    private async Task ExecuteWriteWithRecoveryAsync(Func<Task> action, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            try
            {
                await action().ConfigureAwait(false);
            }
            catch (SqliteException ex) when (IsRecoverableCorruption(ex))
            {
                await RecreateDatabaseFileAsync(cancellationToken).ConfigureAwait(false);
                _schemaReady = false;
                await action().ConfigureAwait(false);
                _schemaReady = true;
            }
        }
        catch (SqliteException ex) when (IsLocked(ex))
        {
            throw CreateLockedException(ex);
        }
        finally
        {
            _gate.Release();
            SqliteConnection.ClearAllPools();
        }
    }

    private async Task<bool> IsCacheCompleteAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await EnsureSchemaCoreAsync(connection, cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM session_projection_cache_metadata WHERE key = $key LIMIT 1;";
            AddParameter(command, "$key", CacheCompleteMetadataKey);
            var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
            if (!string.IsNullOrWhiteSpace(value))
            {
                return string.Equals(value, CacheCompleteValue, StringComparison.Ordinal);
            }

            return await SessionRowCountCoreAsync(connection, cancellationToken).ConfigureAwait(false) > 0;
        }
        catch (SqliteException ex) when (IsLocked(ex))
        {
            throw CreateLockedException(ex);
        }
        catch (SqliteException ex) when (IsRecoverableCorruption(ex))
        {
            await RecreateDatabaseFileAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    private async Task<IReadOnlyList<SqliteSessionProjectionRow>> QuerySessionRowsAsync(
        string? sessionId,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await EnsureSchemaCoreAsync(connection, cancellationToken).ConfigureAwait(false);
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
            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var row = ReadRow(reader);
                if (row is not null)
                {
                    rows.Add(row);
                }
            }

            return rows;
        }
        catch (SqliteException ex) when (IsLocked(ex))
        {
            throw CreateLockedException(ex);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    private async Task<IReadOnlyList<SqliteSessionProjectionRow>?> TryQuerySessionRowsAsync(
        string? sessionId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await QuerySessionRowsAsync(sessionId, cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException ex) when (IsRecoverableCorruption(ex))
        {
            await RecreateDatabaseFileAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }
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

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_databasePath)
            ?? throw new InvalidOperationException($"Cache database path '{_databasePath}' did not resolve to a directory.");
        Directory.CreateDirectory(directory);

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            DefaultTimeout = 1,
            Pooling = false,
        };
        var connection = new SqliteConnection(builder.ToString());
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA busy_timeout = 0;";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch (SqliteException ex) when (IsLocked(ex))
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw CreateLockedException(ex);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
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

        await ExecuteSchemaCommandAsync(connection, "CREATE INDEX IF NOT EXISTS ix_session_projection_cache_updated ON session_projection_cache(updated_at_utc_ticks DESC);", cancellationToken).ConfigureAwait(false);
        await ExecuteSchemaCommandAsync(connection, "CREATE INDEX IF NOT EXISTS ix_session_projection_cache_working_directory ON session_projection_cache(working_directory COLLATE NOCASE);", cancellationToken).ConfigureAwait(false);
        await ExecuteSchemaCommandAsync(connection, "CREATE INDEX IF NOT EXISTS ix_session_projection_cache_project_ref ON session_projection_cache(project_ref COLLATE NOCASE);", cancellationToken).ConfigureAwait(false);
        await ExecuteSchemaCommandAsync(connection, "CREATE UNIQUE INDEX IF NOT EXISTS ux_session_projection_cache_journal_path ON session_projection_cache(journal_path COLLATE NOCASE);", cancellationToken).ConfigureAwait(false);
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
                CreatedByJson: GetString(reader, "created_by_json"));
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

    private async Task RecreateDatabaseFileAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SqliteConnection.ClearAllPools();
        _schemaReady = false;
        DeleteIfExists(_databasePath);
        DeleteIfExists(_databasePath + "-wal");
        DeleteIfExists(_databasePath + "-shm");
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
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
            CreatedByJson: SerializeCreatedBy(state.CreatedBy));
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

    private static bool IsLocked(SqliteException exception)
        => exception.SqliteErrorCode is SqliteBusy or SqliteLocked;

    private static bool IsRecoverableCorruption(SqliteException exception)
        => exception.SqliteErrorCode is SqliteCorrupt or SqliteNotADatabase;

    private AgentSessionCacheLockedException CreateLockedException(SqliteException exception)
        => new($"The CodeAlta local session cache database is locked: {_databasePath}", exception);

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
