using System.Collections.Frozen;
using System.Globalization;
using CodeAlta.Plugin.Statistics.Facts;
using CodeAlta.Plugin.Statistics.Store;

namespace CodeAlta.Plugin.Statistics.Query;

public sealed partial class StatisticsQueries
{
    private sealed record MetricDef(string Name, FactTable Family, string Aggregate, string Unit, string? Where = null, double Scale = 1d, bool PerUnit = false);

    private static readonly FrozenDictionary<string, MetricDef> Metrics = new MetricDef[]
    {
        new("runs", FactTables.Activity, "SUM(f.runs_started)", "count"),
        new("runs-completed", FactTables.Activity, "SUM(f.runs_completed)", "count"),
        new("runs-failed", FactTables.Activity, "SUM(f.runs_failed)", "count"),
        new("runs-interrupted", FactTables.Activity, "SUM(f.runs_interrupted)", "count"),
        new("active-time", FactTables.Activity, "SUM(f.active_ms)", "ms"),
        new("errors", FactTables.Activity, "SUM(f.errors)", "count"),
        new("compactions", FactTables.Activity, "SUM(f.compactions)", "count"),
        new("requests", FactTables.Usage, "SUM(f.requests)", "count"),
        new("tokens", FactTables.Usage, "SUM(f.input_tokens + f.output_tokens)", "tokens"),
        new("input-tokens", FactTables.Usage, "SUM(f.input_tokens)", "tokens"),
        new("fresh-input-tokens", FactTables.Usage, "SUM(f.fresh_input_tokens)", "tokens"),
        new("cache-read-tokens", FactTables.Usage, "SUM(f.cache_read_tokens)", "tokens"),
        new("cache-write-tokens", FactTables.Usage, "SUM(f.cache_write_tokens)", "tokens"),
        new("output-tokens", FactTables.Usage, "SUM(f.output_tokens)", "tokens"),
        new("reasoning-tokens", FactTables.Usage, "SUM(f.reasoning_tokens)", "tokens"),

        // The requests that reported a context window: what the average of `context-fill` is an average of.
        new("context-samples", FactTables.Usage, "SUM(f.context_samples)", "count"),
        new("tool-calls", FactTables.Tools, "SUM(f.calls)", "count"),
        new("tool-failures", FactTables.Tools, "SUM(f.failures)", "count"),
        new("tool-calls-canceled", FactTables.Tools, "SUM(f.canceled)", "count"),
        new("tool-time", FactTables.Tools, "SUM(f.duration_ms_total)", "ms"),
        new("tool-bytes-in", FactTables.Tools, "SUM(f.bytes_in)", "bytes"),
        new("tool-bytes-out", FactTables.Tools, "SUM(f.bytes_out)", "bytes"),
        new("files-read", FactTables.Tools, "SUM(f.files_read)", "count"),
        new("files-changed", FactTables.Tools, "SUM(f.files_changed)", "count"),
        new("lines-added", FactTables.Tools, "SUM(f.lines_added)", "lines"),
        new("lines-removed", FactTables.Tools, "SUM(f.lines_removed)", "lines"),
        new("prompts", FactTables.Content, "SUM(f.count)", "count", Where: "f.kind = 0"),
        new("your-prompts", FactTables.Content, "SUM(f.count)", "count", Where: "f.kind = 0 AND f.sender = 1"),
        new("prompt-chars", FactTables.Content, "SUM(f.chars)", "count", Where: "f.kind = 0"),
        new("prompt-words", FactTables.Content, "SUM(f.words)", "count", Where: "f.kind = 0"),
        new("prompt-files", FactTables.Content, "SUM(f.files)", "count", Where: "f.kind = 0"),
        new("prompt-directories", FactTables.Content, "SUM(f.directories)", "count", Where: "f.kind = 0"),
        new("prompt-images", FactTables.Content, "SUM(f.images)", "count", Where: "f.kind = 0"),
        new("prompt-skills", FactTables.Content, "SUM(f.skills)", "count", Where: "f.kind = 0"),
        new("answers", FactTables.Content, "SUM(f.count)", "count", Where: "f.kind = 1"),
        new("answer-chars", FactTables.Content, "SUM(f.chars)", "count", Where: "f.kind = 1"),
        new("answer-words", FactTables.Content, "SUM(f.words)", "count", Where: "f.kind = 1"),

        // The reasonings a model showed, and the summaries of the ones it did not: what was written, not the tokens it cost.
        new("reasonings", FactTables.Content, "SUM(f.count)", "count", Where: "f.kind IN (2, 3)"),
        new("reasoning-chars", FactTables.Content, "SUM(f.chars)", "count", Where: "f.kind IN (2, 3)"),
        new("instructions", FactTables.Content, "SUM(f.count)", "count", Where: "f.kind = 4"),
        new("instruction-chars", FactTables.Content, "SUM(f.chars)", "count", Where: "f.kind = 4"),
        new("instruction-tokens", FactTables.Content, "SUM(f.approx_tokens)", "tokens", Where: "f.kind = 4"),
        new("cost", FactTables.Cost, "SUM(f.total_micro)", "cost", Scale: 1e-6, PerUnit: true),
    }.ToFrozenDictionary(static metric => metric.Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the names of the metrics <see cref="SeriesAsync"/> knows: the sums of the facts, and those that are computed from the
    /// sessions and the runs: <c>sessions-active</c>, <c>sessions-started</c>, <c>sessions-at-once</c> (the most sessions with a run
    /// going at the same moment) and <c>context-fill</c> (the average fill of the context window).
    /// </summary>
    public static IReadOnlyList<string> MetricNames { get; } = [.. Metrics.Keys.Order(StringComparer.Ordinal), ContextFillMetric, "sessions-active", SessionsAtOnceMetric, "sessions-started"];

    /// <summary>Gets the ways a series can be grouped.</summary>
    public static IReadOnlyList<string> GroupNames { get; } = ["provider", "model", "effort", "project", "delegated", "tool", "kind", "origin", "purpose", "unit", "prompt-kind"];

    /// <summary>Reads one metric over time, in buckets, optionally cut by a group.</summary>
    /// <param name="request">The period, the frequency, the filters and the comparison.</param>
    /// <param name="metric">The metric: one of <see cref="MetricNames"/>.</param>
    /// <param name="group">The group of the lines: one of <see cref="GroupNames"/> the metric supports; null for one line. A cost is always cut by its unit.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>
    /// The lines, the largest first, at most <see cref="StatisticsRequest.Limit"/> with the rest added up as <c>other</c>. The total
    /// of a line is the sum of its values, except for <c>sessions-at-once</c> (the most of the period) and <c>context-fill</c> (the
    /// average of the period).
    /// </returns>
    /// <exception cref="ArgumentException">The metric, the group, the period or the frequency is not valid.</exception>
    public async ValueTask<SeriesResult> SeriesAsync(StatisticsRequest request, string metric, string? group = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(metric);
        var query = await ResolveAsync(request, cancellationToken).ConfigureAwait(false);
        return await _store.ReadAsync(sql => BuildSeries(sql, query, metric.Trim(), group?.Trim()), cancellationToken).ConfigureAwait(false);
    }

    private SeriesResult BuildSeries(SqlSession sql, ResolvedQuery query, string metric, string? group)
    {
        var limit = LimitOf(query.Request, 20);
        if (string.Equals(metric, SessionsAtOnceMetric, StringComparison.OrdinalIgnoreCase))
        {
            return BuildSessionsAtOnce(sql, query, group);
        }

        if (string.Equals(metric, ContextFillMetric, StringComparison.OrdinalIgnoreCase))
        {
            return BuildContextFill(sql, query, group, limit);
        }

        if (metric is "sessions-active" or "sessions-started")
        {
            if (group is not null && group is not ("project" or "delegated"))
            {
                throw new ArgumentException($"'{metric}' can only be grouped by project or delegated.");
            }

            var current = SessionCounts(sql, query, query.Plan, metric, group);
            var previous = query.ComparePlan is { } comparePlan ? SessionCounts(sql, query, comparePlan, metric, group) : null;
            return new SeriesResult(Header(query), metric, "count", group, BucketInfos(query.Plan), Lines(query, query.Plan, current, previous, group, 1d, limit));
        }

        // The sender of the prompt that started a run is in the table of runs, not in the quarter hours: runs by origin read it.
        if (metric == "runs" && (group == "origin" || query.Filter.Origin is { Length: > 0 }))
        {
            if (group is not null && group != "origin")
            {
                throw new ArgumentException("Runs of an origin can only be cut by origin.");
            }

            var current = RunCounts(sql, query, query.Plan, byOrigin: group is not null);
            var previous = query.ComparePlan is { } comparePlan ? RunCounts(sql, query, comparePlan, byOrigin: group is not null) : null;
            return new SeriesResult(Header(query), metric, "count", group, BucketInfos(query.Plan), Lines(query, query.Plan, current, previous, group, 1d, limit));
        }

        if (!Metrics.TryGetValue(metric, out var definition))
        {
            throw new ArgumentException($"'{metric}' is not a metric: use one of {string.Join(", ", MetricNames)}.");
        }

        var column = definition.PerUnit ? "unit" : group is null ? null : GroupColumn(definition.Family, group);
        if (definition.PerUnit && group is not null && group != "unit")
        {
            throw new ArgumentException("A cost is cut by its unit: costs of different units never add up.");
        }

        var groupColumns = column is null ? [] : new[] { column };
        var rows = Aggregate(sql, query, query.Plan, definition.Family, [definition.Aggregate], groupColumns, definition.Where);
        var compared = query.ComparePlan is { } plan ? Aggregate(sql, query, plan, definition.Family, [definition.Aggregate], groupColumns, definition.Where) : null;
        var label = column ?? group;
        return new SeriesResult(Header(query), metric, definition.PerUnit ? "cost" : definition.Unit, label, BucketInfos(query.Plan), Lines(query, query.Plan, rows, compared, column, definition.Scale, limit));
    }

    private static string GroupColumn(FactTable family, string group)
    {
        var name = group.ToLowerInvariant();
        if (name is "project" or "delegated")
        {
            return name;
        }

        if (name == "origin")
        {
            return family.Name == "content" ? "origin" : throw new ArgumentException("Only prompts can be cut by origin.");
        }

        if (name == "prompt-kind")
        {
            return family.Name == "content" ? "prompt-kind" : throw new ArgumentException("Only prompts can be cut by prompt kind.");
        }

        if (name == "kind" && family.Name == "content")
        {
            return "content-kind";
        }

        if (name == "tool" && family.Name != "tool")
        {
            throw new ArgumentException("Only tool metrics can be cut by tool.");
        }

        if (name == "purpose" && family.Name != "usage")
        {
            throw new ArgumentException("Only metrics of requests can be cut by purpose.");
        }

        if (!GroupNames.Contains(name, StringComparer.Ordinal))
        {
            throw new ArgumentException($"'{group}' is not a group: use one of {string.Join(", ", GroupNames)}.");
        }

        if (!family.Keys.Any(key => key.Name == name))
        {
            throw new ArgumentException($"'{group}' is not a way to cut {family.Name} metrics.");
        }

        return name;
    }

    private List<AggRow> SessionCounts(SqlSession sql, ResolvedQuery query, BucketPlan plan, string metric, string? group)
    {
        var byProject = group == "project";
        var byDelegation = group == "delegated";
        // Sessions are counted in the facts: a session is active in a bucket when it had a run or some active time there.
        var rows = new Dictionary<string, AggRow>(StringComparer.Ordinal);
        if (metric == "sessions-active")
        {
            var args = new List<object?>();
            string Arg(object? value)
            {
                args.Add(value);
                return "@p" + (args.Count - 1).ToString(CultureInfo.InvariantCulture);
            }

            var builder = new System.Text.StringBuilder($"SELECT f.q, f.session_id, {(byDelegation ? "CASE WHEN s.parent_session_id IS NULL THEN 'direct' ELSE 'sub-agent' END" : "COALESCE(s.project_ref, '')")} FROM {P}activity_q f LEFT JOIN {P}session s ON s.session_id = f.session_id WHERE f.q >= ");
            var (from, to) = Days.QuarterRangeOfDays(LocalDays.ToDay(plan.Range.From), LocalDays.ToDay(plan.Range.To));
            builder.Append(Arg((long)from)).Append(" AND f.q < ").Append(Arg((long)to)).Append(" AND (f.active_ms > 0 OR f.runs_started > 0)");
            AppendFilters(builder, query, FactTables.Activity, Arg, applyOrigin: false);
            if (query.SessionIds is { } scope)
            {
                builder.Append(scope.Count == 0 ? " AND 0" : " AND f.session_id IN (" + string.Join(", ", scope.Select(id => Arg(id))) + ")");
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (quarter, session, project) in sql.Query(builder.ToString(), static reader => ((int)reader.GetInt64(0), reader.GetString(1), reader.GetString(2)), [.. args]))
            {
                var bucket = plan.OfQuarter(quarter);
                if (bucket < 0 || !seen.Add(bucket.ToString(CultureInfo.InvariantCulture) + "|" + session))
                {
                    continue;
                }

                Count(rows, bucket, byProject || byDelegation ? project : string.Empty);
            }

            return [.. rows.Values];
        }

        var startArgs = new List<object?>();
        string StartArg(object? value)
        {
            startArgs.Add(value);
            return "@p" + (startArgs.Count - 1).ToString(CultureInfo.InvariantCulture);
        }

        var (lower, upper) = Days.QuarterRangeOfDays(LocalDays.ToDay(plan.Range.From), LocalDays.ToDay(plan.Range.To));
        var startBuilder = new System.Text.StringBuilder($"SELECT s.first_ms, {(byDelegation ? "CASE WHEN s.parent_session_id IS NULL THEN 'direct' ELSE 'sub-agent' END" : "COALESCE(s.project_ref, '')")} FROM {P}session s WHERE s.first_ms >= ")
            .Append(StartArg(lower * QuarterHour.Milliseconds)).Append(" AND s.first_ms < ").Append(StartArg(upper * QuarterHour.Milliseconds));
        var filter = query.Filter;
        if (filter.Provider is { Length: > 0 } provider)
        {
            startBuilder.Append(" AND s.provider = ").Append(StartArg(provider)).Append(" COLLATE NOCASE");
        }

        foreach (var ignored in new[] { ("model", filter.Model), ("effort", filter.Effort), ("toolKind", filter.ToolKind), ("origin", filter.Origin) })
        {
            if (ignored.Item2 is { Length: > 0 })
            {
                query.Ignored.Add(ignored.Item1);
            }
        }

        startBuilder.Append(query.ProjectClause("s.project_ref", reference => StartArg(reference)));

        if (query.SessionIds is { } started)
        {
            startBuilder.Append(started.Count == 0 ? " AND 0" : " AND s.session_id IN (" + string.Join(", ", started.Select(id => StartArg(id))) + ")");
        }

        foreach (var (firstMs, project) in sql.Query(startBuilder.ToString(), static reader => (reader.GetInt64(0), reader.GetString(1)), [.. startArgs]))
        {
            var bucket = plan.OfQuarter((int)Math.Floor(firstMs / (double)QuarterHour.Milliseconds));
            if (bucket >= 0)
            {
                Count(rows, bucket, byProject || byDelegation ? project : string.Empty);
            }
        }

        return [.. rows.Values];
    }

    private static void Count(Dictionary<string, AggRow> rows, int bucket, string key)
    {
        var id = bucket.ToString(CultureInfo.InvariantCulture) + "\u001f" + key;
        if (!rows.TryGetValue(id, out var row))
        {
            rows[id] = row = new AggRow(bucket, [key], [0]);
        }

        row.Values[0] += 1;
    }

    /// <summary>Makes the lines of a chart from the rows of an aggregation; the groups beyond the limit are added up as <c>other</c>.</summary>
    private IReadOnlyList<SeriesLine> Lines(ResolvedQuery query, BucketPlan plan, List<AggRow> rows, List<AggRow>? previous, string? group, double scale, int limit)
    {
        var count = plan.Buckets.Count;
        var lines = new Dictionary<string, (double[] Values, double[]? Previous)>(StringComparer.Ordinal);
        (double[] Values, double[]? Previous) Line(string key)
        {
            if (!lines.TryGetValue(key, out var line))
            {
                lines[key] = line = (new double[count], previous is null ? null : new double[query.ComparePlan!.Buckets.Count]);
            }

            return line;
        }

        foreach (var row in rows)
        {
            Line(row.Keys.Length == 0 ? string.Empty : row.Keys[0]).Values[row.Bucket] += row.Values[0] * scale;
        }

        if (previous is not null)
        {
            foreach (var row in previous)
            {
                Line(row.Keys.Length == 0 ? string.Empty : row.Keys[0]).Previous![row.Bucket] += row.Values[0] * scale;
            }
        }

        if (lines.Count == 0)
        {
            lines[string.Empty] = (new double[count], previous is null ? null : new double[query.ComparePlan!.Buckets.Count]);
        }

        var ordered = lines.OrderByDescending(static pair => pair.Value.Values.Sum()).ThenBy(static pair => pair.Key, StringComparer.Ordinal).ToList();
        var result = new List<SeriesLine>();
        foreach (var (key, line) in ordered.Take(limit))
        {
            result.Add(ToLine(query, group, key, line.Values, line.Previous));
        }

        if (ordered.Count > limit)
        {
            var values = new double[count];
            var others = previous is null ? null : new double[query.ComparePlan!.Buckets.Count];
            foreach (var (_, line) in ordered.Skip(limit))
            {
                for (var index = 0; index < count; index++)
                {
                    values[index] += line.Values[index];
                }

                if (others is not null && line.Previous is not null)
                {
                    for (var index = 0; index < others.Length; index++)
                    {
                        others[index] += line.Previous[index];
                    }
                }
            }

            result.Add(new SeriesLine("other", "other", values, others, values.Sum(), others?.Sum()));
        }

        return result;
    }

    private SeriesLine ToLine(ResolvedQuery query, string? group, string key, double[] values, double[]? previous)
    {
        var (name, label) = LineName(query, group, key);
        return new(name, label, values, previous, values.Sum(), previous?.Sum());
    }

    // The key and the label of a line of a group.
    private (string Key, string Label) LineName(ResolvedQuery query, string? group, string key)
    {
        var label = group is null ? "total" : GroupLabel(query, group, key);

        // A group of a fixed list is kept as a number in the facts: its line is keyed by its name, which is the value a filter takes.
        var named = group is "kind" or "origin" or "prompt-kind" or "content-kind" or "purpose";
        return (named ? label : key, label);
    }

    /// <summary>The numbers of the Overview: tiles with their change against the compared period and a line over the period.</summary>
    /// <param name="request">The period, the frequency, the filters and the comparison.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The tiles, and the costs by unit.</returns>
    /// <exception cref="ArgumentException">The period or the frequency is not valid.</exception>
    public async ValueTask<SummaryResult> SummaryAsync(StatisticsRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var query = await ResolveAsync(request, cancellationToken).ConfigureAwait(false);
        return await _store.ReadAsync(sql => BuildSummary(sql, query), cancellationToken).ConfigureAwait(false);
    }

    private SummaryResult BuildSummary(SqlSession sql, ResolvedQuery query)
    {
        var tiles = new List<SummaryTile>();

        void Add(string id, string unit, double[] current, double[]? previous)
            => tiles.Add(new SummaryTile(id, unit, current.Sum(), previous?.Sum(), Change(current.Sum(), previous?.Sum()), current));

        double[] Series(List<AggRow> rows, int index, BucketPlan plan, double scale = 1d)
        {
            var values = new double[plan.Buckets.Count];
            foreach (var row in rows)
            {
                values[row.Bucket] += row.Values[index] * scale;
            }

            return values;
        }

        (List<AggRow> Current, List<AggRow>? Previous) Read(FactTable family, string[] aggregates, string? where = null, string[]? groups = null)
            => (Aggregate(sql, query, query.Plan, family, aggregates, groups ?? [], where),
                query.ComparePlan is { } plan ? Aggregate(sql, query, plan, family, aggregates, groups ?? [], where) : null);

        var active = SessionCounts(sql, query, query.Plan, "sessions-active", null);
        var activeBefore = query.ComparePlan is { } comparePlan ? SessionCounts(sql, query, comparePlan, "sessions-active", null) : null;
        var activeTotal = DistinctActiveSessions(sql, query, query.Plan);
        var activeTotalBefore = query.ComparePlan is { } before ? DistinctActiveSessions(sql, query, before) : (double?)null;
        tiles.Add(new SummaryTile("sessions", "count", activeTotal, activeTotalBefore, Change(activeTotal, activeTotalBefore), Series(active, 0, query.Plan)));

        var activity = Read(FactTables.Activity, ["SUM(f.runs_started)", "SUM(f.active_ms)", "SUM(f.errors)", "SUM(f.compactions)"]);
        if (query.Filter.Origin is { Length: > 0 })
        {
            // Who sent the prompt of a run is known by the table of runs: the tile counts the runs of the origin.
            Add("runs", "count", Series(RunCounts(sql, query, query.Plan, byOrigin: false), 0, query.Plan), query.ComparePlan is { } runPlan ? Series(RunCounts(sql, query, runPlan, byOrigin: false), 0, runPlan) : null);
        }
        else
        {
            Add("runs", "count", Series(activity.Current, 0, query.Plan), activity.Previous is null ? null : Series(activity.Previous, 0, query.ComparePlan!));
        }
        Add("active-time", "ms", Series(activity.Current, 1, query.Plan), activity.Previous is null ? null : Series(activity.Previous, 1, query.ComparePlan!));

        var prompts = Read(FactTables.Content, ["SUM(f.count)"], "f.kind = 0 AND f.sender = 1");
        Add("your-prompts", "count", Series(prompts.Current, 0, query.Plan), prompts.Previous is null ? null : Series(prompts.Previous, 0, query.ComparePlan!));

        var usage = Read(FactTables.Usage, ["SUM(f.requests)", "SUM(f.input_tokens + f.output_tokens)", "SUM(f.input_tokens)", "SUM(f.output_tokens)", "SUM(f.fresh_input_tokens)", "SUM(f.cache_read_tokens)", "SUM(f.cache_write_tokens)", "SUM(f.reasoning_tokens)"]);
        string[] usageIds = ["requests", "tokens", "input-tokens", "output-tokens", "fresh-input-tokens", "cache-read-tokens", "cache-write-tokens", "reasoning-tokens"];
        for (var index = 0; index < usageIds.Length; index++)
        {
            Add(usageIds[index], index == 0 ? "count" : "tokens", Series(usage.Current, index, query.Plan), usage.Previous is null ? null : Series(usage.Previous, index, query.ComparePlan!));
        }

        var tools = Read(FactTables.Tools, ["SUM(f.calls)", "SUM(f.failures)", "SUM(f.lines_added)", "SUM(f.lines_removed)", "SUM(f.files_changed)"]);
        string[] toolIds = ["tool-calls", "tool-failures", "lines-added", "lines-removed", "files-changed"];
        string[] toolUnits = ["count", "count", "lines", "lines", "count"];
        for (var index = 0; index < toolIds.Length; index++)
        {
            Add(toolIds[index], toolUnits[index], Series(tools.Current, index, query.Plan), tools.Previous is null ? null : Series(tools.Previous, index, query.ComparePlan!));
        }

        Add("errors", "count", Series(activity.Current, 2, query.Plan), activity.Previous is null ? null : Series(activity.Previous, 2, query.ComparePlan!));
        Add("compactions", "count", Series(activity.Current, 3, query.Plan), activity.Previous is null ? null : Series(activity.Previous, 3, query.ComparePlan!));

        // Costs never add up across units: a tile for each unit that has a cost.
        var cost = Read(FactTables.Cost, ["SUM(f.total_micro)"], null, ["unit"]);
        var costs = new List<SummaryTile>();
        foreach (var unit in cost.Current.Select(static row => row.Keys[0]).Concat(cost.Previous?.Select(static row => row.Keys[0]) ?? []).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var current = Series([.. cost.Current.Where(row => row.Keys[0] == unit)], 0, query.Plan, 1e-6);
            var previous = cost.Previous is null ? null : Series([.. cost.Previous.Where(row => row.Keys[0] == unit)], 0, query.ComparePlan!, 1e-6);
            costs.Add(new SummaryTile(unit, unit, current.Sum(), previous?.Sum(), Change(current.Sum(), previous?.Sum()), current));
        }

        return new SummaryResult(Header(query), BucketInfos(query.Plan), tiles, costs);
    }

    private double DistinctActiveSessions(SqlSession sql, ResolvedQuery query, BucketPlan plan)
    {
        var single = BucketPlan.Single(plan.Range, plan.WeekStart, Days);
        return SessionCounts(sql, query, single, "sessions-active", null).Sum(static row => row.Values[0]);
    }
}
