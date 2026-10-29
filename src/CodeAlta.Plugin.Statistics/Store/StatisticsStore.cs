using System.Globalization;
using CodeAlta.Catalog;
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
/// <para>No row holds text of a session but its title, in the form <see cref="SessionTitles.Clean"/> gives it, and the name of its project.</para>
/// </remarks>
internal sealed partial class StatisticsStore
{
    /// <summary>The version of the tables, kept by the host for the plugin.</summary>
    public const int SchemaVersion = 7;

    /// <summary>The version of the roll-ups: when it changes, they are added up again from the facts.</summary>
    public const int RollupVersion = 1;

    /// <summary>The key of the version of the roll-ups in the <c>meta</c> table.</summary>
    public const string RollupVersionKey = "rollup.version";

    /// <summary>The key of the time zone of the roll-ups in the <c>meta</c> table.</summary>
    public const string RollupTimeZoneKey = "rollup.timezone";

    /// <summary>
    /// The key, in the <c>meta</c> table, of the sessions that still wait for the key of their folder from their header: the last
    /// session that got it, in the order of their identifiers (empty to start). Absent when none waits, as in a store created with
    /// the column.
    /// </summary>
    public const string FolderBackfillKey = "folders.backfill_after";

    private const string DataUpdatedKey = "data.updated_ms";
    private readonly IPluginDatabase _database;

    /// <summary>Initializes the store over the database of the plugin.</summary>
    /// <param name="database">The database service the host gives the plugin.</param>
    /// <param name="days">The local days of the time zone of the roll-ups.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="InvalidOperationException">The host has no database.</exception>
    public StatisticsStore(IPluginDatabase database, LocalDays days)
        : this(database, days, null)
    {
    }

    internal StatisticsStore(IPluginDatabase database, LocalDays days, StatisticsArchiveStore? archives)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(days);
        if (!database.HasDatabase)
        {
            throw new InvalidOperationException("The host has no application database for the statistics.");
        }

        _database = database;
        _archives = archives;
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
    public ValueTask InitializeAsync(CancellationToken cancellationToken = default) => InitializeAsync(restoreArchives: true, cancellationToken);

    // The engine restores the saved sessions itself once it knows which journals are present: a second pass here would only
    // read the archive again.
    internal async ValueTask InitializeAsync(bool restoreArchives, CancellationToken cancellationToken)
    {
        await _database.MigrateAsync(SchemaVersion, (connection, from, to, token) =>
        {
            if (from < 1)
            {
                using var sql = new SqlSession(connection);
                sql.ExecuteScript(SchemaV1());
            }

            if (from < 2)
            {
                // The time between the start and the end of a run that is not time of the run (see RunRow.SkippedMs).
                using var sql = new SqlSession(connection);
                sql.Execute($"ALTER TABLE {Table("run")} ADD COLUMN skipped_ms INTEGER NOT NULL DEFAULT 0");
            }

            if (from < 3)
            {
                using var sql = new SqlSession(connection);
                sql.Execute($"CREATE TABLE {Table("archive")} (session_id TEXT PRIMARY KEY, generation TEXT NOT NULL, facts_version INTEGER NOT NULL, full_source INTEGER NOT NULL, recorded_metrics TEXT NOT NULL, effective INTEGER NOT NULL, source_seen INTEGER NOT NULL)");
            }

            if (from < 4)
            {
                // The space a session records: a chat is counted in its own space. Sessions read before have none,
                // and are counted in the default space, as the window lists them.
                using var sql = new SqlSession(connection);
                if (sql.ScalarLong($"SELECT COUNT(*) FROM pragma_table_info('{Table("session")}') WHERE name = 'space_id'") == 0)
                    sql.Execute($"ALTER TABLE {Table("session")} ADD COLUMN space_id TEXT");
            }

            if (from < 5)
            {
                // The key of the folder of a session (SessionFolders): a session whose project the catalog does not have counts
                // under the project of its folder. The sessions read before get it from the header of their journal, once, in the
                // background (StatisticsEngine.BackfillFolderKeysAsync): their facts are not read again.
                using var sql = new SqlSession(connection);
                if (sql.ScalarLong($"SELECT COUNT(*) FROM pragma_table_info('{Table("session")}') WHERE name = 'folder_key'") == 0)
                    sql.Execute($"ALTER TABLE {Table("session")} ADD COLUMN folder_key TEXT");
                sql.Execute($"CREATE INDEX IF NOT EXISTS {Table("session")}_by_folder ON {Table("session")} (folder_key)");
                if (from >= 1)
                    SetMeta(sql, new Dictionary<string, string?> { [FolderBackfillKey] = string.Empty });
            }

            if (from < 6)
            {
                // The runs of scripts of Code Mode: their family of facts, a row per run, the calls scripts make, the exec calls of a
                // turn and the run of the script that started a session. A store created at this version has the tables of its facts
                // already (SchemaV1 creates every family); the sessions are read again for version 9 of the facts.
                using var sql = new SqlSession(connection);
                bool HasTable(string name) => sql.ScalarLong("SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = @p0", Table(name)) != 0;
                bool HasColumn(string table, string column) => sql.ScalarLong($"SELECT COUNT(*) FROM pragma_table_info('{Table(table)}') WHERE name = @p0", column) != 0;
                foreach (var level in new[] { RollupLevel.Quarter, RollupLevel.Day, RollupLevel.Month, RollupLevel.Year })
                {
                    if (!HasTable(FactTables.Scripts.Name + level.Suffix()))
                        sql.ExecuteScript(FactTables.Scripts.CreateSql(Prefix, level));
                    if (!HasColumn(FactTables.Tools.Name + level.Suffix(), "script_calls"))
                        sql.Execute($"ALTER TABLE {FactTables.Tools.TableName(Prefix, level)} ADD COLUMN script_calls INTEGER NOT NULL DEFAULT 0");
                }

                if (!HasColumn("run", "exec_calls"))
                    sql.Execute($"ALTER TABLE {Table("run")} ADD COLUMN exec_calls INTEGER NOT NULL DEFAULT 0");
                if (!HasColumn("session", "script_run_id"))
                    sql.Execute($"ALTER TABLE {Table("session")} ADD COLUMN script_run_id TEXT");
                sql.Execute($"CREATE INDEX IF NOT EXISTS {Table("session")}_by_script_run ON {Table("session")} (script_run_id)");
                sql.ExecuteScript($"CREATE TABLE IF NOT EXISTS {Table("script_run")} (session_id TEXT NOT NULL, run_id TEXT NOT NULL, script TEXT NOT NULL, origin TEXT NOT NULL, title TEXT, status TEXT NOT NULL, error TEXT, "
                    + "start_ms INTEGER NOT NULL, end_ms INTEGER, duration_ms INTEGER, resumes_run_id TEXT, provider TEXT NOT NULL, model TEXT NOT NULL, calls INTEGER NOT NULL, steps INTEGER NOT NULL, "
                    + "steps_failed INTEGER NOT NULL, sessions INTEGER NOT NULL, wait_ms INTEGER NOT NULL, PRIMARY KEY (session_id, run_id)) WITHOUT ROWID;"
                    + $"CREATE INDEX IF NOT EXISTS {Table("script_run")}_by_start ON {Table("script_run")} (start_ms);");
            }

            if (from < 7)
            {
                // The row counts of a saved session, kept with its mark: a start checks that its rows are all there without reading its
                // saved copy. A mark written before has none, and gets them at the next start that finds its rows.
                using var sql = new SqlSession(connection);
                if (sql.ScalarLong($"SELECT COUNT(*) FROM pragma_table_info('{Table("archive")}') WHERE name = 'row_counts'") == 0)
                    sql.Execute($"ALTER TABLE {Table("archive")} ADD COLUMN row_counts TEXT");
            }

            return ValueTask.CompletedTask;
        }, cancellationToken).ConfigureAwait(false);

        var version = await GetMetaAsync(RollupVersionKey, cancellationToken).ConfigureAwait(false);
        var zone = await GetMetaAsync(RollupTimeZoneKey, cancellationToken).ConfigureAwait(false);
        if (version != RollupVersion.ToString(CultureInfo.InvariantCulture) || zone != Days.TimeZone.Id)
        {
            await RebuildRollupsAsync(cancellationToken).ConfigureAwait(false);
        }

        if (restoreArchives)
        {
            await RestoreArchivesAsync(cancellationToken).ConfigureAwait(false);
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

    /// <summary>Lists the sessions that have no key of their folder, after a session in the order of their identifiers.</summary>
    /// <param name="after">The last session listed before; empty for the first.</param>
    /// <param name="limit">The most sessions listed.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The sessions, in the order of their identifiers.</returns>
    internal ValueTask<IReadOnlyList<string>> ListSessionsWithoutFolderAsync(string after, int limit, CancellationToken cancellationToken = default)
        => ReadAsync<IReadOnlyList<string>>(
            sql => sql.Query($"SELECT session_id FROM {Table("session")} WHERE folder_key IS NULL AND session_id > @p0 ORDER BY session_id LIMIT @p1", static reader => reader.GetString(0), after, (long)limit),
            cancellationToken);

    /// <summary>Gives sessions the keys of their folders, where they have none yet, and saves how far the backfill went, in one transaction.</summary>
    /// <param name="keys">The sessions and the keys of their folders.</param>
    /// <param name="after">The last session the backfill looked at; null when it is over.</param>
    /// <param name="cancellationToken">A token to cancel the write.</param>
    /// <returns>A task representing the write.</returns>
    internal ValueTask SetFolderKeysAsync(IReadOnlyList<(string SessionId, string FolderKey)> keys, string? after, CancellationToken cancellationToken = default)
        => WriteAsync(
            sql =>
            {
                foreach (var (sessionId, folderKey) in keys)
                {
                    sql.Execute($"UPDATE {Table("session")} SET folder_key = @p1 WHERE session_id = @p0 AND folder_key IS NULL", sessionId, folderKey);
                }

                SetMeta(sql, new Dictionary<string, string?> { [FolderBackfillKey] = after });
            },
            cancellationToken);

    /// <summary>Reads the last committed data update in the caller's snapshot; older databases retain their journal catch-up time.</summary>
    internal DateTimeOffset? ReadDataUpdatedAt(SqlSession sql)
    {
        var milliseconds = sql.ScalarLong($"SELECT COALESCE((SELECT CAST(value AS INTEGER) FROM {Table("meta")} WHERE key=@p0), (SELECT MAX(updated_ms) FROM {Table("journal")}))", DataUpdatedKey);
        return milliseconds > 0 && milliseconds <= DateTimeOffset.MaxValue.ToUnixTimeMilliseconds()
            ? DateTimeOffset.FromUnixTimeMilliseconds(milliseconds) : null;
    }

    // Called inside the same write transaction as the data, never on a query, status publication or page open.
    private void RecordDataUpdate(SqlSession sql)
        => sql.Execute($"INSERT INTO {Table("meta")} (key, value) VALUES (@p0, @p1) ON CONFLICT (key) DO UPDATE SET value=excluded.value",
            DataUpdatedKey, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture));

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
                    var changed = 0;
                    foreach (var id in sessionIds)
                    {
                        changed += sql.Execute($"UPDATE {Table("journal")} SET deleted = 1 WHERE session_id = @p0 AND deleted = 0", id);
                        sql.Execute($"UPDATE {Table("session")} SET deleted = 1 WHERE session_id = @p0", id);
                    }

                    if (changed > 0) RecordDataUpdate(sql);
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
                var ids = sql.Query($"SELECT session_id FROM {Table("journal")} WHERE deleted = 1 AND session_id NOT IN (SELECT session_id FROM {Table("archive")})", static reader => reader.GetString(0));
                var quarters = new HashSet<int>();
                foreach (var id in ids)
                {
                    RemoveSession(sql, id, quarters);
                    sql.Execute($"DELETE FROM {Table("session")} WHERE session_id = @p0", id);
                    sql.Execute($"DELETE FROM {Table("journal")} WHERE session_id = @p0", id);
                }

                sessions = ids.Count;
                days = RecomputeRollups(sql, quarters);
                if (sessions > 0) RecordDataUpdate(sql);
            },
            cancellationToken).ConfigureAwait(false);
        return (sessions, days);
    }

    /// <summary>
    /// Empties every table of the plugin: the facts, the roll-ups, the sessions, where each journal was read to, and the choice of the user.
    /// The tables stay, at their version.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the write.</param>
    /// <returns>A task representing the write.</returns>
    public async ValueTask ResetAsync(CancellationToken cancellationToken = default)
    {
        // Validate the irreplaceable archive BEFORE clearing anything: every saved copy is read and checked (Payload).
        // Migration is idempotent and never issues deletion receipts.
        foreach (var archive in await ReadArchivesAsync(cancellationToken).ConfigureAwait(false)) _ = archive.Payload;
        await PreserveDeletedAsync(cancellationToken).ConfigureAwait(false);
        var archives = await ReadArchivesAsync(cancellationToken).ConfigureAwait(false);
        foreach (var archive in archives) _ = archive.Payload;
        await WriteAsync(
            sql =>
            {
                // The prefix is the name of the plugin followed by an underscore, which LIKE reads as any character: compare the start of the name.
                var tables = sql.Query(
                    "SELECT name FROM sqlite_master WHERE type = 'table' AND substr(name, 1, length(@p0)) = @p0",
                    static reader => reader.GetString(0),
                    Prefix);
                foreach (var table in tables)
                {
                    sql.Execute($"DELETE FROM {table}");
                }

                foreach (var archive in archives)
                {
                    ImportArchive(sql, archive.Entry, archive.Payload);
                }

                SetMeta(sql, new Dictionary<string, string?> { [RollupVersionKey] = RollupVersion.ToString(CultureInfo.InvariantCulture), [RollupTimeZoneKey] = Days.TimeZone.Id });
                RecordDataUpdate(sql);
            },
            cancellationToken).ConfigureAwait(false);
        SavedSessions = archives.Count;
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
        WriteScriptRuns(sql, session, batch, floor);
        WriteSession(sql, batch);
        WriteJournal(sql, request);
        RecordDataUpdate(sql);
        sql.Execute($"UPDATE {Table("archive")} SET effective=0,source_seen=@p1 WHERE session_id=@p0", session, request.VerifiedArchiveReplacement ? 1L : 0L);
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
        sql.Execute($"DELETE FROM {Table("script_run")} WHERE session_id = @p0", session);
    }

    private void WriteRuns(SqlSession sql, string session, FactBatch batch, int floor)
    {
        if (batch.Runs.Count == 0)
        {
            return;
        }

        var insert = $"INSERT OR REPLACE INTO {Table("run")} (session_id, run_id, start_ms, end_ms, outcome, sender, prompt_kind, prompt_chars, prompt_words, requests, tool_calls, tool_failures, input_tokens, output_tokens, compactions, answer_chars, answer_words, cost_usd_micro, cost_credits_micro, provider, model, effort, permission_mode, skipped_ms, exec_calls) "
            + "VALUES (@p0, @p1, @p2, @p3, @p4, @p5, @p6, @p7, @p8, @p9, @p10, @p11, @p12, @p13, @p14, @p15, @p16, @p17, @p18, @p19, @p20, @p21, @p22, @p23, @p24)";
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
                run.PermissionMode,
                run.SkippedMs,
                run.ExecCalls);
        }
    }

    private void WriteScriptRuns(SqlSession sql, string session, FactBatch batch, int floor)
    {
        if (batch.ScriptRuns.Count == 0)
        {
            return;
        }

        var insert = $"INSERT OR REPLACE INTO {Table("script_run")} (session_id, run_id, script, origin, title, status, error, start_ms, end_ms, duration_ms, resumes_run_id, provider, model, calls, steps, steps_failed, sessions, wait_ms) "
            + "VALUES (@p0, @p1, @p2, @p3, @p4, @p5, @p6, @p7, @p8, @p9, @p10, @p11, @p12, @p13, @p14, @p15, @p16, @p17)";
        foreach (var run in batch.ScriptRuns.Values)
        {
            if (floor != int.MinValue && QuarterHour.Of(run.Start).Index < floor)
            {
                continue;
            }

            sql.Execute(
                insert,
                session,
                run.RunId,
                run.Script,
                run.Origin,
                run.Title,
                run.Status,
                run.Error,
                run.Start.ToUnixTimeMilliseconds(),
                run.End?.ToUnixTimeMilliseconds(),
                run.DurationMs,
                run.ResumesRunId,
                run.Provider,
                run.Model,
                run.Calls,
                run.Steps,
                run.StepsFailed,
                run.Sessions,
                run.WaitMs);
        }
    }

    private void WriteSession(SqlSession sql, FactBatch batch)
    {
        if (batch.Session is not { } row)
        {
            return;
        }

        sql.Execute(
            $"INSERT INTO {Table("session")} (session_id, project_ref, session_kind, parent_session_id, created_by_kind, created_by_session_id, automation_id, title, provider, permission_mode, first_ms, last_ms, deleted, space_id, folder_key, script_run_id) "
            + "VALUES (@p0, @p1, @p2, @p3, @p4, @p5, @p6, @p7, @p8, @p9, @p10, @p11, 0, @p12, @p13, @p14) "
            + "ON CONFLICT (session_id) DO UPDATE SET project_ref = excluded.project_ref, session_kind = excluded.session_kind, parent_session_id = excluded.parent_session_id, created_by_kind = excluded.created_by_kind, "
            + "created_by_session_id = excluded.created_by_session_id, automation_id = excluded.automation_id, title = excluded.title, provider = excluded.provider, permission_mode = excluded.permission_mode, "
            + "first_ms = excluded.first_ms, last_ms = excluded.last_ms, deleted = 0, space_id = excluded.space_id, folder_key = COALESCE(excluded.folder_key, folder_key), script_run_id = excluded.script_run_id",
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
            row.LastRecord?.ToUnixTimeMilliseconds(),
            row.SpaceId,
            row.FolderKey,
            row.ScriptRunId);
    }

    private void WriteJournal(SqlSession sql, ApplyRequest request, long? updatedMilliseconds = null)
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
            updatedMilliseconds ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
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
