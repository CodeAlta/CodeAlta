using CodeAlta.Agent.Runtime;
using Microsoft.Data.Sqlite;

namespace CodeAlta.Catalog;

/// <summary>
/// Keeps the index of the session journals in the application database of the instance: one row per session in
/// <c>session_journal_index</c> (the length and last-write time of the journal it covers, where the indexing ends, the
/// snapshots in force there, the number of turns and of events of the history, the last notes record), one row per turn
/// in <c>session_journal_index_turn</c> and one row per usage record in <c>session_journal_index_usage</c>.
/// </summary>
/// <remarks>
/// The rows are derived from the journals. Tables that are damaged, or that a newer build wrote, are dropped and made
/// again empty, and the sessions are indexed again from their journals when they are next needed. A database that is
/// locked is not waited for beyond its busy timeout: the operation throws <see cref="AgentSessionCacheLockedException"/>.
/// </remarks>
internal sealed class SessionJournalIndexStore : IAgentSessionJournalIndex
{
    internal const string Owner = "session_journal_index";
    internal const string TablePrefix = "session_journal_index";
    internal const int SchemaVersion = 1;
    private const string RowTable = "session_journal_index";
    private const string TurnTable = "session_journal_index_turn";
    private const string UsageTable = "session_journal_index_usage";

    private const string RowColumns = "session_id, journal_path, length, last_write_utc_ticks, end_offset, guard, supported, format_version, "
        + "summary_offset, summary_length, state_offset, state_length, view_state_offset, view_state_length, turn_count, history_count, "
        + "last_run_id, notes_json, notes_offset, malformed_offset, usage_error";

    private const string TurnColumns = "session_id, number, run_id, start_offset, end_offset, started_at, ended_at, status, prompt_offset, "
        + "prompt_content_id, prompt_preview, prompt_truncated, source_session_id, ask_id, provenance, steer_count, assistant_message_count, "
        + "tool_call_count, compaction_count, history_index, summary_offset, summary_length, state_offset, state_length, view_state_offset, "
        + "view_state_length";

    private readonly ApplicationDatabase _database;
    private readonly SemaphoreSlim _schemaGate = new(initialCount: 1, maxCount: 1);
    // The generation of the file whose tables are known to exist; -1 when they have to be made or checked.
    private int _schemaGeneration = -1;

    /// <summary>Initializes a new instance of the <see cref="SessionJournalIndexStore"/> class.</summary>
    /// <param name="database">The application database of the instance, which the store borrows.</param>
    /// <exception cref="ArgumentNullException"><paramref name="database"/> is null.</exception>
    public SessionJournalIndexStore(ApplicationDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _database = database;
    }

    /// <inheritdoc />
    public Task<AgentSessionJournalIndexRow?> ReadRowAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        return RunAsync(() => _database.ReadAsync(Owner, (connection, token) => ReadRowAsync(connection, sessionId, token),
            recoverDamagedFile: false, cancellationToken), cancellationToken);
    }

    /// <inheritdoc />
    public Task<AgentSessionJournalIndexTurns?> ReadTurnsAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        return RunAsync(() => _database.ReadAsync<AgentSessionJournalIndexTurns?>(Owner, async (connection, token) =>
        {
            // The row and the turns are read from one snapshot of the database.
            // A deferred transaction reads one snapshot and writes nothing.
            using var transaction = connection.BeginTransaction(deferred: true);
            var row = await ReadRowAsync(connection, sessionId, token).ConfigureAwait(false);
            if (row is null)
            {
                return null;
            }

            var turns = await ReadTurnRowsAsync(connection, $"SELECT {TurnColumns} FROM {TurnTable} WHERE session_id = $session ORDER BY number;",
                command => command.Parameters.AddWithValue("$session", sessionId), token).ConfigureAwait(false);
            return new AgentSessionJournalIndexTurns(row, turns);
        }, recoverDamagedFile: false, cancellationToken), cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AgentSessionJournalIndexTurn>> ReadTurnsOfRunsAsync(string sessionId, IReadOnlyCollection<string> runIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(runIds);
        if (runIds.Count == 0)
        {
            return [];
        }

        return await RunAsync<IReadOnlyList<AgentSessionJournalIndexTurn>>(() => _database.ReadAsync<IReadOnlyList<AgentSessionJournalIndexTurn>>(Owner,
            async (connection, token) =>
            {
                var turns = new List<AgentSessionJournalIndexTurn>();
                foreach (var runId in runIds)
                {
                    turns.AddRange(await ReadTurnRowsAsync(connection, $"SELECT {TurnColumns} FROM {TurnTable} WHERE session_id = $session AND run_id = $run;",
                        command =>
                        {
                            command.Parameters.AddWithValue("$session", sessionId);
                            command.Parameters.AddWithValue("$run", runId);
                        }, token).ConfigureAwait(false));
                }

                return turns;
            }, recoverDamagedFile: false, cancellationToken), cancellationToken).ConfigureAwait(false) ?? [];
    }

    /// <inheritdoc />
    public Task<AgentSessionJournalIndexUsages?> ReadUsageAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        return RunAsync(() => _database.ReadAsync<AgentSessionJournalIndexUsages?>(Owner, async (connection, token) =>
        {
            // A deferred transaction reads one snapshot and writes nothing.
            using var transaction = connection.BeginTransaction(deferred: true);
            var row = await ReadRowAsync(connection, sessionId, token).ConfigureAwait(false);
            if (row is null)
            {
                return null;
            }

            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT offset, run_id, timestamp_utc_ticks, operation_json FROM {UsageTable} WHERE session_id = $session ORDER BY offset;";
            command.Parameters.AddWithValue("$session", sessionId);
            var usage = new List<AgentSessionJournalIndexUsage>();
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                usage.Add(new(reader.GetInt64(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetInt64(2), reader.GetString(3)));
            }

            return new AgentSessionJournalIndexUsages(row, usage);
        }, recoverDamagedFile: false, cancellationToken), cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> WriteAsync(AgentSessionJournalIndexUpdate update, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        return await RunAsync<bool?>(() => _database.WriteAsync<bool?>(Owner, async (connection, token) =>
        {
            var sessionId = update.Row.SessionId;
            if (update.Expected is { } expected)
            {
                // The row the indexing started from must still be the one the index holds: another writer may have gone on meanwhile.
                var current = await ReadRowAsync(connection, sessionId, token).ConfigureAwait(false);
                if (current is null || current.Length != expected.Length || current.LastWriteUtcTicks != expected.LastWriteTimeUtc.Ticks)
                {
                    return false;
                }
            }

            if (update.Replace)
            {
                await DeleteAsync(connection, sessionId, rows: false, token).ConfigureAwait(false);
            }

            await WriteRowAsync(connection, update.Row, token).ConfigureAwait(false);
            await WriteTurnsAsync(connection, sessionId, update.Turns, token).ConfigureAwait(false);
            await WriteUsageAsync(connection, sessionId, update.Usage, token).ConfigureAwait(false);
            return true;
        }, recoverDamagedFile: false, cancellationToken), cancellationToken).ConfigureAwait(false) ?? false;
    }

    /// <inheritdoc />
    public async Task RemoveAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        await RunAsync<bool?>(() => _database.WriteAsync<bool?>(Owner, async (connection, token) =>
        {
            await DeleteAsync(connection, sessionId, rows: true, token).ConfigureAwait(false);
            return true;
        }, recoverDamagedFile: false, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    // Runs an operation on the tables, made first when they are missing. Damaged tables, or tables a newer build wrote,
    // are made again empty and the operation runs once more; a database that cannot be used now gives default.
    private async Task<T?> RunAsync<T>(Func<ValueTask<T>> operation, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var generation = _database.Generation;
            try
            {
                await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
                return await operation().ConfigureAwait(false);
            }
            catch (SqliteException exception) when (ApplicationDatabase.IsLocked(exception))
            {
                throw new AgentSessionCacheLockedException("The session journal index database is locked.", exception);
            }
            catch (SqliteException exception) when (ApplicationDatabase.IsDamaged(exception))
            {
                await ClearAsync(exception, generation, cancellationToken).ConfigureAwait(false);
            }
            catch (ApplicationDatabaseNewerVersionException)
            {
                await ClearAsync(failure: null, generation, cancellationToken).ConfigureAwait(false);
            }
            catch (SqliteException) when (_database.Generation != generation)
            {
                // The file was replaced while the operation ran: the tables are made in the new one.
                Volatile.Write(ref _schemaGeneration, -1);
            }
            catch (SqliteException exception) when (attempt == 0 && IsMissingTable(exception))
            {
                // The file was made again without these tables (deleted, then made by another owner of it): they are made now.
                Volatile.Write(ref _schemaGeneration, -1);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ObjectDisposedException)
            {
                // The file cannot be used now, or the database was closed with the application: the journals are read.
                return default;
            }
        }

        return default;
    }

    private async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _schemaGeneration) == _database.Generation && File.Exists(_database.DatabasePath))
        {
            return;
        }

        await _schemaGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var generation = _database.Generation;
            if (_schemaGeneration == generation && File.Exists(_database.DatabasePath))
            {
                return;
            }

            await _database.MigrateAsync(Owner, TablePrefix, SchemaVersion, static async (connection, _, _, token) =>
                {
                    await using var command = connection.CreateCommand();
                    command.CommandText = $"""
                        CREATE TABLE IF NOT EXISTS {RowTable} (
                            session_id TEXT NOT NULL PRIMARY KEY,
                            journal_path TEXT NOT NULL,
                            length INTEGER NOT NULL,
                            last_write_utc_ticks INTEGER NOT NULL,
                            end_offset INTEGER NOT NULL,
                            guard BLOB NOT NULL,
                            supported INTEGER NOT NULL,
                            format_version INTEGER NOT NULL,
                            summary_offset INTEGER NOT NULL,
                            summary_length INTEGER NOT NULL,
                            state_offset INTEGER NOT NULL,
                            state_length INTEGER NOT NULL,
                            view_state_offset INTEGER NOT NULL,
                            view_state_length INTEGER NOT NULL,
                            turn_count INTEGER NOT NULL,
                            history_count INTEGER NOT NULL,
                            last_run_id TEXT NULL,
                            notes_json TEXT NULL,
                            notes_offset INTEGER NULL,
                            malformed_offset INTEGER NULL,
                            usage_error TEXT NULL
                        );
                        CREATE TABLE IF NOT EXISTS {TurnTable} (
                            session_id TEXT NOT NULL,
                            number INTEGER NOT NULL,
                            run_id TEXT NOT NULL,
                            start_offset INTEGER NOT NULL,
                            end_offset INTEGER NOT NULL,
                            started_at TEXT NOT NULL,
                            ended_at TEXT NULL,
                            status INTEGER NOT NULL,
                            prompt_offset INTEGER NULL,
                            prompt_content_id TEXT NULL,
                            prompt_preview TEXT NOT NULL,
                            prompt_truncated INTEGER NOT NULL,
                            source_session_id TEXT NULL,
                            ask_id TEXT NULL,
                            provenance TEXT NULL,
                            steer_count INTEGER NOT NULL,
                            assistant_message_count INTEGER NOT NULL,
                            tool_call_count INTEGER NOT NULL,
                            compaction_count INTEGER NOT NULL,
                            history_index INTEGER NOT NULL,
                            summary_offset INTEGER NOT NULL,
                            summary_length INTEGER NOT NULL,
                            state_offset INTEGER NOT NULL,
                            state_length INTEGER NOT NULL,
                            view_state_offset INTEGER NOT NULL,
                            view_state_length INTEGER NOT NULL,
                            PRIMARY KEY (session_id, number)
                        );
                        CREATE INDEX IF NOT EXISTS {TurnTable}_run ON {TurnTable} (session_id, run_id);
                        CREATE TABLE IF NOT EXISTS {UsageTable} (
                            session_id TEXT NOT NULL,
                            offset INTEGER NOT NULL,
                            run_id TEXT NULL,
                            timestamp_utc_ticks INTEGER NOT NULL,
                            operation_json TEXT NOT NULL,
                            PRIMARY KEY (session_id, offset)
                        );
                        """;
                    await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }, recoverDamagedFile: false, cancellationToken)
                .ConfigureAwait(false);
            Volatile.Write(ref _schemaGeneration, generation);
        }
        finally
        {
            _schemaGate.Release();
        }
    }

    private static bool IsMissingTable(SqliteException exception)
        => exception.SqliteErrorCode == 1 && exception.Message.Contains("no such table", StringComparison.Ordinal);

    // Drops the tables, to be made again empty; the file itself is replaced when the damage is not in these tables.
    private async Task ClearAsync(SqliteException? failure, int generation, CancellationToken cancellationToken)
    {
        Volatile.Write(ref _schemaGeneration, -1);
        try
        {
            await _database.DropTablesAsync(Owner, TablePrefix, recoverDamagedFile: false, cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException again) when (ApplicationDatabase.IsDamaged(again))
        {
            await _database.RecoverDamagedFileAsync(failure ?? again, generation, cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException exception) when (ApplicationDatabase.IsLocked(exception))
        {
            throw new AgentSessionCacheLockedException("The session journal index database is locked.", exception);
        }
    }

    private static async ValueTask<AgentSessionJournalIndexRow?> ReadRowAsync(SqliteConnection connection, string sessionId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {RowColumns} FROM {RowTable} WHERE session_id = $session;";
        command.Parameters.AddWithValue("$session", sessionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new AgentSessionJournalIndexRow
        {
            SessionId = reader.GetString(0),
            JournalPath = reader.GetString(1),
            Length = reader.GetInt64(2),
            LastWriteUtcTicks = reader.GetInt64(3),
            EndOffset = reader.GetInt64(4),
            Guard = (byte[])reader.GetValue(5),
            Supported = reader.GetInt64(6) != 0,
            FormatVersion = reader.GetInt32(7),
            Summary = new(reader.GetInt64(8), reader.GetInt32(9)),
            State = new(reader.GetInt64(10), reader.GetInt32(11)),
            ViewState = new(reader.GetInt64(12), reader.GetInt32(13)),
            TurnCount = reader.GetInt32(14),
            HistoryCount = reader.GetInt64(15),
            LastRunId = reader.IsDBNull(16) ? null : reader.GetString(16),
            NotesJson = reader.IsDBNull(17) ? null : reader.GetString(17),
            NotesOffset = reader.IsDBNull(18) ? null : reader.GetInt64(18),
            MalformedOffset = reader.IsDBNull(19) ? null : reader.GetInt64(19),
            UsageError = reader.IsDBNull(20) ? null : reader.GetString(20),
        };
    }

    private static async ValueTask<List<AgentSessionJournalIndexTurn>> ReadTurnRowsAsync(SqliteConnection connection, string sql,
        Action<SqliteCommand> bind, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        bind(command);
        var turns = new List<AgentSessionJournalIndexTurn>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var turn = new AgentSessionTurn
            {
                Number = reader.GetInt32(1),
                RunId = reader.GetString(2),
                StartOffset = reader.GetInt64(3),
                EndOffset = reader.GetInt64(4),
                StartedAt = DateTimeOffset.Parse(reader.GetString(5), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind),
                EndedAt = reader.IsDBNull(6) ? null : DateTimeOffset.Parse(reader.GetString(6), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind),
                Status = (AgentSessionTurnStatus)reader.GetInt32(7),
                PromptOffset = reader.IsDBNull(8) ? null : reader.GetInt64(8),
                PromptContentId = reader.IsDBNull(9) ? null : reader.GetString(9),
                PromptPreview = reader.GetString(10),
                PromptTruncated = reader.GetInt64(11) != 0,
                SourceSessionId = reader.IsDBNull(12) ? null : reader.GetString(12),
                AskId = reader.IsDBNull(13) ? null : reader.GetString(13),
                Provenance = reader.IsDBNull(14) ? null : reader.GetString(14),
                SteerCount = reader.GetInt32(15),
                AssistantMessageCount = reader.GetInt32(16),
                ToolCallCount = reader.GetInt32(17),
                CompactionCount = reader.GetInt32(18),
                HistoryIndex = reader.GetInt64(19),
            };
            turns.Add(new(turn, new(reader.GetInt64(20), reader.GetInt32(21)), new(reader.GetInt64(22), reader.GetInt32(23)),
                new(reader.GetInt64(24), reader.GetInt32(25))));
        }

        return turns;
    }

    private static async Task WriteRowAsync(SqliteConnection connection, AgentSessionJournalIndexRow row, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            INSERT OR REPLACE INTO {RowTable} ({RowColumns})
            VALUES ($session, $path, $length, $ticks, $end, $guard, $supported, $format, $summaryOffset, $summaryLength, $stateOffset,
                $stateLength, $viewOffset, $viewLength, $turns, $history, $lastRun, $notes, $notesOffset, $malformed, $usageError);
            """;
        command.Parameters.AddWithValue("$session", row.SessionId);
        command.Parameters.AddWithValue("$path", row.JournalPath);
        command.Parameters.AddWithValue("$length", row.Length);
        command.Parameters.AddWithValue("$ticks", row.LastWriteUtcTicks);
        command.Parameters.AddWithValue("$end", row.EndOffset);
        command.Parameters.Add("$guard", SqliteType.Blob).Value = row.Guard;
        command.Parameters.AddWithValue("$supported", row.Supported ? 1 : 0);
        command.Parameters.AddWithValue("$format", row.FormatVersion);
        command.Parameters.AddWithValue("$summaryOffset", row.Summary.Offset);
        command.Parameters.AddWithValue("$summaryLength", row.Summary.Length);
        command.Parameters.AddWithValue("$stateOffset", row.State.Offset);
        command.Parameters.AddWithValue("$stateLength", row.State.Length);
        command.Parameters.AddWithValue("$viewOffset", row.ViewState.Offset);
        command.Parameters.AddWithValue("$viewLength", row.ViewState.Length);
        command.Parameters.AddWithValue("$turns", row.TurnCount);
        command.Parameters.AddWithValue("$history", row.HistoryCount);
        command.Parameters.AddWithValue("$lastRun", (object?)row.LastRunId ?? DBNull.Value);
        command.Parameters.AddWithValue("$notes", (object?)row.NotesJson ?? DBNull.Value);
        command.Parameters.AddWithValue("$notesOffset", (object?)row.NotesOffset ?? DBNull.Value);
        command.Parameters.AddWithValue("$malformed", (object?)row.MalformedOffset ?? DBNull.Value);
        command.Parameters.AddWithValue("$usageError", (object?)row.UsageError ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteTurnsAsync(SqliteConnection connection, string sessionId, IReadOnlyList<AgentSessionJournalIndexTurn> turns,
        CancellationToken cancellationToken)
    {
        if (turns.Count == 0)
        {
            return;
        }

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            INSERT OR REPLACE INTO {TurnTable} ({TurnColumns})
            VALUES ($session, $number, $run, $start, $end, $startedAt, $endedAt, $status, $promptOffset, $promptId, $preview, $truncated,
                $source, $ask, $provenance, $steers, $assistant, $tools, $compactions, $history, $summaryOffset, $summaryLength,
                $stateOffset, $stateLength, $viewOffset, $viewLength);
            """;
        command.Parameters.AddWithValue("$session", sessionId);
        var parameters = new[]
        {
            "$number", "$run", "$start", "$end", "$startedAt", "$endedAt", "$status", "$promptOffset", "$promptId", "$preview", "$truncated",
            "$source", "$ask", "$provenance", "$steers", "$assistant", "$tools", "$compactions", "$history", "$summaryOffset",
            "$summaryLength", "$stateOffset", "$stateLength", "$viewOffset", "$viewLength",
        }.Select(name => command.Parameters.Add(name, SqliteType.Text)).ToArray();
        foreach (var indexed in turns)
        {
            var turn = indexed.Turn;
            object?[] values =
            [
                turn.Number, turn.RunId, turn.StartOffset, turn.EndOffset, turn.StartedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                turn.EndedAt?.ToString("O", System.Globalization.CultureInfo.InvariantCulture), (int)turn.Status, turn.PromptOffset, turn.PromptContentId,
                turn.PromptPreview, turn.PromptTruncated ? 1 : 0, turn.SourceSessionId, turn.AskId, turn.Provenance, turn.SteerCount,
                turn.AssistantMessageCount, turn.ToolCallCount, turn.CompactionCount, turn.HistoryIndex, indexed.Summary.Offset,
                indexed.Summary.Length, indexed.State.Offset, indexed.State.Length, indexed.ViewState.Offset, indexed.ViewState.Length,
            ];
            for (var index = 0; index < parameters.Length; index++)
            {
                parameters[index].SqliteType = values[index] is string ? SqliteType.Text : SqliteType.Integer;
                parameters[index].Value = values[index] ?? DBNull.Value;
            }

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task WriteUsageAsync(SqliteConnection connection, string sessionId, IReadOnlyList<AgentSessionJournalIndexUsage> usage,
        CancellationToken cancellationToken)
    {
        if (usage.Count == 0)
        {
            return;
        }

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            INSERT OR REPLACE INTO {UsageTable} (session_id, offset, run_id, timestamp_utc_ticks, operation_json)
            VALUES ($session, $offset, $run, $ticks, $operation);
            """;
        command.Parameters.AddWithValue("$session", sessionId);
        var offset = command.Parameters.Add("$offset", SqliteType.Integer);
        var run = command.Parameters.Add("$run", SqliteType.Text);
        var ticks = command.Parameters.Add("$ticks", SqliteType.Integer);
        var operation = command.Parameters.Add("$operation", SqliteType.Text);
        foreach (var record in usage)
        {
            offset.Value = record.Offset;
            run.Value = (object?)record.RunId ?? DBNull.Value;
            ticks.Value = record.TimestampUtcTicks;
            operation.Value = record.OperationJson;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task DeleteAsync(SqliteConnection connection, string sessionId, bool rows, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = rows
            ? $"DELETE FROM {TurnTable} WHERE session_id = $session; DELETE FROM {UsageTable} WHERE session_id = $session; DELETE FROM {RowTable} WHERE session_id = $session;"
            : $"DELETE FROM {TurnTable} WHERE session_id = $session; DELETE FROM {UsageTable} WHERE session_id = $session;";
        command.Parameters.AddWithValue("$session", sessionId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
