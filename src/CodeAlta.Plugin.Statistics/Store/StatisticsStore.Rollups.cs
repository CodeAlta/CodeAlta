using System.Globalization;
using System.Text;
using CodeAlta.Plugin.Statistics.Facts;

namespace CodeAlta.Plugin.Statistics.Store;

internal sealed partial class StatisticsStore
{
    /// <summary>The number of days whose roll-ups one transaction adds up again during a rebuild.</summary>
    private const int RebuildDaysPerTransaction = 30;

    /// <summary>One largest value, as the extreme tables hold it.</summary>
    private readonly record struct ExtremeRow(int Quarter, ExtremeMeasure Measure, string Subject, long Value, string SessionId, string? RunId, long AtMs);

    /// <summary>
    /// Adds the roll-ups up again from the facts alone, for every day: used when the version of the roll-ups or the time zone
    /// changed. The old roll-ups stay readable while it works, one block of days at a time.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the operation; the roll-ups are then rebuilt at the next start.</param>
    /// <returns>A task representing the rebuild.</returns>
    public async ValueTask RebuildRollupsAsync(CancellationToken cancellationToken = default)
    {
        // The quarters that hold facts, from every family: their days are the days that have roll-ups.
        var quarters = await ReadAsync(
            sql =>
            {
                var set = new HashSet<int>();
                foreach (var table in FactTables.All)
                {
                    set.UnionWith(sql.Query($"SELECT DISTINCT q FROM {table.TableName(Prefix, RollupLevel.Quarter)}", static reader => (int)reader.GetInt64(0)));
                }

                set.UnionWith(sql.Query($"SELECT DISTINCT q FROM {Table("extreme_q")}", static reader => (int)reader.GetInt64(0)));
                return set;
            },
            cancellationToken).ConfigureAwait(false);

        var days = quarters.Select(Days.DayOf).Distinct().Order().ToArray();
        var existing = await ReadAsync(
            sql =>
            {
                var set = new HashSet<int>();
                foreach (var table in FactTables.All)
                {
                    set.UnionWith(sql.Query($"SELECT DISTINCT p FROM {table.TableName(Prefix, RollupLevel.Day)}", static reader => (int)reader.GetInt64(0)));
                }

                set.UnionWith(sql.Query($"SELECT DISTINCT p FROM {Table("extreme_day")}", static reader => (int)reader.GetInt64(0)));
                return set;
            },
            cancellationToken).ConfigureAwait(false);

        for (var index = 0; index < days.Length; index += RebuildDaysPerTransaction)
        {
            var block = days.Skip(index).Take(RebuildDaysPerTransaction).ToArray();
            await WriteAsync(sql => AddUpDays(sql, block), cancellationToken).ConfigureAwait(false);
        }

        var stale = existing.Except(days).ToArray();
        var allMonths = days.Select(LocalDays.MonthOf).Distinct().ToArray();
        var allYears = days.Select(LocalDays.YearOf).Distinct().ToArray();
        await WriteAsync(
            sql =>
            {
                foreach (var day in stale)
                {
                    DeleteDay(sql, day);
                }

                // Months and years that no longer hold a day, and the ones that do, are added up from the days.
                DeletePeriods(sql, RollupLevel.Month, allMonths);
                DeletePeriods(sql, RollupLevel.Year, allYears);
                AddUpMonths(sql, allMonths);
                AddUpYears(sql, allYears);
                SetMeta(sql, new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    [RollupVersionKey] = RollupVersion.ToString(CultureInfo.InvariantCulture),
                    [RollupTimeZoneKey] = Days.TimeZone.Id,
                });
            },
            cancellationToken).ConfigureAwait(false);
    }

    private IReadOnlyList<int> RecomputeRollups(SqlSession sql, IEnumerable<int> quarters)
    {
        var days = quarters.Select(Days.DayOf).Distinct().Order().ToArray();
        AddUpDays(sql, days);
        var months = days.Select(LocalDays.MonthOf).Distinct().ToArray();
        foreach (var month in months)
        {
            DeletePeriod(sql, RollupLevel.Month, month);
        }

        AddUpMonths(sql, months);
        var years = days.Select(LocalDays.YearOf).Distinct().ToArray();
        foreach (var year in years)
        {
            DeletePeriod(sql, RollupLevel.Year, year);
        }

        AddUpYears(sql, years);
        return days;
    }

    /// <summary>Adds the quarters of each day up into the day tables, replacing what the days held; then the months and years of those days are the caller's.</summary>
    private void AddUpDays(SqlSession sql, IEnumerable<int> days)
    {
        foreach (var day in days)
        {
            DeleteDay(sql, day);
            foreach (var (from, to) in Days.QuarterIntervalsOf(day))
            {
                foreach (var table in FactTables.All)
                {
                    sql.Execute(table.AddUpSql(Prefix, RollupLevel.Quarter, RollupLevel.Day), (long)day, (long)from, (long)to);
                }

                sql.Execute(ExtremeAddUpSql(RollupLevel.Quarter, RollupLevel.Day), (long)day, (long)from, (long)to);
            }
        }
    }

    private void AddUpMonths(SqlSession sql, IEnumerable<int> months)
    {
        foreach (var month in months)
        {
            foreach (var table in FactTables.All)
            {
                sql.Execute(table.AddUpSql(Prefix, RollupLevel.Day, RollupLevel.Month), (long)month, (long)(month * 100), (long)((month * 100) + 100));
            }

            sql.Execute(ExtremeAddUpSql(RollupLevel.Day, RollupLevel.Month), (long)month, (long)(month * 100), (long)((month * 100) + 100));
        }
    }

    private void AddUpYears(SqlSession sql, IEnumerable<int> years)
    {
        foreach (var year in years)
        {
            foreach (var table in FactTables.All)
            {
                sql.Execute(table.AddUpSql(Prefix, RollupLevel.Month, RollupLevel.Year), (long)year, (long)(year * 100), (long)((year * 100) + 100));
            }

            sql.Execute(ExtremeAddUpSql(RollupLevel.Month, RollupLevel.Year), (long)year, (long)(year * 100), (long)((year * 100) + 100));
        }
    }

    private void DeleteDay(SqlSession sql, int day) => DeletePeriod(sql, RollupLevel.Day, day);

    private void DeletePeriod(SqlSession sql, RollupLevel level, int period)
    {
        foreach (var table in FactTables.All)
        {
            sql.Execute($"DELETE FROM {table.TableName(Prefix, level)} WHERE p = @p0", (long)period);
        }

        sql.Execute($"DELETE FROM {Table("extreme" + level.Suffix())} WHERE p = @p0", (long)period);
    }

    private void DeletePeriods(SqlSession sql, RollupLevel level, IReadOnlyCollection<int> keep)
    {
        var keepSet = keep.ToHashSet();
        foreach (var table in FactTables.All)
        {
            foreach (var period in sql.Query($"SELECT DISTINCT p FROM {table.TableName(Prefix, level)}", static reader => (int)reader.GetInt64(0)).Where(period => !keepSet.Contains(period)))
            {
                sql.Execute($"DELETE FROM {table.TableName(Prefix, level)} WHERE p = @p0", (long)period);
            }
        }

        foreach (var period in sql.Query($"SELECT DISTINCT p FROM {Table("extreme" + level.Suffix())}", static reader => (int)reader.GetInt64(0)).Where(period => !keepSet.Contains(period)))
        {
            sql.Execute($"DELETE FROM {Table("extreme" + level.Suffix())} WHERE p = @p0", (long)period);
        }

        // The periods that are kept are added up again by the caller: clear them first so that nothing is counted twice.
        foreach (var period in keepSet)
        {
            DeletePeriod(sql, level, period);
        }
    }

    private void AddToRollups(SqlSession sql, FactTable table, List<FactRow> rows)
    {
        if (rows.Count == 0)
        {
            return;
        }

        foreach (var level in new[] { RollupLevel.Day, RollupLevel.Month, RollupLevel.Year })
        {
            var aggregate = new Dictionary<string, (int Period, object[] Keys, long[] Measures)>(StringComparer.Ordinal);
            var builder = new StringBuilder();
            foreach (var row in rows)
            {
                var period = level switch
                {
                    RollupLevel.Day => Days.DayOf(row.Quarter),
                    RollupLevel.Month => LocalDays.MonthOf(Days.DayOf(row.Quarter)),
                    _ => LocalDays.YearOf(Days.DayOf(row.Quarter)),
                };

                builder.Clear().Append(period);
                foreach (var key in row.Keys)
                {
                    builder.Append('\u001f').Append(Convert.ToString(key, CultureInfo.InvariantCulture));
                }

                var id = builder.ToString();
                if (aggregate.TryGetValue(id, out var current))
                {
                    for (var index = 0; index < table.Measures.Length; index++)
                    {
                        current.Measures[index] = table.Measures[index].IsMax
                            ? Math.Max(current.Measures[index], row.Measures[index])
                            : current.Measures[index] + row.Measures[index];
                    }
                }
                else
                {
                    aggregate[id] = (period, row.Keys, [.. row.Measures]);
                }
            }

            var upsert = table.UpsertSql(Prefix, level);
            foreach (var (period, keys, measures) in aggregate.Values)
            {
                sql.Execute(upsert, [(long)period, .. keys, .. Boxed(measures)]);
            }
        }
    }

    private void AddExtremesToRollups(SqlSession sql, List<ExtremeRow> rows)
    {
        foreach (var level in new[] { RollupLevel.Day, RollupLevel.Month, RollupLevel.Year })
        {
            var upsert = ExtremeUpsertSql(level);
            foreach (var row in rows)
            {
                var day = Days.DayOf(row.Quarter);
                var period = level switch
                {
                    RollupLevel.Day => day,
                    RollupLevel.Month => LocalDays.MonthOf(day),
                    _ => LocalDays.YearOf(day),
                };

                sql.Execute(upsert, (long)period, (long)row.Measure, row.Subject, row.Value, row.SessionId, row.RunId, row.AtMs);
            }
        }
    }

    private List<ExtremeRow> ExtremeRows(FactBatch batch, int floor)
    {
        var rows = new List<ExtremeRow>(batch.Extremes.Count);
        foreach (var (key, value) in batch.Extremes)
        {
            if (key.Quarter.Index >= floor)
            {
                rows.Add(new ExtremeRow(key.Quarter.Index, key.Measure, key.Subject, value.Value, value.SessionId, value.RunId, value.At.ToUnixTimeMilliseconds()));
            }
        }

        return rows;
    }

    private string ExtremeUpsertSql(RollupLevel level)
    {
        const string Better = "excluded.value > value OR (excluded.value = value AND excluded.at_ms < at_ms)";
        var table = Table("extreme" + level.Suffix());
        return level == RollupLevel.Quarter
            ? $"INSERT INTO {table} (session_id, q, measure, subject, value, run_id, at_ms) VALUES (@p0, @p1, @p2, @p3, @p4, @p5, @p6) ON CONFLICT (session_id, q, measure, subject) DO UPDATE SET value = excluded.value, run_id = excluded.run_id, at_ms = excluded.at_ms WHERE {Better}"
            // A roll-up names the session the largest value comes from.
            : $"INSERT INTO {table} (p, measure, subject, value, session_id, run_id, at_ms) VALUES (@p0, @p1, @p2, @p3, @p4, @p5, @p6) ON CONFLICT (p, measure, subject) DO UPDATE SET value = excluded.value, session_id = excluded.session_id, run_id = excluded.run_id, at_ms = excluded.at_ms WHERE {Better}";
    }

    private string ExtremeAddUpSql(RollupLevel from, RollupLevel to)
    {
        const string Better = "excluded.value > value OR (excluded.value = value AND excluded.at_ms < at_ms)";
        var source = Table("extreme" + from.Suffix());
        var target = Table("extreme" + to.Suffix());
        var column = from == RollupLevel.Quarter ? "q" : "p";
        return $"INSERT INTO {target} (p, measure, subject, value, session_id, run_id, at_ms) "
            + $"SELECT @p0, measure, subject, value, session_id, run_id, at_ms FROM (SELECT measure, subject, value, session_id, run_id, at_ms, ROW_NUMBER() OVER (PARTITION BY measure, subject ORDER BY value DESC, at_ms ASC) AS rn FROM {source} WHERE {column} >= @p1 AND {column} < @p2) WHERE rn = 1 "
            + $"ON CONFLICT (p, measure, subject) DO UPDATE SET value = excluded.value, session_id = excluded.session_id, run_id = excluded.run_id, at_ms = excluded.at_ms WHERE {Better}";
    }

    private string SchemaV1()
    {
        var builder = new StringBuilder();
        builder.Append($"CREATE TABLE {Table("meta")} (key TEXT NOT NULL PRIMARY KEY, value TEXT NOT NULL) WITHOUT ROWID;");
        builder.Append($"CREATE TABLE {Table("journal")} (session_id TEXT NOT NULL PRIMARY KEY, read_offset INTEGER NOT NULL, file_length INTEGER NOT NULL, file_stamp INTEGER NOT NULL, first_line_length INTEGER, first_line_hash INTEGER, state BLOB NOT NULL, facts_version INTEGER NOT NULL, floor_q INTEGER NOT NULL, updated_ms INTEGER NOT NULL, open_runs INTEGER NOT NULL DEFAULT 0, deleted INTEGER NOT NULL DEFAULT 0) WITHOUT ROWID;");
        builder.Append($"CREATE TABLE {Table("session")} (session_id TEXT NOT NULL PRIMARY KEY, project_ref TEXT, session_kind TEXT, parent_session_id TEXT, created_by_kind TEXT, created_by_session_id TEXT, automation_id TEXT, title TEXT, provider TEXT, permission_mode TEXT, first_ms INTEGER, last_ms INTEGER, deleted INTEGER NOT NULL DEFAULT 0) WITHOUT ROWID;");
        builder.Append($"CREATE INDEX {Table("session")}_by_last ON {Table("session")} (last_ms);");
        builder.Append($"CREATE INDEX {Table("session")}_by_project ON {Table("session")} (project_ref);");
        builder.Append($"CREATE TABLE {Table("project")} (project_ref TEXT NOT NULL PRIMARY KEY, name TEXT NOT NULL, updated_ms INTEGER NOT NULL) WITHOUT ROWID;");
        builder.Append($"CREATE TABLE {Table("run")} (session_id TEXT NOT NULL, run_id TEXT NOT NULL, start_ms INTEGER NOT NULL, end_ms INTEGER NOT NULL, outcome INTEGER NOT NULL, sender INTEGER NOT NULL, prompt_kind INTEGER NOT NULL, prompt_chars INTEGER NOT NULL, prompt_words INTEGER NOT NULL, requests INTEGER NOT NULL, tool_calls INTEGER NOT NULL, tool_failures INTEGER NOT NULL, input_tokens INTEGER NOT NULL, output_tokens INTEGER NOT NULL, compactions INTEGER NOT NULL, answer_chars INTEGER NOT NULL, answer_words INTEGER NOT NULL, cost_usd_micro INTEGER NOT NULL, cost_credits_micro INTEGER NOT NULL, provider TEXT NOT NULL, model TEXT NOT NULL, effort TEXT NOT NULL, permission_mode TEXT NOT NULL, PRIMARY KEY (session_id, run_id)) WITHOUT ROWID;");
        builder.Append($"CREATE INDEX {Table("run")}_by_start ON {Table("run")} (start_ms);");
        foreach (var table in FactTables.All)
        {
            foreach (var level in new[] { RollupLevel.Quarter, RollupLevel.Day, RollupLevel.Month, RollupLevel.Year })
            {
                builder.Append(table.CreateSql(Prefix, level));
            }
        }

        builder.Append($"CREATE TABLE {Table("extreme_q")} (session_id TEXT NOT NULL, q INTEGER NOT NULL, measure INTEGER NOT NULL, subject TEXT NOT NULL, value INTEGER NOT NULL, run_id TEXT, at_ms INTEGER NOT NULL, PRIMARY KEY (session_id, q, measure, subject)) WITHOUT ROWID;");
        builder.Append($"CREATE INDEX {Table("extreme_q")}_by_q ON {Table("extreme_q")} (q);");
        foreach (var level in new[] { RollupLevel.Day, RollupLevel.Month, RollupLevel.Year })
        {
            builder.Append($"CREATE TABLE {Table("extreme" + level.Suffix())} (p INTEGER NOT NULL, measure INTEGER NOT NULL, subject TEXT NOT NULL, value INTEGER NOT NULL, session_id TEXT NOT NULL, run_id TEXT, at_ms INTEGER NOT NULL, PRIMARY KEY (p, measure, subject)) WITHOUT ROWID;");
        }

        return builder.ToString();
    }
}
