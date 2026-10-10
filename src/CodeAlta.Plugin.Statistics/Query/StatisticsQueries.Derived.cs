using System.Globalization;
using System.Text;
using CodeAlta.Plugin.Statistics.Facts;
using CodeAlta.Plugin.Statistics.Store;

namespace CodeAlta.Plugin.Statistics.Query;

/// <content>
/// The numbers that are computed from the rows the store keeps, when they are asked, and are not facts of their own: the sessions
/// that run at once (from the runs), the average fill of the context (a sum over a count) and the depth of the trees of sub-agents
/// (from the parents of the sessions).
/// </content>
public sealed partial class StatisticsQueries
{
    private const string SessionsAtOnceMetric = "sessions-at-once";
    private const string ContextFillMetric = "context-fill";
    private const string SubAgentDepthList = "sub-agent-depth";

    /// <summary>The note of a result that left out runs whose first and last time are not those of one stretch of work.</summary>
    internal const string RunsLeftOutNote = "runs-of-unknown-time-left-out";

    /// <summary>The note of a result in which the parent of a session, or of one of its parents, is not a session the statistics know.</summary>
    internal const string ParentsUnknownNote = "some-parents-unknown";

    // ----- sessions at once -----

    // The most sessions with a run going at the same moment, in each bucket. A run is going from its first record to its last one,
    // as the journal dates them: that is what was observed, and it includes the time a run waits for an answer.
    private SeriesResult BuildSessionsAtOnce(SqlSession sql, ResolvedQuery query, string? group)
    {
        if (group is not null)
        {
            throw new ArgumentException($"'{SessionsAtOnceMetric}' is one line: it is not cut by a group.");
        }

        if (query.Filter.ToolKind is { Length: > 0 })
        {
            query.Ignored.Add("toolKind");
        }

        var current = SessionsAtOnce(sql, query, query.Plan);
        var previous = query.ComparePlan is { } plan ? SessionsAtOnce(sql, query, plan) : null;

        // The total of the line is the most of the period: the most of two days is not their sum.
        var line = new SeriesLine(string.Empty, "total", current, previous, Largest(current), previous is null ? null : Largest(previous));
        return new SeriesResult(Header(query), SessionsAtOnceMetric, "count", null, BucketInfos(query.Plan), [line]);
    }

    private static double Largest(double[] values)
    {
        var largest = 0d;
        foreach (var value in values)
        {
            largest = Math.Max(largest, value);
        }

        return largest;
    }

    private double[] SessionsAtOnce(SqlSession sql, ResolvedQuery query, BucketPlan plan)
    {
        var values = new double[plan.Buckets.Count];
        var args = new List<object?>();
        var where = RunWhere(query, plan.Range, args, overlapping: true);
        var join = RunJoin(query);

        // A run with a record far from the others (more than seven days: a damaged line, a clock that was wrong) keeps its first
        // and its last time, and how much of the time between them is not its own, but not where: it cannot be placed, so it is
        // left out and the result says so.
        if (sql.ScalarLong($"SELECT COUNT(*) FROM {P}run r{join}{where} AND r.skipped_ms > 0", [.. args]) > 0 && !query.Notes.Contains(RunsLeftOutNote))
        {
            query.Notes.Add(RunsLeftOutNote);
        }

        var moments = new List<(long At, int Change, string Session)>();
        foreach (var (session, start, end) in sql.Query(
                     $"SELECT r.session_id, r.start_ms, r.end_ms FROM {P}run r{join}{where} AND r.skipped_ms = 0",
                     static reader => (reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2)),
                     [.. args]))
        {
            // A run without a length is going at its moment.
            moments.Add((start, 1, session));
            moments.Add((Math.Max(end, start + 1), -1, session));
        }

        // A run that ends when another one starts was not going with it: the ends of a moment come before its starts.
        moments.Sort(static (left, right) => left.At != right.At ? left.At.CompareTo(right.At) : left.Change.CompareTo(right.Change));
        var (lower, upper) = Days.QuarterRangeOfDays(LocalDays.ToDay(plan.Range.From), LocalDays.ToDay(plan.Range.To));
        var periodStart = lower * QuarterHour.Milliseconds;
        var periodEnd = upper * QuarterHour.Milliseconds;
        var going = new Dictionary<string, int>(StringComparer.Ordinal);
        var buckets = new Dictionary<int, int>();
        for (var index = 0; index < moments.Count; index++)
        {
            var (at, change, session) = moments[index];
            var runs = going.GetValueOrDefault(session) + change;
            if (runs > 0)
            {
                going[session] = runs;
            }
            else
            {
                going.Remove(session);
            }

            if (index + 1 == moments.Count || moments[index + 1].At == at || going.Count == 0)
            {
                continue;
            }

            // From this moment to the next one the same sessions are going: every quarter hour of that time has at least as many.
            var from = Math.Max(at, periodStart);
            var to = Math.Min(moments[index + 1].At, periodEnd);
            for (var quarter = (int)(from / QuarterHour.Milliseconds); from < to && quarter <= (int)((to - 1) / QuarterHour.Milliseconds); quarter++)
            {
                if (!buckets.TryGetValue(quarter, out var bucket))
                {
                    buckets[quarter] = bucket = plan.OfQuarter(quarter);
                }

                if (bucket >= 0 && going.Count > values[bucket])
                {
                    values[bucket] = going.Count;
                }
            }
        }

        return values;
    }

    // ----- the average fill of the context -----

    // The average fill of the context window of the requests that reported one: the sum of their fills over their number, in each
    // bucket and over the period. It is never the average of the averages of the rows, which would count a row of one request as
    // much as a row of a hundred.
    private SeriesResult BuildContextFill(SqlSession sql, ResolvedQuery query, string? group, int limit)
    {
        var column = group is null ? null : GroupColumn(FactTables.Usage, group);
        string[] aggregates = ["SUM(f.context_fill_ppm_sum)", "SUM(f.context_samples)"];
        var groups = column is null ? [] : new[] { column };
        var rows = Aggregate(sql, query, query.Plan, FactTables.Usage, aggregates, groups);
        var compared = query.ComparePlan is { } plan ? Aggregate(sql, query, plan, FactTables.Usage, aggregates, groups) : null;
        var count = query.Plan.Buckets.Count;
        var comparedCount = query.ComparePlan?.Buckets.Count ?? 0;
        var lines = new Dictionary<string, FillLine>(StringComparer.Ordinal);
        FillLine Line(string key)
        {
            if (!lines.TryGetValue(key, out var line))
            {
                lines[key] = line = new FillLine(count, compared is null ? -1 : comparedCount);
            }

            return line;
        }

        foreach (var row in rows)
        {
            var line = Line(row.Keys.Length == 0 ? string.Empty : row.Keys[0]);
            line.Sums[row.Bucket] += row.Values[0];
            line.Samples[row.Bucket] += row.Values[1];
        }

        if (compared is not null)
        {
            foreach (var row in compared)
            {
                var line = Line(row.Keys.Length == 0 ? string.Empty : row.Keys[0]);
                line.PreviousSums![row.Bucket] += row.Values[0];
                line.PreviousSamples![row.Bucket] += row.Values[1];
            }
        }

        // A line is a group that reported a window: one that never did has no average.
        var ordered = lines.Where(static pair => pair.Value.Samples.Sum() > 0 || (pair.Value.PreviousSamples?.Sum() ?? 0) > 0)
            .OrderByDescending(static pair => pair.Value.Samples.Sum()).ThenBy(static pair => pair.Key, StringComparer.Ordinal).ToList();
        var result = new List<SeriesLine>();
        if (ordered.Count == 0)
        {
            var (key, label) = LineName(query, column, string.Empty);
            result.Add(new FillLine(count, compared is null ? -1 : comparedCount).ToSeriesLine(key, label));
        }

        foreach (var (key, line) in ordered.Take(limit))
        {
            var (name, label) = LineName(query, column, key);
            result.Add(line.ToSeriesLine(name, label));
        }

        if (ordered.Count > limit)
        {
            // The rest is one line: the sums of all the others over their counts.
            var rest = new FillLine(count, compared is null ? -1 : comparedCount);
            foreach (var (_, line) in ordered.Skip(limit))
            {
                rest.Add(line);
            }

            result.Add(rest.ToSeriesLine("other", "other"));
        }

        return new SeriesResult(Header(query), ContextFillMetric, "ratio", column ?? group, BucketInfos(query.Plan), result);
    }

    // The sums of the fills (in millionths of the window) and the numbers of requests behind them, per bucket.
    private sealed class FillLine(int buckets, int comparedBuckets)
    {
        public double[] Sums { get; } = new double[buckets];

        public double[] Samples { get; } = new double[buckets];

        public double[]? PreviousSums { get; } = comparedBuckets < 0 ? null : new double[comparedBuckets];

        public double[]? PreviousSamples { get; } = comparedBuckets < 0 ? null : new double[comparedBuckets];

        public void Add(FillLine other)
        {
            AddTo(Sums, other.Sums);
            AddTo(Samples, other.Samples);
            if (PreviousSums is not null && other.PreviousSums is not null)
            {
                AddTo(PreviousSums, other.PreviousSums);
                AddTo(PreviousSamples!, other.PreviousSamples!);
            }
        }

        public SeriesLine ToSeriesLine(string key, string label)
            => new(key, label, Averages(Sums, Samples), PreviousSums is null ? null : Averages(PreviousSums, PreviousSamples!), Average(Sums.Sum(), Samples.Sum()),
                PreviousSums is null ? null : Average(PreviousSums.Sum(), PreviousSamples!.Sum()));

        private static void AddTo(double[] target, double[] source)
        {
            for (var index = 0; index < target.Length; index++)
            {
                target[index] += source[index];
            }
        }

        private static double[] Averages(double[] sums, double[] samples)
        {
            var values = new double[sums.Length];
            for (var index = 0; index < values.Length; index++)
            {
                values[index] = Average(sums[index], samples[index]);
            }

            return values;
        }

        // A bucket without a request that reported a window has no average: it is 0, and the metric of the samples says which.
        private static double Average(double sum, double samples) => samples > 0 ? sum / samples / 1e6 : 0;
    }

    // ----- the depth of the trees of sub-agents -----

    // How far below a session of its own a sub-agent is: 1 for a sub-agent, 2 for a sub-agent of a sub-agent. The sessions counted
    // are those with a parent that started in the period; their parents are followed through every session the statistics know.
    private DetailsResult BuildSubAgentDepth(SqlSession sql, ResolvedQuery query)
    {
        foreach (var (name, value) in new[] { ("provider", query.Filter.Provider), ("model", query.Filter.Model), ("effort", query.Filter.Effort), ("toolKind", query.Filter.ToolKind), ("origin", query.Filter.Origin) })
        {
            if (value is { Length: > 0 })
            {
                query.Ignored.Add(name);
            }
        }

        var parents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, parent) in sql.Query($"SELECT session_id, COALESCE(parent_session_id, '') FROM {P}session", static reader => (reader.GetString(0), reader.GetString(1))))
        {
            parents[id] = parent;
        }

        var args = new List<object?>();
        string Arg(object? value)
        {
            args.Add(value);
            return "@p" + (args.Count - 1).ToString(CultureInfo.InvariantCulture);
        }

        var (lower, upper) = Days.QuarterRangeOfDays(LocalDays.ToDay(query.Range.From), LocalDays.ToDay(query.Range.To));
        var builder = new StringBuilder($"SELECT s.session_id FROM {P}session s WHERE s.parent_session_id IS NOT NULL AND s.parent_session_id <> '' AND s.first_ms >= ")
            .Append(Arg(lower * QuarterHour.Milliseconds)).Append(" AND s.first_ms < ").Append(Arg(upper * QuarterHour.Milliseconds));
        builder.Append(query.ProjectClause("s.project_ref", reference => Arg(reference)));
        if (query.SessionIds is { } scope)
        {
            builder.Append(scope.Count == 0 ? " AND 0" : " AND s.session_id IN (" + string.Join(", ", scope.Select(id => Arg(id))) + ")");
        }

        var counts = new SortedDictionary<int, long>();
        var unknown = false;
        var path = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var session in sql.Query(builder.ToString(), static reader => reader.GetString(0), [.. args]))
        {
            // The parents are followed to a session that has none. A parent the statistics do not know (it was never read, or its
            // numbers were forgotten) is the top of what is known, and so is a parent that was already met (journals that name each
            // other): the depth is then the part that is known, and the result says so.
            var depth = 0;
            var current = session;
            path.Clear();
            path.Add(current);
            while (parents.TryGetValue(current, out var parent) && parent.Length > 0)
            {
                depth++;
                if (!parents.ContainsKey(parent) || !path.Add(parent))
                {
                    unknown = true;
                    break;
                }

                current = parent;
            }

            counts[depth] = counts.GetValueOrDefault(depth) + 1;
        }

        if (unknown && !query.Notes.Contains(ParentsUnknownNote))
        {
            query.Notes.Add(ParentsUnknownNote);
        }

        var total = counts.Values.Sum();
        var limit = LimitOf(query.Request);
        var rows = counts.Take(limit).Select(pair => new NameCount(pair.Key.ToString(CultureInfo.InvariantCulture), pair.Value, total > 0 ? pair.Value / (double)total : 0)).ToList();
        return new DetailsResult(Header(query), SubAgentDepthList, rows, total, counts.Count, counts.Count > rows.Count);
    }
}
