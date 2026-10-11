using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Plugin.Statistics.Facts;

namespace CodeAlta.Plugin.Statistics.Store;

internal sealed partial class StatisticsStore
{
    private readonly StatisticsArchiveStore? _archives;

    internal bool HasArchive => _archives is not null;
    internal int SavedSessions { get; private set; }

    private static IReadOnlyList<(string Name, string[] Columns)> ArchiveTables()
        => [.. FactTables.All.Select(table => (table.Name + "_q", table.KeyColumns(RollupLevel.Quarter).Concat(table.Measures.Select(measure => measure.Name)).ToArray())),
            ("extreme_q", ["session_id", "q", "measure", "subject", "value", "run_id", "at_ms"]),
            ("run", ["session_id", "run_id", "start_ms", "end_ms", "outcome", "sender", "prompt_kind", "prompt_chars", "prompt_words", "requests", "tool_calls", "tool_failures", "input_tokens", "output_tokens", "compactions", "answer_chars", "answer_words", "cost_usd_micro", "cost_credits_micro", "provider", "model", "effort", "permission_mode", "skipped_ms"]),
            ("session", ["session_id", "project_ref", "session_kind", "parent_session_id", "created_by_kind", "created_by_session_id", "automation_id", "title", "provider", "permission_mode", "first_ms", "last_ms", "deleted"]),
            ("project", ["project_ref", "name", "updated_ms"])];

    internal static string[] CurrentMetrics()
        => [.. FactTables.All.SelectMany(table => table.Measures.Select(measure => table.Name + "." + measure.Name)), "extreme", "run", "run.skipped_ms", "session.lineage"];

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
        if (payload.ArchiveVersion != 1 || payload.FactsVersion < 1 || payload.FactsVersion > SessionFactsState.CurrentVersion || payload.SessionId != sessionId || payload.Source != source
            // A partial legacy cursor stores length=0 until EOF. Preserve that unknown-length sentinel, not an invented total.
            || payload.ReadOffset < 0 || payload.SourceLength < 0 || (payload.SourceLength != 0 && payload.SourceLength < payload.ReadOffset) || payload.FullSourceVerified != (source is not null)
            || (source is not null && (source.SessionId != sessionId || payload.ReadOffset != source.Length || payload.SourceLength != source.Length || payload.FloorQuarter != int.MinValue)))
        {
            throw new InvalidDataException("Unsupported or inconsistent saved Statistics coverage; the existing projection was not reset.");
        }

        var definitions = ArchiveTables();
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
        if (present is not null && _archives is not null && sessionId is null)
        {
            // A returning journal can supersede an archive, then disappear again. Preserve those newer facts first.
            await PreserveDeletedAsync(token, existingOnly: true).ConfigureAwait(false);
        }

        var archives = await ReadArchivesAsync(token, sessionId).ConfigureAwait(false);
        await WriteAsync(sql =>
        {
            foreach (var (entry, payload) in archives)
            {
                var journalExists = sql.ScalarLong($"SELECT COUNT(*) FROM {Table("journal")} WHERE session_id=@p0", entry.SessionId) != 0;
                var imported = sql.ScalarLong($"SELECT COUNT(*) FROM {Table("archive")} WHERE session_id=@p0 AND effective=1", entry.SessionId) != 0;
                var currentGeneration = sql.ScalarLong($"SELECT COUNT(*) FROM {Table("archive")} WHERE session_id=@p0 AND generation=@p1", entry.SessionId, entry.Generation) != 0;
                if (!journalExists || imported || !currentGeneration || (present is not null && !present.Contains(entry.SessionId)))
                {
                    ImportArchive(sql, entry, payload, unchanged: journalExists && imported && currentGeneration);
                }
            }
        }, token).ConfigureAwait(false);
    }

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

    private async ValueTask PreserveDeletedAsync(CancellationToken token, bool existingOnly = false)
    {
        var rows = await ListJournalsAsync(token).ConfigureAwait(false);
        var archives = (await ReadArchivesAsync(token).ConfigureAwait(false)).ToDictionary(pair => pair.Entry.SessionId, StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows.Values.Where(row => row.Deleted))
        {
            if (existingOnly && !archives.ContainsKey(row.SessionId))
            {
                continue;
            }

            if (await IsArchivedProjectionAsync(row.SessionId, token).ConfigureAwait(false))
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
        }
    }
}
