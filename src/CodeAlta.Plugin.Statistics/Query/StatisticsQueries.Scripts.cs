using System.Globalization;
using System.Text;
using CodeAlta.Plugin.Statistics.Facts;
using CodeAlta.Plugin.Statistics.Store;

namespace CodeAlta.Plugin.Statistics.Query;

public sealed partial class StatisticsQueries
{
    private const int RecentScriptRuns = 20;

    /// <summary>
    /// The runs of scripts of Code Mode in a period: their totals, each script with its failures, durations and the sessions it
    /// started (their tokens and costs are those of the sessions in the period), how much each model sends scripts, the turns that
    /// send one beside those that do not, and the latest runs.
    /// </summary>
    /// <param name="request">The period, the frequency, the filters (provider, model, origin, project, space, session) and the limit of scripts.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The runs of scripts of the period.</returns>
    /// <exception cref="ArgumentException">The period or the frequency is not valid.</exception>
    public async ValueTask<ScriptsResult> ScriptsAsync(StatisticsRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var query = await ResolveAsync(request, cancellationToken).ConfigureAwait(false);
        return await _store.ReadAsync(sql => BuildScripts(sql, query), cancellationToken).ConfigureAwait(false);
    }

    private ScriptsResult BuildScripts(SqlSession sql, ResolvedQuery query)
    {
        if (query.Filter.Effort is { Length: > 0 })
        {
            query.Ignored.Add("effort");
        }

        if (query.Filter.ToolKind is { Length: > 0 })
        {
            query.Ignored.Add("toolKind");
        }

        var single = BucketPlan.Single(query.Range, query.WeekStart, Days);
        string[] measures = ["SUM(f.runs_started)", "SUM(f.runs_completed)", "SUM(f.runs_failed)", "SUM(f.runs_cancelled)", "SUM(f.runs_interrupted)", "SUM(f.calls)",
            "SUM(f.sessions_started)", "SUM(f.duration_ms)", "SUM(f.wait_ms)", "SUM(f.steps)", "SUM(f.steps_failed)"];
        var byScript = Aggregate(sql, query, single, FactTables.Scripts, measures, ["script"]);
        double Sum(IEnumerable<AggRow> rows, int index) => rows.Sum(row => row.Values[index]);
        var ended = Sum(byScript, 1) + Sum(byScript, 2) + Sum(byScript, 3) + Sum(byScript, 4);
        var toolCalls = Aggregate(sql, query, single, FactTables.Tools, ["SUM(f.script_calls)"], []).Sum(static row => row.Values[0]);
        var (tokens, costs) = ScriptSessionUsage(sql, query);

        var totals = new ScriptTotals(
            (long)Sum(byScript, 0), (long)Sum(byScript, 1), (long)Sum(byScript, 2), (long)Sum(byScript, 3), (long)Sum(byScript, 4),
            ended > 0 ? Sum(byScript, 1) / ended : null,
            (long)Sum(byScript, 5),
            (long)toolCalls,
            (long)Sum(byScript, 6),
            (long)tokens.Values.Sum(),
            [.. costs.SelectMany(static pair => pair.Value).GroupBy(static pair => pair.Key).Select(static group => new CostAmount(group.Key, group.Sum(static pair => pair.Value))).OrderBy(static cost => cost.Unit, StringComparer.Ordinal)],
            Sum(byScript, 7),
            Sum(byScript, 8),
            (long)Sum(byScript, 9),
            (long)Sum(byScript, 10));

        var durations = Percentiles(sql, query, single, HistogramMeasure.ScriptRunDurationMs, null, perSubject: true);
        var lastRuns = ScriptLastRuns(sql, query);
        var ordered = byScript
            .GroupBy(static row => row.Keys[0], StringComparer.Ordinal)
            .Select(static group => (Script: group.Key, Values: Enumerable.Range(0, group.First().Values.Length).Select(index => group.Sum(row => row.Values[index])).ToArray()))
            .Where(static row => row.Values.Any(static value => value != 0))
            .OrderByDescending(static row => row.Values[0])
            .ThenBy(static row => row.Script, StringComparer.Ordinal)
            .ToList();
        var top = ordered.Take(LimitOf(query.Request)).ToList();
        var sparks = new Dictionary<string, double[]>(StringComparer.Ordinal);
        if (top.Count > 0)
        {
            foreach (var row in Aggregate(sql, query, query.Plan, FactTables.Scripts, ["SUM(f.runs_started)"], ["script"], restrict: [("script", [.. top.Select(static item => item.Script)])]))
            {
                if (!sparks.TryGetValue(row.Keys[0], out var spark))
                {
                    sparks[row.Keys[0]] = spark = new double[query.Plan.Buckets.Count];
                }

                spark[row.Bucket] += row.Values[0];
            }
        }

        var scripts = top.Select(item =>
        {
            var finished = item.Values[1] + item.Values[2] + item.Values[3] + item.Values[4];
            durations.TryGetValue(item.Script, out var percentiles);
            return new ScriptRow(
                item.Script,
                (long)item.Values[0],
                (long)item.Values[2],
                finished > 0 ? item.Values[2] / finished : 0,
                percentiles?.P50,
                percentiles?.P90,
                (long)item.Values[6],
                (long)tokens.GetValueOrDefault(item.Script),
                costs.TryGetValue(item.Script, out var cost) ? [.. cost.Select(static pair => new CostAmount(pair.Key, pair.Value)).OrderBy(static amount => amount.Unit, StringComparer.Ordinal)] : [],
                lastRuns.TryGetValue(item.Script, out var last) ? last : null,
                sparks.TryGetValue(item.Script, out var values) ? values : new double[query.Plan.Buckets.Count]);
        }).ToList();

        return new ScriptsResult(Header(query), BucketInfos(query.Plan), totals, scripts, ScriptAdoption(sql, query, single), ScriptTurns(sql, query), RecentScriptRunsOf(sql, query),
            ordered.Count, ordered.Count > scripts.Count);
    }

    // The tokens and the costs of the sessions scripts started, by the script of the run that started each: a session records its
    // run (`workflow:<run>`), and the run is one of its parent.
    private (Dictionary<string, double> Tokens, Dictionary<string, Dictionary<string, double>> Costs) ScriptSessionUsage(SqlSession sql, ResolvedQuery query)
    {
        var tokens = new Dictionary<string, double>(StringComparer.Ordinal);
        var costs = new Dictionary<string, Dictionary<string, double>>(StringComparer.Ordinal);
        foreach (var (family, value, unit) in new[] { (FactTables.Usage, "SUM(f.input_tokens + f.output_tokens)", (string?)null), (FactTables.Cost, "SUM(f.total_micro)", "f.unit") })
        {
            var args = new List<object?>();
            string Arg(object? item)
            {
                args.Add(item);
                return "@p" + (args.Count - 1).ToString(CultureInfo.InvariantCulture);
            }

            var (lower, upper) = Days.QuarterRangeOfDays(LocalDays.ToDay(query.Range.From), LocalDays.ToDay(query.Range.To));
            var select = new StringBuilder($"SELECT r.script, {unit ?? "''"}, {value} FROM {family.TableName(P, RollupLevel.Quarter)} f JOIN {P}session s ON s.session_id = f.session_id ")
                .Append($"JOIN {P}script_run r ON r.session_id = s.parent_session_id AND r.run_id = s.script_run_id WHERE f.q >= {Arg((long)lower)} AND f.q < {Arg((long)upper)}");
            AppendFilters(select, query, family, Arg, applyOrigin: false);
            if (query.SessionIds is { } scope)
            {
                select.Append(scope.Count == 0 ? " AND 0" : " AND f.session_id IN (" + string.Join(", ", scope.Select(id => Arg(id))) + ")");
            }

            select.Append(unit is null ? " GROUP BY r.script" : " GROUP BY r.script, f.unit");
            foreach (var (script, costUnit, amount) in sql.Query(select.ToString(), static reader => (reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? 0 : reader.GetDouble(2)), [.. args]))
            {
                if (unit is null)
                {
                    tokens[script] = tokens.GetValueOrDefault(script) + amount;
                }
                else
                {
                    if (!costs.TryGetValue(script, out var byUnit))
                    {
                        costs[script] = byUnit = new Dictionary<string, double>(StringComparer.Ordinal);
                    }

                    byUnit[costUnit] = byUnit.GetValueOrDefault(costUnit) + (amount * 1e-6);
                }
            }
        }

        return (tokens, costs);
    }

    // The filters of the table of the runs of scripts (`r`), for the period they started in.
    private string ScriptRunWhere(ResolvedQuery query, List<object?> args)
    {
        string Arg(object? value)
        {
            args.Add(value);
            return "@p" + (args.Count - 1).ToString(CultureInfo.InvariantCulture);
        }

        var (lower, upper) = Days.QuarterRangeOfDays(LocalDays.ToDay(query.Range.From), LocalDays.ToDay(query.Range.To));
        var where = new StringBuilder(" WHERE r.start_ms >= ").Append(Arg(lower * QuarterHour.Milliseconds)).Append(" AND r.start_ms < ").Append(Arg(upper * QuarterHour.Milliseconds));
        var filter = query.Filter;
        if (filter.Provider is { Length: > 0 } provider)
        {
            where.Append(" AND r.provider = ").Append(Arg(provider)).Append(" COLLATE NOCASE");
        }

        if (filter.Model is { Length: > 0 } model)
        {
            where.Append(" AND r.model = ").Append(Arg(model)).Append(" COLLATE NOCASE");
        }

        if (filter.Origin is { Length: > 0 } origin && TryParseOrigin(origin, out var sender))
        {
            where.Append(" AND r.origin = ").Append(Arg(sender.ToString().ToLowerInvariant()));
        }

        where.Append(query.ProjectClause("s.project_ref", reference => Arg(reference)));
        if (query.SessionIds is { } sessions)
        {
            where.Append(sessions.Count == 0 ? " AND 0" : " AND r.session_id IN (" + string.Join(", ", sessions.Select(id => Arg(id))) + ")");
        }

        return where.ToString();
    }

    private string ScriptRunJoin(ResolvedQuery query)
        => query.ProjectRefs is not null ? $" LEFT JOIN {P}session s ON s.session_id = r.session_id" : string.Empty;

    private Dictionary<string, string> ScriptLastRuns(SqlSession sql, ResolvedQuery query)
    {
        var args = new List<object?>();
        var where = ScriptRunWhere(query, args);
        return sql.Query($"SELECT r.script, MAX(r.start_ms) FROM {P}script_run r{ScriptRunJoin(query)}{where} GROUP BY r.script",
                static reader => (reader.GetString(0), Instant(reader.GetInt64(1))), [.. args])
            .ToDictionary(static pair => pair.Item1, static pair => pair.Item2, StringComparer.Ordinal);
    }

    private List<ScriptRunSample> RecentScriptRunsOf(SqlSession sql, ResolvedQuery query)
    {
        var args = new List<object?>();
        var where = ScriptRunWhere(query, args);
        return sql.Query(
            $"SELECT r.session_id, r.run_id, r.script, r.title, r.status, r.error, r.origin, r.start_ms, r.duration_ms, r.calls, r.steps, r.sessions, r.model FROM {P}script_run r{ScriptRunJoin(query)}{where} "
            + $"ORDER BY r.start_ms DESC, r.run_id LIMIT {RecentScriptRuns.ToString(CultureInfo.InvariantCulture)}",
            static reader => new ScriptRunSample(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.GetString(6),
                Instant(reader.GetInt64(7)),
                reader.IsDBNull(8) ? null : reader.GetInt64(8),
                reader.GetInt64(9),
                reader.GetInt64(10),
                reader.GetInt64(11),
                reader.GetString(12)),
            [.. args]);
    }

    // The turns of each model and those that called exec, from the table of runs; the runs of scripts and their calls from the facts.
    private List<ScriptAdoptionRow> ScriptAdoption(SqlSession sql, ResolvedQuery query, BucketPlan single)
    {
        var args = new List<object?>();
        var where = RunWhere(query, query.Range, args);
        var turns = sql.Query($"SELECT r.provider, r.model, COUNT(*), SUM(CASE WHEN r.exec_calls > 0 THEN 1 ELSE 0 END) FROM {P}run r{RunJoin(query)}{where} GROUP BY r.provider, r.model",
            static reader => (Provider: reader.GetString(0), Model: reader.GetString(1), Turns: reader.GetInt64(2), WithScript: reader.GetInt64(3)), [.. args]);
        var scripts = Aggregate(sql, query, single, FactTables.Scripts, ["SUM(f.runs_started)", "SUM(f.calls)", "SUM(f.runs_completed + f.runs_failed + f.runs_cancelled + f.runs_interrupted)"], ["provider", "model"])
            .GroupBy(static row => (row.Keys[0], row.Keys[1]))
            .ToDictionary(static group => group.Key, static group => (Runs: group.Sum(static row => row.Values[0]), Calls: group.Sum(static row => row.Values[1]), Ended: group.Sum(static row => row.Values[2])));
        var keys = turns.Select(static row => (row.Provider, row.Model)).Concat(scripts.Keys).Distinct();
        return [.. keys.Select(key =>
            {
                var turn = turns.FirstOrDefault(row => row.Provider == key.Item1 && row.Model == key.Item2);
                scripts.TryGetValue(key, out var script);
                return new ScriptAdoptionRow(key.Item1, key.Item2, turn.Turns, turn.WithScript, turn.Turns > 0 ? (double)turn.WithScript / turn.Turns : 0, (long)script.Runs,
                    script.Ended > 0 ? script.Calls / script.Ended : null);
            })
            .Where(static row => row.Turns > 0 || row.ScriptRuns > 0)
            .OrderByDescending(static row => row.Turns)
            .ThenBy(static row => row.Model, StringComparer.Ordinal)
            .Take(MaxLimit)];
    }

    private List<ScriptTurnsRow> ScriptTurns(SqlSession sql, ResolvedQuery query)
    {
        var args = new List<object?>();
        var where = RunWhere(query, query.Range, args);
        return sql.Query(
            $"SELECT CASE WHEN r.exec_calls > 0 THEN 1 ELSE 0 END, COUNT(*), SUM(r.tool_calls), SUM(r.requests), SUM(r.input_tokens + r.output_tokens), SUM(MAX(0, r.end_ms - r.start_ms - r.skipped_ms)) "
            + $"FROM {P}run r{RunJoin(query)}{where} GROUP BY 1 ORDER BY 1 DESC",
            static reader =>
            {
                var turns = reader.GetInt64(1);
                double Average(int index) => turns > 0 ? reader.GetDouble(index) / turns : 0;
                return new ScriptTurnsRow(reader.GetInt64(0) == 1, turns, Average(2), Average(3), Average(4), Average(5));
            },
            [.. args]);
    }

    private static string Instant(long milliseconds) => DateTimeOffset.FromUnixTimeMilliseconds(milliseconds).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}
