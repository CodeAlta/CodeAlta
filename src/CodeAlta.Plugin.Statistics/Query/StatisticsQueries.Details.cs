using System.Globalization;
using CodeAlta.Plugin.Statistics.Facts;
using CodeAlta.Plugin.Statistics.Store;

namespace CodeAlta.Plugin.Statistics.Query;

public sealed partial class StatisticsQueries
{
    private sealed record PercentileSet(double? P50, double? P90, long Count);

    private sealed record DistributionDef(HistogramMeasure Measure, string Unit);

    private static readonly IReadOnlyDictionary<string, DistributionDef> Distributions = new Dictionary<string, DistributionDef>(StringComparer.OrdinalIgnoreCase)
    {
        ["run-duration"] = new(HistogramMeasure.RunDurationMs, "ms"),
        ["tool-duration"] = new(HistogramMeasure.ToolDurationMs, "ms"),
        ["request-input"] = new(HistogramMeasure.RequestInputTokens, "tokens"),
        ["request-output"] = new(HistogramMeasure.RequestOutputTokens, "tokens"),
        ["prompt-chars"] = new(HistogramMeasure.PromptChars, "chars"),
        ["prompt-words"] = new(HistogramMeasure.PromptWords, "words"),
        ["run-cost"] = new(HistogramMeasure.RunCostMicro, "micro-unit"),
        ["run-tool-calls"] = new(HistogramMeasure.RunToolCalls, "count"),
    };

    /// <summary>Gets the names of the distributions <see cref="DistributionAsync"/> knows.</summary>
    public static IReadOnlyList<string> DistributionNames { get; } = [.. Distributions.Keys.Order(StringComparer.Ordinal)];

    /// <summary>A distribution over a period: the number of values in each fixed step, with the median and the 90th percentile.</summary>
    /// <param name="request">The period and the filters; only the project and the space apply to a distribution.</param>
    /// <param name="measure">The measure: one of <see cref="DistributionNames"/>.</param>
    /// <param name="subject">What the measure is of: a tool for <c>tool-duration</c>, a model for the tokens of a request, a unit for <c>run-cost</c>; null for all.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The steps that hold values, and the percentiles, exact within a step (about 19%).</returns>
    /// <exception cref="ArgumentException">The measure or the period is not valid.</exception>
    public async ValueTask<DistributionResult> DistributionAsync(StatisticsRequest request, string measure, string? subject = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(measure);
        if (!Distributions.TryGetValue(measure.Trim(), out var definition))
        {
            throw new ArgumentException($"'{measure}' is not a distribution: use one of {string.Join(", ", DistributionNames)}.");
        }

        var query = await ResolveAsync(request, cancellationToken).ConfigureAwait(false);
        return await _store.ReadAsync(sql => BuildDistribution(sql, query, measure.Trim().ToLowerInvariant(), definition, subject), cancellationToken).ConfigureAwait(false);
    }

    private DistributionResult BuildDistribution(SqlSession sql, ResolvedQuery query, string name, DistributionDef definition, string? subject)
    {
        var single = BucketPlan.Single(query.Range, query.WeekStart, Days);
        var counts = ReadHistogram(sql, query, single, definition.Measure, subject);
        var total = counts.Values.Sum();
        var steps = counts.OrderBy(static pair => pair.Key)
            .Where(static pair => pair.Value > 0)
            .Select(static pair => new DistributionStep(Facts.HistogramSteps.LowerBound(pair.Key), Facts.HistogramSteps.UpperBound(pair.Key) is var upper && upper == long.MaxValue ? null : upper, pair.Value))
            .ToList();
        return new DistributionResult(Header(query), name, subject, definition.Unit, total, Quantile(counts, total, 0.5), Quantile(counts, total, 0.9), steps);
    }

    private Dictionary<int, long> ReadHistogram(SqlSession sql, ResolvedQuery query, BucketPlan single, HistogramMeasure measure, string? subject)
    {
        var counts = new Dictionary<int, long>();
        foreach (var row in Aggregate(sql, query, single, FactTables.Histograms, ["SUM(f.n)"], ["step"], $"f.measure = {(int)measure}", restrict: subject is null ? null : [("subject", [subject])]))
        {
            counts[int.Parse(row.Keys[0], CultureInfo.InvariantCulture)] = counts.GetValueOrDefault(int.Parse(row.Keys[0], CultureInfo.InvariantCulture)) + (long)row.Values[0];
        }

        return counts;
    }

    private Dictionary<string, PercentileSet> Percentiles(SqlSession sql, ResolvedQuery query, BucketPlan single, HistogramMeasure measure, string? subject, bool perSubject)
    {
        var result = new Dictionary<string, PercentileSet>(StringComparer.Ordinal);
        var perKey = new Dictionary<string, Dictionary<int, long>>(StringComparer.Ordinal);
        foreach (var row in Aggregate(sql, query, single, FactTables.Histograms, ["SUM(f.n)"], perSubject ? ["subject", "step"] : ["step"], $"f.measure = {(int)measure}", restrict: subject is null ? null : [("subject", [subject])]))
        {
            var key = perSubject ? row.Keys[0] : string.Empty;
            var step = int.Parse(row.Keys[^1], CultureInfo.InvariantCulture);
            if (!perKey.TryGetValue(key, out var counts))
            {
                perKey[key] = counts = [];
            }

            counts[step] = counts.GetValueOrDefault(step) + (long)row.Values[0];
        }

        foreach (var (key, counts) in perKey)
        {
            var total = counts.Values.Sum();
            result[key] = new PercentileSet(Quantile(counts, total, 0.5), Quantile(counts, total, 0.9), total);
        }

        return result;
    }

    /// <summary>The value of a quantile of a distribution in steps, interpolated inside the step it falls in.</summary>
    private static double? Quantile(Dictionary<int, long> counts, long total, double fraction)
    {
        if (total <= 0)
        {
            return null;
        }

        var target = fraction * total;
        long before = 0;
        foreach (var (step, count) in counts.OrderBy(static pair => pair.Key))
        {
            if (count > 0 && before + count >= target)
            {
                var lower = Facts.HistogramSteps.LowerBound(step);
                var upper = Facts.HistogramSteps.UpperBound(step);
                if (upper == long.MaxValue || upper <= lower)
                {
                    return lower;
                }

                return lower + ((target - before) / count * (upper - lower));
            }

            before += count;
        }

        return null;
    }

    // --- The calendar and the week ---

    /// <summary>The days of a period with their active time, runs and prompts: a heat map of a year.</summary>
    /// <param name="request">The period (usually a year) and the filters; the frequency is always the day.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The days that have activity.</returns>
    /// <exception cref="ArgumentException">The period is not valid.</exception>
    public async ValueTask<CalendarResult> CalendarAsync(StatisticsRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var query = await ResolveAsync(request with { Frequency = StatisticsFrequency.Day }, cancellationToken).ConfigureAwait(false);
        return await _store.ReadAsync(sql => BuildCalendar(sql, query), cancellationToken).ConfigureAwait(false);
    }

    private CalendarResult BuildCalendar(SqlSession sql, ResolvedQuery query)
    {
        var plan = query.Plan;
        var activity = Aggregate(sql, query, plan, FactTables.Activity, ["SUM(f.active_ms)", "SUM(f.runs_started)"], []);
        var prompts = Aggregate(sql, query, plan, FactTables.Content, ["SUM(f.count)"], [], "f.kind = 0 AND f.sender = 1");
        var time = new double[plan.Buckets.Count];
        var runs = new long[plan.Buckets.Count];
        var yours = new long[plan.Buckets.Count];
        foreach (var row in activity)
        {
            time[row.Bucket] += row.Values[0];
            runs[row.Bucket] += (long)row.Values[1];
        }

        foreach (var row in prompts)
        {
            yours[row.Bucket] += (long)row.Values[0];
        }

        var days = new List<CalendarDay>();
        for (var index = 0; index < plan.Buckets.Count; index++)
        {
            if (time[index] != 0 || runs[index] != 0 || yours[index] != 0)
            {
                days.Add(new CalendarDay(plan.Buckets[index].Label, time[index], runs[index], yours[index]));
            }
        }

        return new CalendarResult(Header(query), days, time.Length == 0 ? 0 : time.Max());
    }

    /// <summary>When in the week the work happens: the active time and the runs by day of the week and hour of the day.</summary>
    /// <param name="request">The period and the filters; the first day of the week is the one of the request.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>Seven rows of 24 hours, from the first day of the week.</returns>
    /// <exception cref="ArgumentException">The period is not valid.</exception>
    public async ValueTask<WeekHourResult> WeekHourAsync(StatisticsRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var query = await ResolveAsync(request, cancellationToken).ConfigureAwait(false);
        return await _store.ReadAsync(sql => BuildWeekHour(sql, query), cancellationToken).ConfigureAwait(false);
    }

    private WeekHourResult BuildWeekHour(SqlSession sql, ResolvedQuery query)
    {
        var plan = BucketPlan.WeekHour(query.Range, query.WeekStart, Days);
        var rows = Aggregate(sql, query, plan, FactTables.Activity, ["SUM(f.active_ms)", "SUM(f.runs_started)"], []);
        var time = Enumerable.Range(0, 7).Select(static _ => new double[24]).ToArray();
        var runs = Enumerable.Range(0, 7).Select(static _ => new double[24]).ToArray();
        foreach (var row in rows)
        {
            time[row.Bucket / 24][row.Bucket % 24] += row.Values[0];
            runs[row.Bucket / 24][row.Bucket % 24] += row.Values[1];
        }

        var names = Enumerable.Range(0, 7).Select(offset => CultureInfo.InvariantCulture.DateTimeFormat.GetDayName((DayOfWeek)(((int)query.WeekStart + offset) % 7))).ToList();
        return new WeekHourResult(Header(query), names, [.. time.Select(static row => (IReadOnlyList<double>)row)], [.. runs.Select(static row => (IReadOnlyList<double>)row)]);
    }

    // --- Records and health ---

    /// <summary>The records of a period: the longest run, the busiest day, the longest streak of days, the largest request and prompt.</summary>
    /// <param name="request">The period; only the project and the space filters apply to a record.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The records.</returns>
    /// <exception cref="ArgumentException">The period is not valid.</exception>
    public async ValueTask<RecordsResult> RecordsAsync(StatisticsRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var query = await ResolveAsync(request, cancellationToken).ConfigureAwait(false);
        return await _store.ReadAsync(sql => BuildRecords(sql, query), cancellationToken).ConfigureAwait(false);
    }

    private RecordsResult BuildRecords(SqlSession sql, ResolvedQuery query)
    {
        foreach (var (name, value) in new[] { ("provider", query.Filter.Provider), ("model", query.Filter.Model), ("effort", query.Filter.Effort), ("toolKind", query.Filter.ToolKind), ("origin", query.Filter.Origin) })
        {
            if (value is { Length: > 0 })
            {
                query.Ignored.Add(name);
            }
        }

        var records = new List<RecordEntry>();
        var quarters = query.ProjectRefs is not null || query.SessionIds is not null;
        var args = new List<object?>();
        string Arg(object? value)
        {
            args.Add(value);
            return "@p" + (args.Count - 1).ToString(CultureInfo.InvariantCulture);
        }

        var table = quarters ? $"{P}extreme_q e LEFT JOIN {P}session s ON s.session_id = e.session_id" : $"{P}extreme_day e";
        var periodColumn = quarters ? "e.q" : "e.p";
        var (lower, upper) = quarters
            ? Days.QuarterRangeOfDays(LocalDays.ToDay(query.Range.From), LocalDays.ToDay(query.Range.To))
            : (LocalDays.ToDay(query.Range.From), LocalDays.ToDay(query.Range.To) + 1);
        var where = new System.Text.StringBuilder($" WHERE {periodColumn} >= {Arg((long)lower)} AND {periodColumn} < {Arg((long)upper)}");
        if (query.ProjectRefs is { } refs)
        {
            where.Append(refs.Count == 0 ? " AND 0" : " AND s.project_ref IN (" + string.Join(", ", refs.Select(reference => Arg(reference))) + ")");
        }

        if (query.SessionIds is { } sessions)
        {
            where.Append(sessions.Count == 0 ? " AND 0" : " AND e.session_id IN (" + string.Join(", ", sessions.Select(id => Arg(id))) + ")");
        }

        var extremes = sql.Query(
            $"SELECT e.measure, e.subject, e.value, e.session_id, e.run_id, e.at_ms FROM {table}{where} ORDER BY e.value DESC, e.at_ms ASC",
            static reader => (Measure: (ExtremeMeasure)reader.GetInt64(0), Subject: reader.GetString(1), Value: reader.GetInt64(2), Session: reader.GetString(3), Run: reader.IsDBNull(4) ? null : reader.GetString(4), At: reader.GetInt64(5)),
            [.. args]);

        void Add(string name, ExtremeMeasure measure, string unit, double scale, int take)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in extremes.Where(row => row.Measure == measure))
            {
                if (seen.Count >= take)
                {
                    break;
                }

                if (seen.Add(row.Subject))
                {
                    records.Add(new RecordEntry(name, row.Subject, row.Value * scale, unit, row.Session, row.Run, DateTimeOffset.FromUnixTimeMilliseconds(row.At).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)));
                }
            }
        }

        Add("longestRun", ExtremeMeasure.LongestRunMs, "ms", 1, 1);
        Add("mostToolCallsInRun", ExtremeMeasure.MostToolCallsInRun, "count", 1, 1);
        Add("longestTool", ExtremeMeasure.LongestToolMs, "ms", 1, 5);
        Add("highestContextFill", ExtremeMeasure.HighestContextFillPpm, "ratio", 1e-6, 3);
        Add("largestRequestInput", ExtremeMeasure.LargestRequestInputTokens, "tokens", 1, 3);

        var dayPlan = BucketPlan.Create(query.Range, StatisticsFrequency.Day, query.WeekStart, Days);
        var activity = Aggregate(sql, query, dayPlan, FactTables.Activity, ["SUM(f.active_ms)", "SUM(f.runs_started)"], []);
        if (activity.Count > 0)
        {
            var busiest = activity.OrderByDescending(static row => row.Values[0]).ThenBy(static row => row.Bucket).First();
            records.Add(new RecordEntry("busiestDay", string.Empty, busiest.Values[0], "ms", null, null, dayPlan.Buckets[busiest.Bucket].Label));
        }

        var yours = Aggregate(sql, query, dayPlan, FactTables.Content, ["SUM(f.count)"], [], "f.kind = 0 AND f.sender = 1").Where(static row => row.Values[0] > 0).Select(static row => row.Bucket).Order().ToList();
        if (yours.Count > 0)
        {
            int best = 1, current = 1, bestEnd = yours[0];
            for (var index = 1; index < yours.Count; index++)
            {
                current = yours[index] == yours[index - 1] + 1 ? current + 1 : 1;
                if (current > best)
                {
                    best = current;
                    bestEnd = yours[index];
                }
            }

            records.Add(new RecordEntry("longestStreak", string.Empty, best, "days", null, null, dayPlan.Buckets[bestEnd].Label));
        }

        var steps = ReadHistogram(sql, query, BucketPlan.Single(query.Range, query.WeekStart, Days), HistogramMeasure.PromptChars, null);
        if (steps.Where(static pair => pair.Value > 0).Select(static pair => pair.Key).DefaultIfEmpty(-1).Max() is var largest && largest >= 0)
        {
            records.Add(new RecordEntry("largestPrompt", string.Empty, Facts.HistogramSteps.LowerBound(largest), "chars", null, null, null));
        }

        return new RecordsResult(Header(query), records);
    }

    /// <summary>How the work goes: errors, runs found without an end, failing tools, compactions and how full the context gets.</summary>
    /// <param name="request">The period, the frequency and the filters.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The health of the period.</returns>
    /// <exception cref="ArgumentException">The period or the frequency is not valid.</exception>
    public async ValueTask<HealthResult> HealthAsync(StatisticsRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var query = await ResolveAsync(request, cancellationToken).ConfigureAwait(false);
        return await _store.ReadAsync(sql => BuildHealth(sql, query), cancellationToken).ConfigureAwait(false);
    }

    private HealthResult BuildHealth(SqlSession sql, ResolvedQuery query)
    {
        var plan = query.Plan;
        var activity = Aggregate(sql, query, plan, FactTables.Activity, ["SUM(f.errors)", "SUM(f.runs_interrupted)", "SUM(f.runs_started)", "SUM(f.compactions)", "SUM(f.compaction_tokens_before)", "SUM(f.compaction_tokens_after)"], []);
        var errors = new double[plan.Buckets.Count];
        var interrupted = new double[plan.Buckets.Count];
        var compactions = new double[plan.Buckets.Count];
        double runs = 0, before = 0, after = 0;
        foreach (var row in activity)
        {
            errors[row.Bucket] += row.Values[0];
            interrupted[row.Bucket] += row.Values[1];
            runs += row.Values[2];
            compactions[row.Bucket] += row.Values[3];
            before += row.Values[4];
            after += row.Values[5];
        }

        var failed = ToolRows(sql, query, null, out _, out _, int.MaxValue).Where(static row => row.Failures > 0).OrderByDescending(static row => row.Failures).Take(10).ToList();
        var single = BucketPlan.Single(query.Range, query.WeekStart, Days);
        var triggers = Aggregate(sql, query, single, FactTables.Details, ["SUM(f.n)"], ["name"], $"f.list = {(int)DetailList.CompactionTrigger}")
            .Where(static row => row.Values[0] != 0)
            .Select(static row => new KeyValuePair<string, long>(row.Keys[0], (long)row.Values[0]))
            .OrderByDescending(static pair => pair.Value)
            .ToList();
        var context = Aggregate(sql, query, single, FactTables.Usage, ["SUM(f.context_fill_ppm_sum)", "SUM(f.context_samples)", "MAX(f.context_fill_ppm_max)"], ["provider", "model"])
            .Where(static row => row.Values[1] > 0)
            .Select(static row => new ContextFillRow(row.Keys[0], row.Keys[1], row.Values[0] / row.Values[1] / 1e6, row.Values[2] / 1e6, (long)row.Values[1]))
            .OrderByDescending(static row => row.Highest)
            .Take(MaxLimit)
            .ToList();
        var totalErrors = errors.Sum();
        return new HealthResult(Header(query), BucketInfos(plan), errors, interrupted, (long)runs, runs > 0 ? totalErrors / runs : 0, failed, triggers, compactions, (long)before, (long)after, context);
    }

    // --- One session ---

    /// <summary>The numbers of one session over its whole life, with its sub-agents when asked.</summary>
    /// <param name="sessionId">The session: its identifier, or the start of it when only one session matches.</param>
    /// <param name="withChildren">Whether the totals include the sessions the session created, at any depth.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The session, its totals, its latest runs, its models and its tools; null when no session matches.</returns>
    /// <exception cref="ArgumentException"><paramref name="sessionId"/> is empty, or the start of several sessions.</exception>
    public async ValueTask<SessionDetailResult?> SessionAsync(string sessionId, bool withChildren = false, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var query = await ResolveAsync(new StatisticsRequest { Period = "all", Frequency = StatisticsFrequency.Month }, cancellationToken).ConfigureAwait(false);
        return await _store.ReadAsync(sql => BuildSession(sql, query, sessionId.Trim(), withChildren), cancellationToken).ConfigureAwait(false);
    }

    private SessionDetailResult? BuildSession(SqlSession sql, ResolvedQuery query, string text, bool withChildren)
    {
        var matches = sql.Query($"SELECT session_id FROM {P}session WHERE session_id = @p0 OR session_id LIKE @p1 ESCAPE '\\' LIMIT 3", static reader => reader.GetString(0), text, EscapeLike(text) + "%");
        if (matches.Count == 0)
        {
            return null;
        }

        var exact = matches.FirstOrDefault(match => string.Equals(match, text, StringComparison.OrdinalIgnoreCase));
        if (exact is null && matches.Count > 1)
        {
            throw new ArgumentException($"'{text}' is the start of several sessions: give more of its identifier.");
        }

        var id = exact ?? matches[0];
        IReadOnlyList<string> ids = withChildren
            ? sql.Query(
                $"WITH RECURSIVE tree(id) AS (SELECT @p0 UNION SELECT s.session_id FROM {P}session s JOIN tree t ON s.parent_session_id = t.id) SELECT id FROM tree",
                static reader => reader.GetString(0),
                id)
            : [id];
        query.SessionIds = ids;
        var rootRow = SessionEntries(sql, query, [id], "recent", 1, out _).FirstOrDefault()
            ?? sql.Query(
                $"SELECT session_id, title, project_ref, provider, parent_session_id, COALESCE(last_ms, 0), deleted FROM {P}session WHERE session_id = @p0",
                reader => new SessionEntry(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.IsDBNull(2) ? null : query.ProjectName(reader.GetString(2)), reader.IsDBNull(3) ? null : reader.GetString(3), null, reader.IsDBNull(4) ? null : reader.GetString(4), 0, 0, 0, 0, [], 0, null, reader.GetInt64(6) != 0),
                id).First();
        var children = withChildren && ids.Count > 1
            ? SessionEntries(sql, query, [.. ids.Where(other => other != id)], "recent", MaxLimit, out _)
            : [];
        var summary = BuildSummary(sql, query);
        var runs = sql.Query(
            $"SELECT run_id, start_ms, end_ms, outcome, sender, requests, tool_calls, input_tokens, output_tokens, model FROM {P}run WHERE session_id = @p0 ORDER BY start_ms DESC LIMIT 50",
            static reader => new RunEntry(
                reader.GetString(0),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1)).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
                Math.Max(0, reader.GetInt64(2) - reader.GetInt64(1)),
                ((RunOutcome)reader.GetInt64(3)).ToString().ToLowerInvariant(),
                ((PromptSender)reader.GetInt64(4)) switch { PromptSender.You => "you", var other => other.ToString().ToLowerInvariant() },
                reader.GetInt64(5),
                reader.GetInt64(6),
                reader.GetInt64(7),
                reader.GetInt64(8),
                reader.GetString(9)),
            id);
        var models = ModelRows(sql, query, 20);
        var tools = ToolRows(sql, query, null, out _, out _, 10);
        return new SessionDetailResult(Header(query), rootRow, children, [.. summary.Tiles, .. summary.Costs], runs, models, tools);
    }

    private static string EscapeLike(string text)
        => text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);
}
