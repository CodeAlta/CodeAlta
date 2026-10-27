using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Plugin.Statistics.Facts;

namespace CodeAlta.Plugin.Statistics.Store;

internal sealed partial class StatisticsStore
{
    private readonly StatisticsArchiveStore? _archives;

    internal bool HasArchive => _archives is not null;
    internal int SavedSessions { get; private set; }

    // Version 2 of the saved statistics adds the runs of scripts (Code Mode): their facts and rows, the calls scripts make, the
    // exec calls of a run and the run of the script that started a session. A copy saved at version 1 stays readable as it was.
    private const int CurrentArchiveVersion = 2;

    private static IReadOnlyList<(string Name, string[] Columns)> ArchiveTables(int version = CurrentArchiveVersion)
    {
        string[] run = ["session_id", "run_id", "start_ms", "end_ms", "outcome", "sender", "prompt_kind", "prompt_chars", "prompt_words", "requests", "tool_calls", "tool_failures", "input_tokens", "output_tokens", "compactions", "answer_chars", "answer_words", "cost_usd_micro", "cost_credits_micro", "provider", "model", "effort", "permission_mode", "skipped_ms"];
        // `deleted` stays the last column of a session and `title` the eighth: the export and the checks name them by place.
        string[] session = ["session_id", "project_ref", "session_kind", "parent_session_id", "created_by_kind", "created_by_session_id", "automation_id", "title", "provider", "permission_mode", "first_ms", "last_ms"];
        var facts = FactTables.All
            .Where(table => version >= 2 || table != FactTables.Scripts)
            .Select(table => (table.Name + "_q", table.KeyColumns(RollupLevel.Quarter)
                .Concat(table.Measures.Select(measure => measure.Name).Where(name => version >= 2 || !(table == FactTables.Tools && name == "script_calls"))).ToArray()));
        List<(string Name, string[] Columns)> tables =
        [
            .. facts,
            ("extreme_q", ["session_id", "q", "measure", "subject", "value", "run_id", "at_ms"]),
            ("run", version >= 2 ? [.. run, "exec_calls"] : run),
            ("session", version >= 2 ? [.. session, "script_run_id", "deleted"] : [.. session, "deleted"]),
            ("project", ["project_ref", "name", "updated_ms"]),
        ];
        if (version >= 2)
        {
            tables.Add(("script_run", ["session_id", "run_id", "script", "origin", "title", "status", "error", "start_ms", "end_ms", "duration_ms", "resumes_run_id", "provider", "model", "calls", "steps", "steps_failed", "sessions", "wait_ms"]));
        }

        return tables;
    }

    internal static string[] CurrentMetrics()
        => [.. FactTables.All.SelectMany(table => table.Measures.Select(measure => table.Name + "." + measure.Name)), "extreme", "run", "run.skipped_ms", "session.lineage", "run.exec_calls", "session.script_run", "script_run"];

    internal async ValueTask<StatisticsArchiveEntry> SaveArchiveAsync(string sessionId, SessionStatisticsSource? source, CancellationToken token)
    {
        if (_archives is null)
        {
            throw new InvalidOperationException("No durable Statistics archive was supplied by the host.");
        }

        var payload = await ReadAsync(sql => ExportArchive(sql, sessionId, source), token).ConfigureAwait(false);
        ValidatePayload(payload, sessionId, source);
        var entry = await _archives.SaveAsync(sessionId, payload.Serialize(), source, token).ConfigureAwait(false);
        ValidatePayload(StatisticsArchivePayload.Deserialize(entry.Payload), entry.SessionId, entry.Source);
        await WriteAsync(sql => ProtectArchive(sql, entry, payload, effective: false), token).ConfigureAwait(false);
        return entry;
    }

    internal async ValueTask<bool> VerifyArchiveAsync(SessionStatisticsRetirementReceipt receipt, CancellationToken token)
    {
        if (_archives is null)
        {
            return false;
        }

        var entries = await _archives.ReadAsync(token).ConfigureAwait(false);
        var entry = entries.FirstOrDefault(item => item.SessionId == receipt.Source.SessionId && item.Generation == receipt.Generation && item.PayloadSha256 == receipt.PayloadSha256 && item.Source == receipt.Source);
        if (entry is null)
        {
            return false;
        }

        var payload = StatisticsArchivePayload.Deserialize(entry.Payload);
        ValidatePayload(payload, entry.SessionId, entry.Source);
        return payload.FullSourceVerified && payload.FactsVersion == SessionFactsState.CurrentVersion;
    }

    internal async ValueTask<bool> ReconcileDeletedArchiveAsync(SessionStatisticsRetirementReceipt receipt, bool artifactsRemaining, CancellationToken token)
    {
        if (_archives is null || !await _archives.RecordDeletionAsync(receipt, artifactsRemaining, token).ConfigureAwait(false)) return false;
        await MarkDeletedAsync([receipt.Source.SessionId], token).ConfigureAwait(false);
        // Only this session changed: importing every saved session again would make each deletion of a cleanup slower than
        // the one before. Startup and reset restore them all.
        await RestoreArchivesAsync(token, (await ListJournalsAsync(token).ConfigureAwait(false)).Values.Where(row => !row.Deleted).Select(row => row.SessionId).ToHashSet(StringComparer.OrdinalIgnoreCase),
            receipt.Source.SessionId).ConfigureAwait(false);
        return true;
    }

    private StatisticsArchivePayload ExportArchive(SqlSession sql, string sessionId, SessionStatisticsSource? source)
    {
        var journal = sql.Query(SelectJournal() + " WHERE session_id = @p0", ReadJournal, sessionId).Single();
        var tables = new List<ArchiveTable>();
        foreach (var (name, columns) in ArchiveTables())
        {
            var filter = name == "project" ? $"project_ref IN (SELECT project_ref FROM {Table("session")} WHERE session_id=@p0)" : "session_id=@p0";
            var rows = sql.Query($"SELECT {string.Join(',', columns)} FROM {Table(name)} WHERE {filter} ORDER BY {string.Join(',', columns)}", reader =>
            {
                var cells = new ArchiveCell[columns.Length];
                for (var index = 0; index < cells.Length; index++)
                {
                    cells[index] = reader.IsDBNull(index) ? new ArchiveCell() : reader.GetValue(index) is string text ? new ArchiveCell(Text: text) : new ArchiveCell(reader.GetInt64(index));
                }

                return cells;
            }, sessionId);
            // Deletion is projection state, not part of the statistics; imports are always a saved (deleted) projection.
            if (name == "session")
            {
                foreach (var row in rows)
                {
                    row[^1] = new ArchiveCell(1);
                    row[7] = new ArchiveCell(Text: SessionTitles.Clean(row[7].Text));
                }
            }

            tables.Add(new ArchiveTable(name, columns, [.. rows]));
        }

        return new StatisticsArchivePayload
        {
            ArchiveVersion = CurrentArchiveVersion,
            SessionId = sessionId,
            FactsVersion = journal.FactsVersion,
            Source = source,
            ReadOffset = journal.Offset,
            SourceLength = journal.FileLength,
            FloorQuarter = journal.FloorQuarter,
            FullSourceVerified = source is not null,
            // A historical reducer version is not evidence that today's definitions were recorded.
            RecordedMetrics = journal.FactsVersion == SessionFactsState.CurrentVersion ? CurrentMetrics() : [],
            Tables = [.. tables],
        };
    }

    private static void ValidatePayload(StatisticsArchivePayload payload, string sessionId, SessionStatisticsSource? source)
    {
        if (payload.ArchiveVersion is < 1 or > CurrentArchiveVersion || payload.FactsVersion < 1 || payload.FactsVersion > SessionFactsState.CurrentVersion || payload.SessionId != sessionId || payload.Source != source
            // A partial legacy cursor stores length=0 until EOF. Preserve that unknown-length sentinel, not an invented total.
            || payload.ReadOffset < 0 || payload.SourceLength < 0 || (payload.SourceLength != 0 && payload.SourceLength < payload.ReadOffset) || payload.FullSourceVerified != (source is not null)
            || (source is not null && (source.SessionId != sessionId || payload.ReadOffset != source.Length || payload.SourceLength != source.Length || payload.FloorQuarter != int.MinValue)))
        {
            throw new InvalidDataException("Unsupported or inconsistent saved Statistics coverage; the existing projection was not reset.");
        }

        var definitions = ArchiveTables(payload.ArchiveVersion);
        if (payload.Tables.Length != definitions.Count || payload.Tables.Select(table => table.Name).Distinct(StringComparer.Ordinal).Count() != definitions.Count
            || payload.RecordedMetrics.Except(CurrentMetrics(), StringComparer.Ordinal).Any())
        {
            throw new InvalidDataException("Unsupported saved Statistics tables or metrics.");
        }

        foreach (var (name, columns) in definitions)
        {
            var table = payload.Tables.SingleOrDefault(table => table.Name == name);
            if (table is null || !table.Columns.SequenceEqual(columns, StringComparer.Ordinal)
                || table.Rows.Any(row => row.Length != columns.Length || row.Any(cell => cell is null || (cell.Number is not null && cell.Text is not null)))
                || (name != "project" && table.Rows.Any(row => row[0].Text != sessionId))
                || (name == "session" && (table.Rows.Length != 1 || table.Rows[0][^1].Number != 1)))
            {
                throw new InvalidDataException("Saved Statistics has invalid base rows or session identity.");
            }
        }
    }

    private async ValueTask<List<(StatisticsArchiveEntry Entry, StatisticsArchivePayload Payload)>> ReadArchivesAsync(CancellationToken token, string? sessionId = null)
    {
        var result = new List<(StatisticsArchiveEntry, StatisticsArchivePayload)>();
        if (_archives is not null)
        {
            var entries = await _archives.ReadAsync(token).ConfigureAwait(false);
            SavedSessions = entries.Count;
            foreach (var entry in entries)
            {
                if (sessionId is not null && !string.Equals(entry.SessionId, sessionId, StringComparison.OrdinalIgnoreCase)) continue;
                var payload = StatisticsArchivePayload.Deserialize(entry.Payload);
                ValidatePayload(payload, entry.SessionId, entry.Source);
                result.Add((entry, payload));
            }
        }

        return result;
    }

    // With a session, only that session's saved copy is considered.
    internal async ValueTask RestoreArchivesAsync(CancellationToken token, IReadOnlySet<string>? present = null, string? sessionId = null)
    {
        List<(StatisticsArchiveEntry Entry, StatisticsArchivePayload Payload)>? archives = null;
        if (present is not null && _archives is not null && sessionId is null)
        {
            // A returning journal can supersede an archive, then disappear again. Preserve those newer facts first.
            archives = await ReadArchivesAsync(token).ConfigureAwait(false);
            if (await PreserveDeletedAsync(token, existingOnly: true, archives).ConfigureAwait(false)) archives = null;
        }

        archives ??= await ReadArchivesAsync(token, sessionId).ConfigureAwait(false);
        if (archives.Count == 0) return;
        await WriteAsync(sql =>
        {
            var journals = sql.Query($"SELECT session_id, deleted FROM {Table("journal")}", static reader => (Id: reader.GetString(0), Deleted: reader.GetInt64(1) != 0))
                .ToDictionary(static row => row.Id, static row => row.Deleted, StringComparer.Ordinal);
            var marks = sql.Query($"SELECT session_id, generation, effective FROM {Table("archive")}", static reader => (Id: reader.GetString(0), Generation: reader.GetString(1), Effective: reader.GetInt64(2) != 0))
                .ToDictionary(static row => row.Id, StringComparer.Ordinal);
            Dictionary<string, Dictionary<string, long>>? counts = null;
            foreach (var (entry, payload) in archives)
            {
                var journalExists = journals.TryGetValue(entry.SessionId, out var deleted);
                var currentGeneration = marks.TryGetValue(entry.SessionId, out var mark) && mark.Generation == entry.Generation;
                // A journal that came back replaced these saved facts with what it holds; they return when it is gone again.
                if (journalExists && currentGeneration && !mark.Effective && (present is null || present.Contains(entry.SessionId))) continue;
                // Already the projection: importing it again deletes and inserts the same rows and adds the roll-ups up again, for
                // every saved session at every start. Only an import or a reread of the journal (which ends the effective mark)
                // changes these rows, so rows that are still all there are this generation's.
                var effective = journalExists && currentGeneration && mark.Effective;
                if (effective && deleted && HoldsRows(entry.SessionId, payload, counts ??= CountArchivedRows(sql))) continue;
                ImportArchive(sql, entry, payload, unchanged: effective);
            }
        }, token).ConfigureAwait(false);
    }

    // The rows of each session table, by effective saved session: a few grouped reads instead of one per session and table.
    private Dictionary<string, Dictionary<string, long>> CountArchivedRows(SqlSession sql)
    {
        var counts = new Dictionary<string, Dictionary<string, long>>(StringComparer.Ordinal);
        foreach (var (name, _) in ArchiveTables())
        {
            if (name == "project") continue;
            counts[name] = sql.Query($"SELECT session_id, COUNT(*) FROM {Table(name)} WHERE session_id IN (SELECT session_id FROM {Table("archive")} WHERE effective=1) GROUP BY session_id",
                    static reader => (Id: reader.GetString(0), Count: reader.GetInt64(1)))
                .ToDictionary(static row => row.Id, static row => row.Count, StringComparer.Ordinal);
        }

        return counts;
    }

    // Project names are shared and renamed after the save: they are not evidence of the session's rows.
    private static bool HoldsRows(string sessionId, StatisticsArchivePayload payload, Dictionary<string, Dictionary<string, long>> counts)
        => payload.Tables.All(table => table.Name == "project" || (counts.TryGetValue(table.Name, out var rows) ? rows.GetValueOrDefault(sessionId) : -1) == table.Rows.Length);

    private void ImportArchive(SqlSession sql, StatisticsArchiveEntry entry, StatisticsArchivePayload payload, bool unchanged = false)
    {
        var quarters = new HashSet<int>();
        RemoveSession(sql, entry.SessionId, quarters);
        sql.Execute($"DELETE FROM {Table("session")} WHERE session_id=@p0", entry.SessionId);
        foreach (var table in payload.Tables)
        {
            var insert = $"INSERT OR REPLACE INTO {Table(table.Name)} ({string.Join(',', table.Columns)}) VALUES ({string.Join(',', table.Columns.Select((_, index) => "@p" + index))})";
            foreach (var row in table.Rows)
            {
                sql.Execute(insert, row.Select(cell => cell.Value).ToArray());
                if (table.Name.EndsWith("_q", StringComparison.Ordinal))
                {
                    quarters.Add(checked((int)row[1].Number!.Value));
                }
            }
        }

        // No reducer checkpoint is archived. A returning journal MUST be read from zero and replace, never append to these facts.
        // Replaying an already effective generation on startup does not make its data fresher, including the legacy fallback.
        long? updatedMilliseconds = unchanged ? sql.ScalarLong($"SELECT updated_ms FROM {Table("journal")} WHERE session_id=@p0", entry.SessionId) : null;
        WriteJournal(sql, new ApplyRequest { SessionId = entry.SessionId, Batch = new FactBatch(entry.SessionId), Cursor = JournalCursor.Start,
            FactsVersion = payload.FactsVersion, FileLength = payload.SourceLength, FloorQuarter = payload.FloorQuarter }, updatedMilliseconds);
        sql.Execute($"UPDATE {Table("journal")} SET deleted=1 WHERE session_id=@p0", entry.SessionId);
        ProtectArchive(sql, entry, payload, effective: true);
        RecomputeRollups(sql, quarters);
        if (!unchanged) RecordDataUpdate(sql);
    }

    private void ProtectArchive(SqlSession sql, StatisticsArchiveEntry entry, StatisticsArchivePayload payload, bool effective)
        => sql.Execute($"INSERT OR REPLACE INTO {Table("archive")} (session_id,generation,facts_version,full_source,recorded_metrics,effective,source_seen) VALUES (@p0,@p1,@p2,@p3,@p4,@p5,0)",
            entry.SessionId, entry.Generation, (long)payload.FactsVersion, payload.FullSourceVerified ? 1L : 0L, string.Join('\n', payload.RecordedMetrics), effective ? 1L : 0L);

    internal ValueTask<bool> IsArchivedProjectionAsync(string sessionId, CancellationToken token)
        => ReadAsync(sql => sql.ScalarLong($"SELECT COUNT(*) FROM {Table("archive")} WHERE session_id=@p0 AND effective=1", sessionId) != 0, token);

    internal ValueTask<bool> HasArchivedGenerationAsync(string sessionId, CancellationToken token)
        => ReadAsync(sql => sql.ScalarLong($"SELECT COUNT(*) FROM {Table("archive")} WHERE session_id=@p0", sessionId) != 0, token);

    // Returns whether a session was saved: the archives read before are then out of date.
    private async ValueTask<bool> PreserveDeletedAsync(CancellationToken token, bool existingOnly = false, IReadOnlyList<(StatisticsArchiveEntry Entry, StatisticsArchivePayload Payload)>? read = null)
    {
        var changed = false;
        var rows = await ListJournalsAsync(token).ConfigureAwait(false);
        var archives = (read ?? await ReadArchivesAsync(token).ConfigureAwait(false)).ToDictionary(pair => pair.Entry.SessionId, StringComparer.OrdinalIgnoreCase);
        // Saving a session below leaves the effective marks of the others as they are.
        var effective = await ReadAsync(sql => sql.Query($"SELECT session_id FROM {Table("archive")} WHERE effective=1", static reader => reader.GetString(0)).ToHashSet(StringComparer.Ordinal), token).ConfigureAwait(false);
        foreach (var row in rows.Values.Where(row => row.Deleted))
        {
            if (existingOnly && !archives.ContainsKey(row.SessionId))
            {
                continue;
            }

            if (effective.Contains(row.SessionId))
            {
                continue;
            }

            if (archives.TryGetValue(row.SessionId, out var previous))
            {
                var sourceSeen = await ReadAsync(sql => sql.ScalarLong($"SELECT COUNT(*) FROM {Table("archive")} WHERE session_id=@p0 AND generation=@p1 AND source_seen=1", row.SessionId, previous.Entry.Generation) != 0, token).ConfigureAwait(false);
                if (!sourceSeen)
                {
                    // A backup can contain older live facts. Only a source observed after this exact durable generation
                    // may supersede it; archive existence or an old journal row is not reread evidence.
                    continue;
                }
            }

            var current = await ReadAsync(sql => ExportArchive(sql, row.SessionId, null), token).ConfigureAwait(false);
            if (archives.TryGetValue(row.SessionId, out var saved)
                && current.Serialize().AsSpan().SequenceEqual((saved.Payload with { Source = null, FullSourceVerified = false }).Serialize()))
            {
                continue;
            }

            await SaveArchiveAsync(row.SessionId, null, token).ConfigureAwait(false);
            changed = true;
        }

        return changed;
    }
}
