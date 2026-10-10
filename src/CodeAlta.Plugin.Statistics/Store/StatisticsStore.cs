using System.Globalization;
using CodeAlta.Plugin.Statistics.Facts;
using CodeAlta.Plugin.Statistics.Journal;
using CodeAlta.Plugins.Abstractions;
using Microsoft.Data.Sqlite;

namespace CodeAlta.Plugin.Statistics.Store;

/// <summary>
/// The tables of the Statistics plugin in the application database: the facts per session and quarter hour, the roll-ups per local day,
/// month and year, the sessions and their runs, and where the reading of each journal stopped. Saving one catch-up of a session (its
/// facts, the roll-ups, the cursor) is one write transaction.
/// </summary>
/// <remarks>
/// <para>
/// The roll-ups are maintained in the same transaction as the facts: the rows a catch-up adds are added to its quarter hours, its local
/// days, its months and its years. When a session is replaced (a rewritten file, a new version of the facts) the roll-ups of the days
/// it touched are added up again from the quarter hours of every session, because a largest value cannot be taken away. When the
/// version of the roll-ups or the time zone changes, every roll-up is added up again from the facts alone.
/// </para>
/// <para>No row holds text of a session but its title and the name of its project.</para>
/// </remarks>
internal sealed partial class StatisticsStore
{
    /// <summary>The version of the tables, kept by the host for the plugin.</summary>
    public const int SchemaVersion = 1;

    /// <summary>The version of the roll-ups: when it changes, they are added up again from the facts.</summary>
    public const int RollupVersion = 1;

    /// <summary>The key of the version of the roll-ups in the <c>meta</c> table.</summary>
    public const string RollupVersionKey = "rollup.version";

    /// <summary>The key of the time zone of the roll-ups in the <c>meta</c> table.</summary>
    public const string RollupTimeZoneKey = "rollup.timezone";

    private readonly IPluginDatabase _database;

    /// <summary>Initializes the store over the database of the plugin.</summary>
    /// <param name="database">The database service the host gives the plugin.</param>
    /// <param name="days">The local days of the time zone of the roll-ups.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="InvalidOperationException">The host has no database.</exception>
    public StatisticsStore(IPluginDatabase database, LocalDays days)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(days);
        if (!database.HasDatabase)
        {
            throw new InvalidOperationException("The host has no application database for the statistics.");
        }

        _database = database;
        Days = days;
        Prefix = database.TablePrefix;
    }

    /// <summary>Gets the prefix of the tables.</summary>
    public string Prefix { get; }

    /// <summary>Gets the local days of the time zone of the roll-ups.</summary>
    public LocalDays Days { get; }

    /// <summary>Gets the name of a table of the store.</summary>
    /// <param name="name">The name without the prefix.</param>
    /// <returns>The name with the prefix.</returns>
    public string Table(string name) => Prefix + name;

    /// <summary>Creates the tables and adds the roll-ups up again when their version or the time zone changed.</summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task representing the operation.</returns>
    public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _database.MigrateAsync(SchemaVersion, (connection, from, to, token) =>
        {
            if (from < 1)
            {
                using var sql = new SqlSession(connection);
                sql.ExecuteScript(SchemaV1());
            }

            return ValueTask.CompletedTask;
        }, cancellationToken).ConfigureAwait(false);

        var version = await GetMetaAsync(RollupVersionKey, cancellationToken).ConfigureAwait(false);
        var zone = await GetMetaAsync(RollupTimeZoneKey, cancellationToken).ConfigureAwait(false);
        if (version != RollupVersion.ToString(CultureInfo.InvariantCulture) || zone != Days.TimeZone.Id)
        {
            await RebuildRollupsAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Gets a value of the <c>meta</c> table.</summary>
    /// <param name="key">The key.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The value; null when there is none.</returns>
    public ValueTask<string?> GetMetaAsync(string key, CancellationToken cancellationToken = default)
        => ReadAsync(sql => sql.Scalar($"SELECT value FROM {Table("meta")} WHERE key = @p0", key) as string, cancellationToken);

    /// <summary>Gets every value of the <c>meta</c> table.</summary>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The values by key.</returns>
    public ValueTask<IReadOnlyDictionary<string, string>> GetAllMetaAsync(CancellationToken cancellationToken = default)
        => ReadAsync<IReadOnlyDictionary<string, string>>(
            sql => sql.Query($"SELECT key, value FROM {Table("meta")}", static reader => (reader.GetString(0), reader.GetString(1))).ToDictionary(static pair => pair.Item1, static pair => pair.Item2, StringComparer.Ordinal),
            cancellationToken);

    /// <summary>Sets values of the <c>meta</c> table; a null value removes the key.</summary>
    /// <param name="values">The values by key.</param>
    /// <param name="cancellationToken">A token to cancel the write.</param>
    /// <returns>A task representing the write.</returns>
    public ValueTask SetMetaAsync(IReadOnlyDictionary<string, string?> values, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(values);
        return WriteAsync(sql => SetMeta(sql, values), cancellationToken);
    }

    /// <summary>Runs a read on a connection of its own.</summary>
    /// <typeparam name="T">The result type.</typeparam>
    /// <param name="read">The read.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The result.</returns>
    public async ValueTask<T> ReadAsync<T>(Func<SqlSession, T> read, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(read);
        return await _database.ReadAsync((connection, token) =>
        {
            using var sql = new SqlSession(connection);
            return ValueTask.FromResult(read(sql));
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Runs a write in one transaction, in turn with the other writers of the application.</summary>
    /// <param name="write">The write.</param>
    /// <param name="cancellationToken">A token to cancel the write.</param>
    /// <returns>A task representing the write.</returns>
    public async ValueTask WriteAsync(Action<SqlSession> write, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(write);
        await _database.WriteAsync((connection, token) =>
        {
            using var sql = new SqlSession(connection);
            write(sql);
            return ValueTask.CompletedTask;
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Gets where the reading of every journal stopped.</summary>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The rows by session.</returns>
    public ValueTask<IReadOnlyDictionary<string, JournalRow>> ListJournalsAsync(CancellationToken cancellationToken = default)
        => ReadAsync<IReadOnlyDictionary<string, JournalRow>>(
            sql => sql.Query(SelectJournal(), ReadJournal).ToDictionary(static row => row.SessionId, StringComparer.OrdinalIgnoreCase),
            cancellationToken);

    /// <summary>Gets where the reading of a journal stopped.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The row; null when the journal was never read.</returns>
    public ValueTask<JournalRow?> GetJournalAsync(string sessionId, CancellationToken cancellationToken = default)
        => ReadAsync(sql => sql.Query(SelectJournal() + " WHERE session_id = @p0", ReadJournal, sessionId).FirstOrDefault(), cancellationToken);

    /// <summary>Marks the sessions whose journal is gone: their facts stay.</summary>
    /// <param name="sessionIds">The sessions.</param>
    /// <param name="cancellationToken">A token to cancel the write.</param>
    /// <returns>A task representing the write.</returns>
    public ValueTask MarkDeletedAsync(IReadOnlyCollection<string> sessionIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sessionIds);
        return sessionIds.Count == 0
            ? ValueTask.CompletedTask
            : WriteAsync(
                sql =>
                {
                    foreach (var id in sessionIds)
                    {
                        sql.Execute($"UPDATE {Table("journal")} SET deleted = 1 WHERE session_id = @p0", id);
                        sql.Execute($"UPDATE {Table("session")} SET deleted = 1 WHERE session_id = @p0", id);
                    }
                },
                cancellationToken);
    }

    /// <summary>
    /// Gets the names of the projects the sessions were created for, as they were last resolved.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The names by project reference.</returns>
    public ValueTask<IReadOnlyDictionary<string, string>> GetProjectNamesAsync(CancellationToken cancellationToken = default)
        => ReadAsync<IReadOnlyDictionary<string, string>>(
            sql => sql.Query($"SELECT project_ref, name FROM {Table("project")}", static reader => (reader.GetString(0), reader.GetString(1)))
                .ToDictionary(static pair => pair.Item1, static pair => pair.Item2, StringComparer.OrdinalIgnoreCase),
            cancellationToken);

    /// <summary>Remembers the names of projects.</summary>
    /// <param name="names">The names by project reference.</param>
    /// <param name="cancellationToken">A token to cancel the write.</param>
    /// <returns>A task representing the write.</returns>
    public ValueTask SetProjectNamesAsync(IReadOnlyDictionary<string, string> names, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(names);
        return names.Count == 0
            ? ValueTask.CompletedTask
            : WriteAsync(
                sql =>
                {
                    var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    foreach (var (reference, name) in names)
                    {
                        sql.Execute($"INSERT INTO {Table("project")} (project_ref, name, updated_ms) VALUES (@p0, @p1, @p2) ON CONFLICT (project_ref) DO UPDATE SET name = excluded.name, updated_ms = excluded.updated_ms", reference, name, now);
                    }
                },
                cancellationToken);
    }

    /// <summary>Saves one catch-up of a session: its facts, the roll-ups and the cursor, in one transaction.</summary>
    /// <param name="request">What to save.</param>
    /// <param name="cancellationToken">A token to cancel the write.</param>
    /// <returns>What changed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
    public async ValueTask<ApplyResult> ApplyAsync(ApplyRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ApplyResult? result = null;
        await WriteAsync(sql => result = Apply(sql, request), cancellationToken).ConfigureAwait(false);
        return result!;
    }

    /// <summary>Removes the facts of the sessions whose journal is gone.</summary>
    /// <param name="cancellationToken">A token to cancel the write.</param>
    /// <returns>The number of sessions forgotten and the days whose numbers changed.</returns>
    public async ValueTask<(int Sessions, IReadOnlyList<int> ChangedDays)> ForgetDeletedAsync(CancellationToken cancellationToken = default)
    {
        var sessions = 0;
        IReadOnlyList<int> days = [];
        await WriteAsync(
            sql =>
            {
                var ids = sql.Query($"SELECT session_id FROM {Table("journal")} WHERE deleted = 1", static reader => reader.GetString(0));
                var quarters = new HashSet<int>();
                foreach (var id in ids)
                {
                    RemoveSession(sql, id, quarters);
                    sql.Execute($"DELETE FROM {Table("session")} WHERE session_id = @p0", id);
                    sql.Execute($"DELETE FROM {Table("journal")} WHERE session_id = @p0", id);
                }

                sessions = ids.Count;
                days = RecomputeRollups(sql, quarters);
            },
            cancellationToken).ConfigureAwait(false);
        return (sessions, days);
    }

    private ApplyResult Apply(SqlSession sql, ApplyRequest request)
    {
        var batch = request.Batch;
        var session = request.SessionId;
        var floor = request.FloorQuarter;
        var oldQuarters = new HashSet<int>();
        if (request.Replace)
        {
            RemoveSession(sql, session, oldQuarters);
        }

        var newQuarters = new HashSet<int>();
        var rows = 0;
        foreach (var table in FactTables.All)
        {
            var factRows = table.Extract(batch).Where(row => row.Quarter >= floor).ToList();
            var upsert = table.UpsertSql(Prefix, RollupLevel.Quarter);
            foreach (var row in factRows)
            {
                sql.Execute(upsert, [session, (long)row.Quarter, .. row.Keys, .. Boxed(row.Measures)]);
                newQuarters.Add(row.Quarter);
                rows++;
            }

            if (!request.Replace)
            {
                AddToRollups(sql, table, factRows);
            }
        }

        var extremes = ExtremeRows(batch, floor);
        foreach (var extreme in extremes)
        {
            sql.Execute(ExtremeUpsertSql(RollupLevel.Quarter), session, (long)extreme.Quarter, (long)extreme.Measure, extreme.Subject, extreme.Value, extreme.RunId, extreme.AtMs);
            newQuarters.Add(extreme.Quarter);
            rows++;
        }

        if (!request.Replace)
        {
            AddExtremesToRollups(sql, extremes);
        }

        IReadOnlyList<int> changed;
        if (request.Replace)
        {
            oldQuarters.UnionWith(newQuarters);
            changed = RecomputeRollups(sql, oldQuarters);
        }
        else
        {
            changed = [.. newQuarters.Select(Days.DayOf).Distinct().Order()];
        }

        WriteRuns(sql, session, batch, floor);
        WriteSession(sql, batch);
        WriteJournal(sql, request);
        if (request.Meta is { Count: > 0 } meta)
        {
            SetMeta(sql, meta.ToDictionary(static pair => pair.Key, static pair => (string?)pair.Value, StringComparer.Ordinal));
        }

        return new ApplyResult(changed, rows);
    }

    private void RemoveSession(SqlSession sql, string session, HashSet<int> quarters)
    {
        foreach (var table in FactTables.All)
        {
            var name = table.TableName(Prefix, RollupLevel.Quarter);
            foreach (var quarter in sql.Query($"SELECT DISTINCT q FROM {name} WHERE session_id = @p0", static reader => (int)reader.GetInt64(0), session))
            {
                quarters.Add(quarter);
            }

            sql.Execute($"DELETE FROM {name} WHERE session_id = @p0", session);
        }

        var extreme = Table("extreme_q");
        foreach (var quarter in sql.Query($"SELECT DISTINCT q FROM {extreme} WHERE session_id = @p0", static reader => (int)reader.GetInt64(0), session))
        {
            quarters.Add(quarter);
        }

        sql.Execute($"DELETE FROM {extreme} WHERE session_id = @p0", session);
        sql.Execute($"DELETE FROM {Table("run")} WHERE session_id = @p0", session);
    }

    private void WriteRuns(SqlSession sql, string session, FactBatch batch, int floor)
    {
        if (batch.Runs.Count == 0)
        {
            return;
        }

        var insert = $"INSERT OR REPLACE INTO {Table("run")} (session_id, run_id, start_ms, end_ms, outcome, sender, prompt_kind, prompt_chars, prompt_words, requests, tool_calls, tool_failures, input_tokens, output_tokens, compactions, answer_chars, answer_words, cost_usd_micro, cost_credits_micro, provider, model, effort, permission_mode) "
            + "VALUES (@p0, @p1, @p2, @p3, @p4, @p5, @p6, @p7, @p8, @p9, @p10, @p11, @p12, @p13, @p14, @p15, @p16, @p17, @p18, @p19, @p20, @p21, @p22)";
        foreach (var run in batch.Runs.Values)
        {
            if (floor != int.MinValue && QuarterHour.Of(run.Start).Index < floor)
            {
                continue;
            }

            sql.Execute(
                insert,
                session,
                run.RunId,
                run.Start.ToUnixTimeMilliseconds(),
                run.End.ToUnixTimeMilliseconds(),
                (long)run.Outcome,
                (long)run.Sender,
                (long)run.PromptKind,
                run.PromptChars,
                run.PromptWords,
                run.Requests,
                run.ToolCalls,
                run.ToolFailures,
                run.InputTokens,
                run.OutputTokens,
                run.Compactions,
                run.AnswerChars,
                run.AnswerWords,
                Micro(run.CostUsd),
                Micro(run.CostCredits),
                run.Provider,
                run.Model,
                run.Effort,
                run.PermissionMode);
        }
    }

    private void WriteSession(SqlSession sql, FactBatch batch)
    {
        if (batch.Session is not { } row)
        {
            return;
        }

        sql.Execute(
            $"INSERT INTO {Table("session")} (session_id, project_ref, session_kind, parent_session_id, created_by_kind, created_by_session_id, automation_id, title, provider, permission_mode, first_ms, last_ms, deleted) "
            + "VALUES (@p0, @p1, @p2, @p3, @p4, @p5, @p6, @p7, @p8, @p9, @p10, @p11, 0) "
            + "ON CONFLICT (session_id) DO UPDATE SET project_ref = excluded.project_ref, session_kind = excluded.session_kind, parent_session_id = excluded.parent_session_id, created_by_kind = excluded.created_by_kind, "
            + "created_by_session_id = excluded.created_by_session_id, automation_id = excluded.automation_id, title = excluded.title, provider = excluded.provider, permission_mode = excluded.permission_mode, "
            + "first_ms = excluded.first_ms, last_ms = excluded.last_ms, deleted = 0",
            row.SessionId,
            row.ProjectRef,
            row.SessionKind,
            row.ParentSessionId,
            row.CreatedByKind,
            row.CreatedBySessionId,
            row.AutomationId,
            row.Title,
            row.Provider,
            row.PermissionMode,
            row.FirstRecord?.ToUnixTimeMilliseconds(),
            row.LastRecord?.ToUnixTimeMilliseconds());
    }

    private void WriteJournal(SqlSession sql, ApplyRequest request)
    {
        var cursor = request.Cursor;
        sql.Execute(
            $"INSERT OR REPLACE INTO {Table("journal")} (session_id, read_offset, file_length, file_stamp, first_line_length, first_line_hash, state, facts_version, floor_q, updated_ms, open_runs, deleted) "
            + "VALUES (@p0, @p1, @p2, @p3, @p4, @p5, @p6, @p7, @p8, @p9, @p10, 0)",
            request.SessionId,
            cursor.Offset,
            request.FileLength,
            request.FileStampTicks,
            cursor.FirstLine?.Length,
            cursor.FirstLine is { } mark ? unchecked((long)mark.Hash) : null,
            cursor.State.ToUtf8Json(),
            (long)request.FactsVersion,
            (long)request.FloorQuarter,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            (long)cursor.State.OpenRuns.Count);
    }

    private string SelectJournal()
        => $"SELECT session_id, read_offset, file_length, file_stamp, first_line_length, first_line_hash, state, facts_version, floor_q, deleted, open_runs FROM {Table("journal")}";

    private static JournalRow ReadJournal(SqliteDataReader reader)
        => new(
            reader.GetString(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            reader.IsDBNull(4) ? null : new JournalFingerprint(reader.GetInt64(4), unchecked((ulong)reader.GetInt64(5))),
            (byte[])reader.GetValue(6),
            (int)reader.GetInt64(7),
            (int)reader.GetInt64(8),
            reader.GetInt64(9) != 0,
            (int)reader.GetInt64(10));

    private void SetMeta(SqlSession sql, IReadOnlyDictionary<string, string?> values)
    {
        foreach (var (key, value) in values)
        {
            if (value is null)
            {
                sql.Execute($"DELETE FROM {Table("meta")} WHERE key = @p0", key);
            }
            else
            {
                sql.Execute($"INSERT INTO {Table("meta")} (key, value) VALUES (@p0, @p1) ON CONFLICT (key) DO UPDATE SET value = excluded.value", key, value);
            }
        }
    }

    private static long Micro(double value) => (long)Math.Round(value * 1_000_000d, MidpointRounding.AwayFromZero);

    private static object?[] Boxed(long[] values)
    {
        var boxed = new object?[values.Length];
        for (var index = 0; index < values.Length; index++)
        {
            boxed[index] = values[index];
        }

        return boxed;
    }
}
