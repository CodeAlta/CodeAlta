using System.Globalization;
using CodeAlta.Plugin.Statistics.Facts;
using CodeAlta.Plugin.Statistics.Store;

namespace CodeAlta.Plugin.Statistics.Query;

public sealed partial class StatisticsQueries
{
    /// <summary>Gets what <see cref="TopAsync"/> ranks.</summary>
    public static IReadOnlyList<string> TopKinds { get; } = ["tools", "models", "projects", "sessions"];

    /// <summary>Gets the measures <see cref="TopAsync"/> ranks by.</summary>
    public static IReadOnlyList<string> TopMeasures { get; } = ["tokens", "time", "calls"];

    /// <summary>The totals of one entity of a ranking.</summary>
    private sealed class Entity(string key)
    {
        public string Key { get; } = key;

        public string Label { get; set; } = key;

        public string? Detail { get; set; }

        public double Tokens { get; set; }

        public double TimeMs { get; set; }

        public double Calls { get; set; }

        public double Requests { get; set; }

        public double Failures { get; set; }

        public double Bytes { get; set; }

        public double Value(string by) => by switch
        {
            "time" => TimeMs,
            "calls" => Calls,
            _ => Tokens,
        };
    }

    /// <summary>Ranks the tools, the models, the projects or the sessions of a period.</summary>
    /// <param name="request">The period, the filters and the limit; the frequency cuts the lines of the rows.</param>
    /// <param name="kind">What to rank: one of <see cref="TopKinds"/>.</param>
    /// <param name="by">The measure: one of <see cref="TopMeasures"/>. For tools, <c>tokens</c> is the bytes of their arguments and results, and for models and sessions <c>calls</c> are the requests and the tool calls.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The rows, the largest first, with the share of each and its line over time.</returns>
    /// <exception cref="ArgumentException">The kind or the measure is not valid.</exception>
    public async ValueTask<TopResult> TopAsync(StatisticsRequest request, string kind, string by = "tokens", CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        var normalizedKind = kind.Trim().ToLowerInvariant();
        var measure = by.Trim().ToLowerInvariant();
        if (!TopKinds.Contains(normalizedKind, StringComparer.Ordinal))
        {
            throw new ArgumentException($"'{kind}' cannot be ranked: use one of {string.Join(", ", TopKinds)}.");
        }

        if (!TopMeasures.Contains(measure, StringComparer.Ordinal))
        {
            throw new ArgumentException($"'{by}' is not a measure: use one of {string.Join(", ", TopMeasures)}.");
        }

        var query = await ResolveAsync(request, cancellationToken).ConfigureAwait(false);
        return await _store.ReadAsync(sql => BuildTop(sql, query, normalizedKind, measure), cancellationToken).ConfigureAwait(false);
    }

    private TopResult BuildTop(SqlSession sql, ResolvedQuery query, string kind, string by)
    {
        var single = BucketPlan.Single(query.Range, query.WeekStart, Days);
        var entities = Entities(sql, query, single, kind, null);
        var ordered = entities.Values.OrderByDescending(entity => entity.Value(by)).ThenBy(static entity => entity.Label, StringComparer.OrdinalIgnoreCase).ToList();
        var limit = LimitOf(query.Request, 10);
        var top = ordered.Take(limit).ToList();
        var total = ordered.Sum(entity => entity.Value(by));
        var sparks = Sparks(sql, query, kind, by, top.Select(static entity => entity.Key).ToArray());
        var unit = kind == "tools" && by == "tokens" ? "bytes" : by switch { "time" => "ms", "calls" => "count", _ => "tokens" };
        var rows = top.Select(entity => new RankedRow(
                entity.Key,
                entity.Label,
                entity.Detail,
                kind == "tools" && by == "tokens" ? entity.Bytes : entity.Value(by),
                total > 0 ? entity.Value(by) / total : 0,
                entity.Tokens,
                entity.TimeMs,
                entity.Calls,
                entity.Requests,
                kind == "tools" ? entity.Failures : null,
                sparks.TryGetValue(entity.Key, out var spark) ? spark : new double[query.Plan.Buckets.Count]))
            .ToList();
        return new TopResult(Header(query), kind, by, unit, BucketInfos(query.Plan), rows, ordered.Count, ordered.Count > top.Count);
    }

    /// <summary>Reads the totals of every entity of a kind over a period: the tools, models, projects or sessions.</summary>
    private Dictionary<string, Entity> Entities(SqlSession sql, ResolvedQuery query, BucketPlan plan, string kind, IReadOnlyList<string>? restrictKeys)
    {
        var entities = new Dictionary<string, Entity>(StringComparer.Ordinal);
        Entity Get(string key, string? label = null, string? detail = null)
        {
            if (!entities.TryGetValue(key, out var entity))
            {
                entities[key] = entity = new Entity(key) { Label = label ?? key, Detail = detail };
            }

            return entity;
        }

        switch (kind)
        {
            case "tools":
                foreach (var row in Aggregate(sql, query, plan, FactTables.Tools, ["SUM(f.calls)", "SUM(f.failures)", "SUM(f.duration_ms_total)", "SUM(f.bytes_in + f.bytes_out)"], ["kind", "tool"], restrict: Restrict("tool", restrictKeys)))
                {
                    var entity = Get(row.Keys[1], row.Keys[1], GroupLabel(query, "kind", row.Keys[0]));
                    entity.Calls += row.Values[0];
                    entity.Failures += row.Values[1];
                    entity.TimeMs += row.Values[2];
                    entity.Bytes += row.Values[3];
                }

                foreach (var entity in entities.Values)
                {
                    entity.Tokens = entity.Bytes;
                }

                break;
            case "models":
                foreach (var row in Aggregate(sql, query, plan, FactTables.Usage, ["SUM(f.requests)", "SUM(f.input_tokens + f.output_tokens)"], ["provider", "model"], restrict: Restrict("model", restrictKeys, key => key)))
                {
                    var entity = Get(row.Keys[1], row.Keys[1], row.Keys[0]);
                    entity.Requests += row.Values[0];
                    entity.Calls += row.Values[0];
                    entity.Tokens += row.Values[1];
                }

                foreach (var row in Aggregate(sql, query, plan, FactTables.Activity, ["SUM(f.active_ms)"], ["provider", "model"], restrict: Restrict("model", restrictKeys)))
                {
                    Get(row.Keys[1], row.Keys[1], row.Keys[0]).TimeMs += row.Values[0];
                }

                break;
            case "projects":
                AddByKey(sql, query, plan, entities, "project", restrictKeys, key => Get(key, key.Length == 0 ? "(no project)" : query.ProjectName(key)));
                break;
            default:
                AddByKey(sql, query, plan, entities, "session", restrictKeys, key => Get(key));
                break;
        }

        if (kind == "sessions" && entities.Count > 0)
        {
            NameSessions(sql, query, entities);
        }

        return entities;
    }

    private void AddByKey(SqlSession sql, ResolvedQuery query, BucketPlan plan, Dictionary<string, Entity> entities, string group, IReadOnlyList<string>? restrictKeys, Func<string, Entity> get)
    {
        foreach (var row in Aggregate(sql, query, plan, FactTables.Usage, ["SUM(f.requests)", "SUM(f.input_tokens + f.output_tokens)"], [group], restrict: Restrict(group, restrictKeys)))
        {
            var entity = get(row.Keys[0]);
            entity.Requests += row.Values[0];
            entity.Tokens += row.Values[1];
        }

        foreach (var row in Aggregate(sql, query, plan, FactTables.Activity, ["SUM(f.active_ms)"], [group], restrict: Restrict(group, restrictKeys)))
        {
            get(row.Keys[0]).TimeMs += row.Values[0];
        }

        foreach (var row in Aggregate(sql, query, plan, FactTables.Tools, ["SUM(f.calls)", "SUM(f.failures)"], [group], restrict: Restrict(group, restrictKeys)))
        {
            var entity = get(row.Keys[0]);
            entity.Calls += row.Values[0];
            entity.Failures += row.Values[1];
        }
    }

    private void NameSessions(SqlSession sql, ResolvedQuery query, Dictionary<string, Entity> entities)
    {
        foreach (var chunk in entities.Keys.Chunk(400))
        {
            var rows = sql.Query(
                $"SELECT session_id, title, project_ref FROM {P}session WHERE session_id IN ({string.Join(", ", chunk.Select(static (_, index) => "@p" + index))})",
                static reader => (reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2)),
                [.. chunk.Cast<object?>()]);
            foreach (var (id, title, project) in rows)
            {
                var entity = entities[id];
                entity.Label = string.IsNullOrWhiteSpace(title) ? id : title;
                entity.Detail = project is null ? null : query.ProjectName(project);
            }
        }
    }

    private static IReadOnlyList<(string Group, IReadOnlyList<string> Keys)>? Restrict(string group, IReadOnlyList<string>? keys, Func<string, string>? map = null)
        => keys is null ? null : [(group, map is null ? keys : [.. keys.Select(map)])];

    /// <summary>Reads the line over time of the given entities, by the measure of the ranking.</summary>
    private Dictionary<string, IReadOnlyList<double>> Sparks(SqlSession sql, ResolvedQuery query, string kind, string by, string[] keys)
    {
        var result = new Dictionary<string, IReadOnlyList<double>>(StringComparer.Ordinal);
        if (keys.Length == 0)
        {
            return result;
        }

        var plan = query.Plan;
        void Collect(List<AggRow> rows, int keyIndex)
        {
            foreach (var row in rows)
            {
                var key = row.Keys[keyIndex];
                if (!result.TryGetValue(key, out var values))
                {
                    result[key] = values = new double[plan.Buckets.Count];
                }

                ((double[])values)[row.Bucket] += row.Values[0];
            }
        }

        switch (kind)
        {
            case "tools":
                var toolAggregate = by switch { "time" => "SUM(f.duration_ms_total)", "calls" => "SUM(f.calls)", _ => "SUM(f.bytes_in + f.bytes_out)" };
                Collect(Aggregate(sql, query, plan, FactTables.Tools, [toolAggregate], ["tool"], restrict: Restrict("tool", keys)), 0);
                break;
            case "models":
                if (by == "time")
                {
                    Collect(Aggregate(sql, query, plan, FactTables.Activity, ["SUM(f.active_ms)"], ["model"], restrict: Restrict("model", keys)), 0);
                }
                else
                {
                    Collect(Aggregate(sql, query, plan, FactTables.Usage, [by == "calls" ? "SUM(f.requests)" : "SUM(f.input_tokens + f.output_tokens)"], ["model"], restrict: Restrict("model", keys)), 0);
                }

                break;
            default:
                var group = kind == "projects" ? "project" : "session";
                var (family, aggregate) = by switch
                {
                    "time" => (FactTables.Activity, "SUM(f.active_ms)"),
                    "calls" => (FactTables.Tools, "SUM(f.calls)"),
                    _ => (FactTables.Usage, "SUM(f.input_tokens + f.output_tokens)"),
                };
                Collect(Aggregate(sql, query, plan, family, [aggregate], [group], restrict: Restrict(group, keys)), 0);
                break;
        }

        return result;
    }

    // --- The tables of the pages ---

    /// <summary>The tools of a period: calls, failures, time, the median and the 90th percentile of the duration, bytes, and a line each.</summary>
    /// <param name="request">The period, the frequency, the filters and the limit.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The tools, the most called first, and the calls by kind.</returns>
    /// <exception cref="ArgumentException">The period or the frequency is not valid.</exception>
    public async ValueTask<ToolsResult> ToolsAsync(StatisticsRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var query = await ResolveAsync(request, cancellationToken).ConfigureAwait(false);
        return await _store.ReadAsync(sql => BuildTools(sql, query), cancellationToken).ConfigureAwait(false);
    }

    private ToolsResult BuildTools(SqlSession sql, ResolvedQuery query)
    {
        var rows = ToolRows(sql, query, null, out var kinds, out var total, int.MaxValue);
        var limit = LimitOf(query.Request);
        var top = rows.Take(limit).ToList();
        var sparks = Sparks(sql, query, "tools", "calls", top.Select(static row => row.Tool).ToArray());
        var shaped = top.Select(row => row with { Spark = sparks.TryGetValue(row.Tool, out var spark) ? spark : new double[query.Plan.Buckets.Count] }).ToList();
        return new ToolsResult(Header(query), BucketInfos(query.Plan), shaped, kinds, total, total > shaped.Count);
    }

    private List<ToolRow> ToolRows(SqlSession sql, ResolvedQuery query, IReadOnlyList<string>? onlyTools, out IReadOnlyList<KeyValuePair<string, long>> kinds, out int total, int take)
    {
        var single = BucketPlan.Single(query.Range, query.WeekStart, Days);
        var aggregates = Aggregate(
            sql,
            query,
            single,
            FactTables.Tools,
            ["SUM(f.calls)", "SUM(f.failures)", "SUM(f.duration_ms_total)", "MAX(f.duration_ms_max)", "SUM(f.bytes_in)", "SUM(f.bytes_out)"],
            ["kind", "tool"],
            restrict: Restrict("tool", onlyTools));
        var durations = Percentiles(sql, query, single, HistogramMeasure.ToolDurationMs, null, perSubject: true);
        var rows = aggregates
            .GroupBy(static row => (row.Keys[0], row.Keys[1]))
            .Select(group =>
            {
                var calls = group.Sum(static row => row.Values[0]);
                var failures = group.Sum(static row => row.Values[1]);
                durations.TryGetValue(group.Key.Item2, out var percentiles);
                return new ToolRow(
                    GroupLabel(query, "kind", group.Key.Item1),
                    group.Key.Item2,
                    (long)calls,
                    (long)failures,
                    calls > 0 ? failures / calls : 0,
                    group.Sum(static row => row.Values[2]),
                    percentiles?.P50,
                    percentiles?.P90,
                    group.Max(static row => row.Values[3]),
                    (long)group.Sum(static row => row.Values[4]),
                    (long)group.Sum(static row => row.Values[5]),
                    []);
            })
            .OrderByDescending(static row => row.Calls)
            .ThenBy(static row => row.Tool, StringComparer.Ordinal)
            .ToList();
        kinds = [.. rows.GroupBy(static row => row.Kind).Select(static group => new KeyValuePair<string, long>(group.Key, group.Sum(static row => row.Calls))).OrderByDescending(static pair => pair.Value)];
        total = rows.Count;
        return rows.Take(take).ToList();
    }

    /// <summary>The models of a period: requests, tokens of each kind, cache share, time, context fill, costs by unit, and a line each.</summary>
    /// <param name="request">The period, the frequency, the filters and the limit.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The models, the most used first, and the models by reasoning effort.</returns>
    /// <exception cref="ArgumentException">The period or the frequency is not valid.</exception>
    public async ValueTask<ModelsResult> ModelsAsync(StatisticsRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var query = await ResolveAsync(request, cancellationToken).ConfigureAwait(false);
        return await _store.ReadAsync(sql => BuildModels(sql, query), cancellationToken).ConfigureAwait(false);
    }

    private ModelsResult BuildModels(SqlSession sql, ResolvedQuery query)
    {
        var rows = ModelRows(sql, query, int.MaxValue);
        var limit = LimitOf(query.Request);
        var top = rows.Take(limit).ToList();
        var sparks = Sparks(sql, query, "models", "tokens", top.Select(static row => row.Model).ToArray());
        var shaped = top.Select(row => row with { Spark = sparks.TryGetValue(row.Model, out var spark) ? spark : new double[query.Plan.Buckets.Count] }).ToList();

        var single = BucketPlan.Single(query.Range, query.WeekStart, Days);
        var efforts = Aggregate(sql, query, single, FactTables.Usage, ["SUM(f.requests)", "SUM(f.input_tokens + f.output_tokens)", "SUM(f.reasoning_tokens)", "SUM(f.output_tokens)"], ["provider", "model", "effort"])
            .Select(static row => new EffortRow(row.Keys[0], row.Keys[1], row.Keys[2], (long)row.Values[0], (long)row.Values[1], row.Values[3] > 0 ? row.Values[2] / row.Values[3] : 0))
            .OrderByDescending(static row => row.Tokens)
            .Take(MaxLimit)
            .ToList();
        return new ModelsResult(Header(query), BucketInfos(query.Plan), shaped, efforts, rows.Count, rows.Count > shaped.Count);
    }

    private List<ModelRow> ModelRows(SqlSession sql, ResolvedQuery query, int take)
    {
        var single = BucketPlan.Single(query.Range, query.WeekStart, Days);
        var usage = Aggregate(
            sql,
            query,
            single,
            FactTables.Usage,
            ["SUM(f.requests)", "SUM(f.input_tokens)", "SUM(f.fresh_input_tokens)", "SUM(f.cache_read_tokens)", "SUM(f.cache_write_tokens)", "SUM(f.output_tokens)", "SUM(f.reasoning_tokens)", "SUM(f.context_fill_ppm_sum)", "SUM(f.context_samples)", "MAX(f.context_fill_ppm_max)"],
            ["provider", "model"]);
        var time = Aggregate(sql, query, single, FactTables.Activity, ["SUM(f.active_ms)"], ["provider", "model"]).ToDictionary(static row => (row.Keys[0], row.Keys[1]), static row => row.Values[0]);
        var costs = Aggregate(sql, query, single, FactTables.Cost, ["SUM(f.total_micro)"], ["provider", "model", "unit"])
            .GroupBy(static row => (row.Keys[0], row.Keys[1]))
            .ToDictionary(static group => group.Key, static group => (IReadOnlyList<CostAmount>)[.. group.Select(static row => new CostAmount(row.Keys[2], row.Values[0] / 1e6)).OrderBy(static amount => amount.Unit, StringComparer.Ordinal)]);
        return usage
            .Select(row =>
            {
                var key = (row.Keys[0], row.Keys[1]);
                var input = row.Values[1];
                var samples = row.Values[8];
                return new ModelRow(
                    row.Keys[0],
                    row.Keys[1],
                    (long)row.Values[0],
                    (long)input,
                    (long)row.Values[2],
                    (long)row.Values[3],
                    (long)row.Values[4],
                    (long)row.Values[5],
                    (long)row.Values[6],
                    input > 0 ? row.Values[3] / input : 0,
                    time.GetValueOrDefault(key),
                    samples > 0 ? row.Values[7] / samples / 1e6 : null,
                    row.Values[9] / 1e6,
                    costs.GetValueOrDefault(key) ?? [],
                    []);
            })
            .OrderByDescending(static row => row.InputTokens + row.OutputTokens)
            .ThenBy(static row => row.Model, StringComparer.Ordinal)
            .Take(take)
            .ToList();
    }

    /// <summary>The projects of a period: sessions, runs, time, tokens, tool calls, costs by unit, and a line each.</summary>
    /// <param name="request">The period, the frequency, the filters and the limit.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The projects, the most active first.</returns>
    /// <exception cref="ArgumentException">The period or the frequency is not valid.</exception>
    public async ValueTask<ProjectsResult> ProjectsAsync(StatisticsRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var query = await ResolveAsync(request, cancellationToken).ConfigureAwait(false);
        return await _store.ReadAsync(sql => BuildProjects(sql, query), cancellationToken).ConfigureAwait(false);
    }

    private ProjectsResult BuildProjects(SqlSession sql, ResolvedQuery query)
    {
        var single = BucketPlan.Single(query.Range, query.WeekStart, Days);
        var map = new Dictionary<string, (double Runs, double Time, double Tokens, double Calls)>(StringComparer.Ordinal);
        void Merge(string key, double runs = 0, double time = 0, double tokens = 0, double calls = 0)
        {
            var current = map.GetValueOrDefault(key);
            map[key] = (current.Runs + runs, current.Time + time, current.Tokens + tokens, current.Calls + calls);
        }

        foreach (var row in Aggregate(sql, query, single, FactTables.Activity, ["SUM(f.runs_started)", "SUM(f.active_ms)"], ["project"]))
        {
            Merge(row.Keys[0], runs: row.Values[0], time: row.Values[1]);
        }

        foreach (var row in Aggregate(sql, query, single, FactTables.Usage, ["SUM(f.input_tokens + f.output_tokens)"], ["project"]))
        {
            Merge(row.Keys[0], tokens: row.Values[0]);
        }

        foreach (var row in Aggregate(sql, query, single, FactTables.Tools, ["SUM(f.calls)"], ["project"]))
        {
            Merge(row.Keys[0], calls: row.Values[0]);
        }

        var costs = Aggregate(sql, query, single, FactTables.Cost, ["SUM(f.total_micro)"], ["project", "unit"])
            .GroupBy(static row => row.Keys[0])
            .ToDictionary(static group => group.Key, static group => (IReadOnlyList<CostAmount>)[.. group.Select(static row => new CostAmount(row.Keys[1], row.Values[0] / 1e6)).OrderBy(static amount => amount.Unit, StringComparer.Ordinal)]);
        var sessions = SessionCounts(sql, query, single, "sessions-active", "project").ToDictionary(static row => row.Keys[0], static row => (int)row.Values[0]);

        var ordered = map.OrderByDescending(static pair => pair.Value.Time).ThenByDescending(static pair => pair.Value.Tokens).ThenBy(static pair => pair.Key, StringComparer.Ordinal).ToList();
        var limit = LimitOf(query.Request);
        var top = ordered.Take(limit).ToList();
        var sparks = Sparks(sql, query, "projects", "time", top.Select(static pair => pair.Key).ToArray());
        var rows = top.Select(pair => new ProjectRow(
                pair.Key,
                pair.Key.Length == 0 ? "(no project)" : query.ProjectName(pair.Key),
                sessions.GetValueOrDefault(pair.Key),
                (long)pair.Value.Runs,
                pair.Value.Time,
                pair.Value.Tokens,
                pair.Value.Calls,
                costs.GetValueOrDefault(pair.Key) ?? [],
                sparks.TryGetValue(pair.Key, out var spark) ? spark : new double[query.Plan.Buckets.Count]))
            .ToList();
        return new ProjectsResult(Header(query), BucketInfos(query.Plan), rows, ordered.Count, ordered.Count > rows.Count);
    }

    /// <summary>Gets the ways <see cref="SessionsAsync"/> sorts.</summary>
    public static IReadOnlyList<string> SessionSorts { get; } = ["recent", "time", "tokens", "calls", "runs"];

    /// <summary>The sessions that were active in a period, with their numbers; a session that was deleted since is listed with its numbers.</summary>
    /// <param name="request">The period, the filters and the limit.</param>
    /// <param name="sort">The measure the rows are sorted on, largest first: one of <see cref="SessionSorts"/>.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The sessions.</returns>
    /// <exception cref="ArgumentException">The sort or the period is not valid.</exception>
    public async ValueTask<SessionsResult> SessionsAsync(StatisticsRequest request, string sort = "recent", CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var key = (sort ?? "recent").Trim().ToLowerInvariant();
        if (!SessionSorts.Contains(key, StringComparer.Ordinal))
        {
            throw new ArgumentException($"'{sort}' is not a sort: use one of {string.Join(", ", SessionSorts)}.");
        }

        var query = await ResolveAsync(request, cancellationToken).ConfigureAwait(false);
        return await _store.ReadAsync(sql => BuildSessions(sql, query, key, null), cancellationToken).ConfigureAwait(false);
    }

    private SessionsResult BuildSessions(SqlSession sql, ResolvedQuery query, string sort, IReadOnlyList<string>? only)
    {
        var rows = SessionEntries(sql, query, only, sort, int.MaxValue, out var total);
        var limit = LimitOf(query.Request);
        var shaped = rows.Take(limit).ToList();
        return new SessionsResult(Header(query), sort, shaped, total, total > shaped.Count);
    }

    private List<SessionEntry> SessionEntries(SqlSession sql, ResolvedQuery query, IReadOnlyList<string>? only, string sort, int take, out int total)
    {
        var single = BucketPlan.Single(query.Range, query.WeekStart, Days);
        var scope = query.SessionIds;
        if (only is not null)
        {
            query.SessionIds = only;
        }

        var map = new Dictionary<string, (double Runs, double Time, double Tokens, double Calls)>(StringComparer.Ordinal);
        void Merge(string key, double runs = 0, double time = 0, double tokens = 0, double calls = 0)
        {
            var current = map.GetValueOrDefault(key);
            map[key] = (current.Runs + runs, current.Time + time, current.Tokens + tokens, current.Calls + calls);
        }

        try
        {
            foreach (var row in Aggregate(sql, query, single, FactTables.Activity, ["SUM(f.runs_started)", "SUM(f.active_ms)"], ["session"]))
            {
                Merge(row.Keys[0], runs: row.Values[0], time: row.Values[1]);
            }

            foreach (var row in Aggregate(sql, query, single, FactTables.Usage, ["SUM(f.input_tokens + f.output_tokens)"], ["session"]))
            {
                Merge(row.Keys[0], tokens: row.Values[0]);
            }

            foreach (var row in Aggregate(sql, query, single, FactTables.Tools, ["SUM(f.calls)"], ["session"]))
            {
                Merge(row.Keys[0], calls: row.Values[0]);
            }

            var costs = Aggregate(sql, query, single, FactTables.Cost, ["SUM(f.total_micro)"], ["session", "unit"])
                .GroupBy(static row => row.Keys[0])
                .ToDictionary(static group => group.Key, static group => (IReadOnlyList<CostAmount>)[.. group.Select(static row => new CostAmount(row.Keys[1], row.Values[0] / 1e6)).OrderBy(static amount => amount.Unit, StringComparer.Ordinal)]);
            var models = Aggregate(sql, query, single, FactTables.Usage, ["SUM(f.input_tokens + f.output_tokens)"], ["session", "model"])
                .GroupBy(static row => row.Keys[0])
                .ToDictionary(static group => group.Key, static group => group.OrderByDescending(static row => row.Values[0]).First().Keys[1]);

            var info = new Dictionary<string, (string? Title, string? Project, string? Provider, string? Parent, long LastMs, bool Deleted)>(StringComparer.Ordinal);
            foreach (var chunk in map.Keys.Chunk(400))
            {
                foreach (var (id, title, project, provider, parent, last, deleted) in sql.Query(
                             $"SELECT session_id, title, project_ref, provider, parent_session_id, COALESCE(last_ms, 0), deleted FROM {P}session WHERE session_id IN ({string.Join(", ", chunk.Select(static (_, index) => "@p" + index))})",
                             static reader => (reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetInt64(5), reader.GetInt64(6) != 0),
                             [.. chunk.Cast<object?>()]))
                {
                    info[id] = (title, project, provider, parent, last, deleted);
                }
            }

            var children = sql.Query($"SELECT parent_session_id, COUNT(*) FROM {P}session WHERE parent_session_id IS NOT NULL GROUP BY parent_session_id", static reader => (reader.GetString(0), (int)reader.GetInt64(1)))
                .ToDictionary(static pair => pair.Item1, static pair => pair.Item2, StringComparer.Ordinal);

            IOrderedEnumerable<KeyValuePair<string, (double Runs, double Time, double Tokens, double Calls)>> ordered = sort switch
            {
                "time" => map.OrderByDescending(static pair => pair.Value.Time),
                "tokens" => map.OrderByDescending(static pair => pair.Value.Tokens),
                "calls" => map.OrderByDescending(static pair => pair.Value.Calls),
                "runs" => map.OrderByDescending(static pair => pair.Value.Runs),
                _ => map.OrderByDescending(pair => info.TryGetValue(pair.Key, out var session) ? session.LastMs : 0),
            };
            var list = ordered.ThenBy(static pair => pair.Key, StringComparer.Ordinal).ToList();
            total = list.Count;
            return list.Take(take).Select(pair =>
            {
                info.TryGetValue(pair.Key, out var session);
                return new SessionEntry(
                    pair.Key,
                    session.Title,
                    session.Project,
                    session.Project is null ? null : query.ProjectName(session.Project),
                    session.Provider,
                    models.GetValueOrDefault(pair.Key),
                    session.Parent,
                    (long)pair.Value.Runs,
                    pair.Value.Time,
                    pair.Value.Tokens,
                    pair.Value.Calls,
                    costs.GetValueOrDefault(pair.Key) ?? [],
                    children.GetValueOrDefault(pair.Key),
                    session.LastMs > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(session.LastMs).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) : null,
                    session.Deleted);
            }).ToList();
        }
        finally
        {
            query.SessionIds = scope;
        }
    }
}
