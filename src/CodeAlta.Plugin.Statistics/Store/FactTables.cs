using CodeAlta.Plugin.Statistics.Facts;

namespace CodeAlta.Plugin.Statistics.Store;

/// <summary>One row of facts, before it is written: the quarter, the key and the measures, in the order of its table.</summary>
/// <param name="Quarter">The quarter hour index.</param>
/// <param name="Keys">The key values after the quarter: text or integers, in the order of <see cref="FactTable.Keys"/>.</param>
/// <param name="Measures">The measures, in the order of <see cref="FactTable.Measures"/>.</param>
internal readonly record struct FactRow(int Quarter, object[] Keys, long[] Measures);

/// <summary>A column of a table of facts.</summary>
/// <param name="Name">The name of the column.</param>
/// <param name="IsText">Whether the column holds text; it holds an integer otherwise.</param>
/// <param name="IsMax">For a measure: whether two values merge with the larger instead of the sum.</param>
internal sealed record FactColumn(string Name, bool IsText = false, bool IsMax = false);

/// <summary>
/// A family of facts as the store keeps it: a table per session and quarter hour (<c>_q</c>) and the tables that add the quarters up per
/// local day, month and year (<c>_day</c>, <c>_month</c>, <c>_year</c>), all with the same keys and the same measures. Every measure is an
/// integer; a measure that merges with the larger value says so, the others add.
/// </summary>
internal sealed class FactTable
{
    private readonly Func<FactBatch, List<FactRow>> _extract;

    /// <summary>Initializes a family.</summary>
    /// <param name="name">The name of the family, without the prefix of the plugin.</param>
    /// <param name="keys">The key columns after the session and the quarter.</param>
    /// <param name="measures">The measures.</param>
    /// <param name="extract">Turns a batch into rows.</param>
    public FactTable(string name, FactColumn[] keys, FactColumn[] measures, Func<FactBatch, List<FactRow>> extract)
    {
        Name = name;
        Keys = keys;
        Measures = measures;
        _extract = extract;
    }

    /// <summary>Gets the name of the family.</summary>
    public string Name { get; }

    /// <summary>Gets the key columns.</summary>
    public FactColumn[] Keys { get; }

    /// <summary>Gets the measures.</summary>
    public FactColumn[] Measures { get; }

    /// <summary>Gets the rows a batch adds to this family, without the rows whose measures are all zero.</summary>
    /// <param name="batch">The batch.</param>
    /// <returns>The rows.</returns>
    public List<FactRow> Extract(FactBatch batch) => _extract(batch);

    /// <summary>Gets the name of the table at a level.</summary>
    /// <param name="prefix">The prefix of the plugin.</param>
    /// <param name="level">The level.</param>
    /// <returns>The table name.</returns>
    public string TableName(string prefix, RollupLevel level) => prefix + Name + level.Suffix();

    /// <summary>Gets the statement that adds a row to the table of a level.</summary>
    /// <param name="prefix">The prefix of the plugin.</param>
    /// <param name="level">The level.</param>
    /// <returns>The statement; its values are the session (for <see cref="RollupLevel.Quarter"/> only), the period, the keys and the measures.</returns>
    public string UpsertSql(string prefix, RollupLevel level)
    {
        var table = TableName(prefix, level);
        var keys = KeyColumns(level);
        var columns = keys.Concat(Measures.Select(static measure => measure.Name)).ToArray();
        var parameters = string.Join(", ", columns.Select(static (_, index) => "@p" + index));
        var sets = string.Join(", ", Measures.Select(static measure => measure.IsMax
            ? $"{measure.Name} = MAX({measure.Name}, excluded.{measure.Name})"
            : $"{measure.Name} = {measure.Name} + excluded.{measure.Name}"));
        return $"INSERT INTO {table} ({string.Join(", ", columns)}) VALUES ({parameters}) ON CONFLICT ({string.Join(", ", keys)}) DO UPDATE SET {sets}";
    }

    /// <summary>Gets the key columns of the table of a level: the session and the quarter, or the period, then the keys of the family.</summary>
    /// <param name="level">The level.</param>
    /// <returns>The column names.</returns>
    public string[] KeyColumns(RollupLevel level)
        => level == RollupLevel.Quarter
            ? ["session_id", "q", .. Keys.Select(static key => key.Name)]
            : ["p", .. Keys.Select(static key => key.Name)];

    /// <summary>Gets the statement that creates the table of a level.</summary>
    /// <param name="prefix">The prefix of the plugin.</param>
    /// <param name="level">The level.</param>
    /// <returns>The statements.</returns>
    public string CreateSql(string prefix, RollupLevel level)
    {
        var table = TableName(prefix, level);
        var columns = new List<string>();
        if (level == RollupLevel.Quarter)
        {
            columns.Add("session_id TEXT NOT NULL");
            columns.Add("q INTEGER NOT NULL");
        }
        else
        {
            columns.Add("p INTEGER NOT NULL");
        }

        columns.AddRange(Keys.Select(static key => $"{key.Name} {(key.IsText ? "TEXT" : "INTEGER")} NOT NULL"));
        columns.AddRange(Measures.Select(static measure => $"{measure.Name} INTEGER NOT NULL DEFAULT 0"));
        columns.Add($"PRIMARY KEY ({string.Join(", ", KeyColumns(level))})");
        var sql = $"CREATE TABLE {table} ({string.Join(", ", columns)}) WITHOUT ROWID;";
        if (level == RollupLevel.Quarter)
        {
            sql += $" CREATE INDEX {table}_by_q ON {table} (q);";
        }

        return sql;
    }

    /// <summary>
    /// Gets the statement that adds up the rows of a level into the level above (quarters into a local day, days into a month, months into
    /// a year), for the rows whose period or quarter is in a range. The statement takes the period it writes, then the range.
    /// </summary>
    /// <param name="prefix">The prefix of the plugin.</param>
    /// <param name="from">The level that is read.</param>
    /// <param name="to">The level that is written.</param>
    /// <returns>The statement; its values are the period written, the first value of the range and the end of the range, excluded.</returns>
    public string AddUpSql(string prefix, RollupLevel from, RollupLevel to)
    {
        var source = TableName(prefix, from);
        var target = TableName(prefix, to);
        var sourceColumn = from == RollupLevel.Quarter ? "q" : "p";
        var keys = Keys.Select(static key => key.Name).ToArray();
        var columns = new[] { "p" }.Concat(keys).Concat(Measures.Select(static measure => measure.Name)).ToArray();
        var selects = new[] { "@p0" }.Concat(keys).Concat(Measures.Select(static measure => measure.IsMax ? $"MAX({measure.Name})" : $"SUM({measure.Name})")).ToArray();
        var sets = string.Join(", ", Measures.Select(static measure => measure.IsMax
            ? $"{measure.Name} = MAX({measure.Name}, excluded.{measure.Name})"
            : $"{measure.Name} = {measure.Name} + excluded.{measure.Name}"));
        var group = keys.Length == 0 ? string.Empty : $" GROUP BY {string.Join(", ", keys)}";
        return $"INSERT INTO {target} ({string.Join(", ", columns)}) SELECT {string.Join(", ", selects)} FROM {source} WHERE {sourceColumn} >= @p1 AND {sourceColumn} < @p2{group} ON CONFLICT ({string.Join(", ", new[] { "p" }.Concat(keys))}) DO UPDATE SET {sets}";
    }
}

/// <summary>A level of the tables of facts.</summary>
internal enum RollupLevel
{
    /// <summary>Per session and quarter hour: the facts themselves.</summary>
    Quarter,

    /// <summary>Per local day.</summary>
    Day,

    /// <summary>Per local month.</summary>
    Month,

    /// <summary>Per local year.</summary>
    Year,
}

/// <summary>Helpers for <see cref="RollupLevel"/>.</summary>
internal static class RollupLevels
{
    /// <summary>Gets the suffix of a table name at a level.</summary>
    /// <param name="level">The level.</param>
    /// <returns><c>_q</c>, <c>_day</c>, <c>_month</c> or <c>_year</c>.</returns>
    public static string Suffix(this RollupLevel level) => level switch
    {
        RollupLevel.Quarter => "_q",
        RollupLevel.Day => "_day",
        RollupLevel.Month => "_month",
        RollupLevel.Year => "_year",
        _ => throw new ArgumentOutOfRangeException(nameof(level)),
    };
}

/// <summary>The families of facts and the way a batch becomes rows of each.</summary>
internal static class FactTables
{
    private const double MicroPerUnit = 1_000_000d;

    /// <summary>The active time and the runs.</summary>
    public static FactTable Activity { get; } = new(
        "activity",
        [Text("provider"), Text("model"), Text("effort")],
        [Sum("active_ms"), Sum("runs_started"), Sum("runs_completed"), Sum("runs_failed"), Sum("runs_interrupted"), Sum("errors"), Sum("compactions"), Sum("compaction_tokens_before"), Sum("compaction_tokens_after")],
        static batch => Rows(batch.Activity, static value => value.IsZero, static key => (key.Quarter, new object[] { key.Provider, key.Model, key.Effort }),
            static value => [value.ActiveMs, value.RunsStarted, value.RunsCompleted, value.RunsFailed, value.RunsInterrupted, value.Errors, value.Compactions, value.CompactionTokensBefore, value.CompactionTokensAfter]));

    /// <summary>The requests to models.</summary>
    public static FactTable Usage { get; } = new(
        "usage",
        [Text("provider"), Text("model"), Text("effort"), Text("agent_prompt"), Int("purpose")],
        [Sum("requests"), Sum("input_tokens"), Sum("fresh_input_tokens"), Sum("cache_read_tokens"), Sum("cache_write_tokens"), Sum("output_tokens"), Sum("reasoning_tokens"), Sum("provider_ms"),
            Sum("context_samples"), Sum("context_tokens_sum"), Sum("context_limit_sum"), Sum("context_fill_ppm_sum"), Max("context_fill_ppm_max")],
        static batch => Rows(batch.Usage, static value => value.IsZero, static key => (key.Quarter, new object[] { key.Provider, key.Model, key.Effort, key.AgentPrompt, (long)key.Purpose }),
            static value => [value.Requests, value.InputTokens, value.FreshInputTokens, value.CacheReadTokens, value.CacheWriteTokens, value.OutputTokens, value.ReasoningTokens, value.ProviderDurationMs,
                value.ContextSamples, value.ContextTokensSum, value.ContextLimitSum, value.ContextFillPpmSum, value.ContextFillPpmMax]));

    /// <summary>The costs, by unit; the total is in millionths of the unit.</summary>
    public static FactTable Cost { get; } = new(
        "cost",
        [Text("provider"), Text("model"), Text("unit")],
        [Sum("total_micro"), Sum("records")],
        static batch => Rows(batch.Cost, static value => value.IsZero, static key => (key.Quarter, new object[] { key.Provider, key.Model, key.Unit }),
            static value => [(long)Math.Round(value.Total * MicroPerUnit, MidpointRounding.AwayFromZero), value.Records]));

    /// <summary>The tool calls.</summary>
    public static FactTable Tools { get; } = new(
        "tool",
        [Text("provider"), Int("kind"), Text("tool")],
        [Sum("calls"), Sum("failures"), Sum("canceled"), Sum("duration_count"), Sum("duration_ms_total"), Max("duration_ms_max"), Sum("bytes_in"), Sum("bytes_out"), Sum("files_read"), Sum("files_changed"), Sum("lines_added"), Sum("lines_removed")],
        static batch => Rows(batch.Tools, static value => value.IsZero, static key => (key.Quarter, new object[] { key.Provider, (long)key.Kind, key.Tool }),
            static value => [value.Calls, value.Failures, value.Canceled, value.DurationCount, value.DurationMsTotal, value.DurationMsMax, value.BytesIn, value.BytesOut, value.FilesRead, value.FilesChanged, value.LinesAdded, value.LinesRemoved]));

    /// <summary>The prompts, answers, reasonings and instructions.</summary>
    public static FactTable Content { get; } = new(
        "content",
        [Int("kind"), Int("sender"), Int("prompt_kind")],
        [Sum("count"), Sum("chars"), Sum("words"), Sum("approx_tokens"), Sum("files"), Sum("directories"), Sum("images"), Sum("skills")],
        static batch => Rows(batch.Content, static value => value.IsZero, static key => (key.Quarter, new object[] { (long)key.Kind, (long)key.Sender, (long)key.PromptKind }),
            static value => [value.Count, value.Chars, value.Words, value.ApproxTokens, value.Files, value.Directories, value.Images, value.Skills]));

    /// <summary>The counted names.</summary>
    public static FactTable Details { get; } = new(
        "detail",
        [Int("list"), Text("name")],
        [Sum("n")],
        static batch => Rows(batch.Details, static value => value == 0, static key => (key.Quarter, new object[] { (long)key.List, key.Name }), static value => [value]));

    /// <summary>The distributions.</summary>
    public static FactTable Histograms { get; } = new(
        "histogram",
        [Int("measure"), Text("subject"), Int("step")],
        [Sum("n")],
        static batch => Rows(batch.Histograms, static value => value == 0, static key => (key.Quarter, new object[] { (long)key.Measure, key.Subject, (long)key.Step }), static value => [value]));

    /// <summary>The families that add up: every family but the extremes.</summary>
    public static IReadOnlyList<FactTable> All { get; } = [Activity, Usage, Cost, Tools, Content, Details, Histograms];

    private static FactColumn Text(string name) => new(name, IsText: true);

    private static FactColumn Int(string name) => new(name);

    private static FactColumn Sum(string name) => new(name);

    private static FactColumn Max(string name) => new(name, IsMax: true);

    private static List<FactRow> Rows<TKey, TValue>(
        Dictionary<TKey, TValue> source,
        Func<TValue, bool> isZero,
        Func<TKey, (QuarterHour Quarter, object[] Keys)> key,
        Func<TValue, long[]> measures)
        where TKey : notnull
    {
        var rows = new List<FactRow>(source.Count);
        foreach (var (k, value) in source)
        {
            if (isZero(value))
            {
                continue;
            }

            var (quarter, keys) = key(k);
            rows.Add(new FactRow(quarter.Index, keys, measures(value)));
        }

        return rows;
    }
}
