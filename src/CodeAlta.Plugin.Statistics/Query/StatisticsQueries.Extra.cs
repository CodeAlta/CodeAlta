using System.Collections.Frozen;
using System.Globalization;
using System.Text;
using CodeAlta.Plugin.Statistics.Facts;
using CodeAlta.Plugin.Statistics.Store;

namespace CodeAlta.Plugin.Statistics.Query;

public sealed partial class StatisticsQueries
{
    private static readonly FrozenDictionary<string, DetailList> DetailLists = new Dictionary<string, DetailList>
    {
        ["shell-program"] = DetailList.ShellProgram,
        ["alta-command"] = DetailList.AltaCommand,
        ["changed-file-extension"] = DetailList.ChangedFileExtension,
        ["permission-mode"] = DetailList.PermissionMode,
        ["compaction-trigger"] = DetailList.CompactionTrigger,
        ["run-origin"] = DetailList.RunOrigin,
        ["skill"] = DetailList.Skill,
        ["session-origin"] = DetailList.SessionOrigin,
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets the lists of names <see cref="DetailsAsync"/> counts.</summary>
    public static IReadOnlyList<string> DetailNames { get; } = [.. DetailLists.Keys.Order(StringComparer.Ordinal)];

    /// <summary>Gets the ways <see cref="RunsAsync"/> sorts.</summary>
    public static IReadOnlyList<string> RunSorts { get; } = ["recent", "longest", "tokens", "tools"];

    /// <summary>
    /// The names the facts count, ranked: the programs of shell commands (<c>git</c>, <c>dotnet</c>), the first two words of <c>alta</c> commands, the
    /// extensions of the files that tools changed, the skills, the permission modes runs started in, the triggers of compactions.
    /// </summary>
    /// <param name="request">The period and the filters (only the project and the space apply); the limit is the most names returned.</param>
    /// <param name="list">The list: one of <see cref="DetailNames"/>.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The names, the most counted first, with their share.</returns>
    /// <exception cref="ArgumentException">The list or the period is not valid.</exception>
    public async ValueTask<DetailsResult> DetailsAsync(StatisticsRequest request, string list, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(list);
        if (!DetailLists.TryGetValue(list.Trim(), out var detail))
        {
            throw new ArgumentException($"'{list}' is not a list of names: use one of {string.Join(", ", DetailNames)}.");
        }

        var query = await ResolveAsync(request, cancellationToken).ConfigureAwait(false);
        return await _store.ReadAsync(sql => BuildDetails(sql, query, list.Trim().ToLowerInvariant(), detail), cancellationToken).ConfigureAwait(false);
    }

    private DetailsResult BuildDetails(SqlSession sql, ResolvedQuery query, string list, DetailList detail)
    {
        var single = BucketPlan.Single(query.Range, query.WeekStart, Days);
        foreach (var (name, value) in new[] { ("provider", query.Filter.Provider), ("model", query.Filter.Model), ("effort", query.Filter.Effort), ("toolKind", query.Filter.ToolKind), ("origin", query.Filter.Origin) })
        {
            if (value is { Length: > 0 })
            {
                query.Ignored.Add(name);
            }
        }

        var counts = Aggregate(sql, query, single, FactTables.Details, ["SUM(f.n)"], ["name"], $"f.list = {(int)detail}")
            .Where(static row => row.Values[0] > 0)
            .OrderByDescending(static row => row.Values[0])
            .ThenBy(static row => row.Keys[0], StringComparer.Ordinal)
            .ToList();
        var total = counts.Sum(static row => row.Values[0]);
        var limit = LimitOf(query.Request);
        var rows = counts.Take(limit).Select(row => new NameCount(row.Keys[0].Length == 0 ? "(none)" : row.Keys[0], (long)row.Values[0], total > 0 ? row.Values[0] / total : 0)).ToList();
        return new DetailsResult(Header(query), list, rows, (long)total, counts.Count, counts.Count > rows.Count);
    }

    /// <summary>
    /// The runs of a period, one row each, to see what a prompt brings back: the size of the prompt, the time, the tool calls and the words of the answer.
    /// </summary>
    /// <param name="request">The period and the filters: provider, model, effort, origin (who sent the prompt), project and space; the limit is the most runs returned.</param>
    /// <param name="sort">One of <see cref="RunSorts"/>, largest first.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The runs; a run is listed under the day it started.</returns>
    /// <exception cref="ArgumentException">The sort or the period is not valid.</exception>
    public async ValueTask<RunsResult> RunsAsync(StatisticsRequest request, string sort = "recent", CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var key = (sort ?? "recent").Trim().ToLowerInvariant();
        if (!RunSorts.Contains(key, StringComparer.Ordinal))
        {
            throw new ArgumentException($"'{sort}' is not a sort: use one of {string.Join(", ", RunSorts)}.");
        }

        var query = await ResolveAsync(request, cancellationToken).ConfigureAwait(false);
        return await _store.ReadAsync(sql => BuildRuns(sql, query, key), cancellationToken).ConfigureAwait(false);
    }

    private RunsResult BuildRuns(SqlSession sql, ResolvedQuery query, string sort)
    {
        var args = new List<object?>();
        var where = RunWhere(query, query.Plan.Range, args);
        var total = (int)sql.ScalarLong($"SELECT COUNT(*) FROM {P}run r{RunJoin(query)}{where}", [.. args]);
        var order = sort switch
        {
            "longest" => "(r.end_ms - r.start_ms - r.skipped_ms) DESC",
            "tokens" => "(r.input_tokens + r.output_tokens) DESC",
            "tools" => "r.tool_calls DESC",
            _ => "r.start_ms DESC",
        };
        var limit = LimitOf(query.Request);
        var runs = sql.Query(
            $"SELECT r.session_id, r.run_id, r.start_ms, r.end_ms - r.skipped_ms, r.outcome, r.sender, r.prompt_kind, r.prompt_chars, r.prompt_words, r.requests, r.tool_calls, r.tool_failures, r.input_tokens, r.output_tokens, r.answer_chars, r.answer_words, r.provider, r.model, r.effort FROM {P}run r{RunJoin(query)}{where} ORDER BY {order}, r.run_id LIMIT {limit.ToString(CultureInfo.InvariantCulture)}",
            static reader => new RunSample(
                reader.GetString(0),
                reader.GetString(1),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2)).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
                Math.Max(0, reader.GetInt64(3) - reader.GetInt64(2)),
                ((RunOutcome)reader.GetInt64(4)).ToString().ToLowerInvariant(),
                ((PromptSender)reader.GetInt64(5)) switch { PromptSender.You => "you", var other => other.ToString().ToLowerInvariant() },
                ((PromptKind)reader.GetInt64(6)).ToString().ToLowerInvariant(),
                reader.GetInt64(7),
                reader.GetInt64(8),
                reader.GetInt64(9),
                reader.GetInt64(10),
                reader.GetInt64(11),
                reader.GetInt64(12),
                reader.GetInt64(13),
                reader.GetInt64(14),
                reader.GetInt64(15),
                reader.GetString(16),
                reader.GetString(17),
                reader.GetString(18)),
            [.. args]);
        if (query.Filter.ToolKind is { Length: > 0 })
        {
            query.Ignored.Add("toolKind");
        }

        return new RunsResult(Header(query), sort, runs, total, total > runs.Count);
    }

    // The filters of the project and of the space need the session.
    private string RunJoin(ResolvedQuery query)
        => query.ProjectRefs is not null ? $" LEFT JOIN {P}session s ON s.session_id = r.session_id" : string.Empty;

    private string RunWhere(ResolvedQuery query, DayRange range, List<object?> args)
    {
        string Arg(object? value)
        {
            args.Add(value);
            return "@p" + (args.Count - 1).ToString(CultureInfo.InvariantCulture);
        }

        var (lower, upper) = Days.QuarterRangeOfDays(LocalDays.ToDay(range.From), LocalDays.ToDay(range.To));
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

        if (filter.Effort is { Length: > 0 } effort)
        {
            where.Append(" AND r.effort = ").Append(Arg(string.Equals(effort, "none", StringComparison.OrdinalIgnoreCase) ? string.Empty : effort)).Append(" COLLATE NOCASE");
        }

        if (filter.Origin is { Length: > 0 } origin)
        {
            if (TryParseOrigin(origin, out var sender))
            {
                where.Append(" AND r.sender = ").Append(Arg((long)sender));
            }
            else
            {
                query.Ignored.Add("origin");
            }
        }

        where.Append(query.ProjectClause("s.project_ref", reference => Arg(reference)));

        if (query.SessionIds is { } sessions)
        {
            where.Append(sessions.Count == 0 ? " AND 0" : " AND r.session_id IN (" + string.Join(", ", sessions.Select(id => Arg(id))) + ")");
        }

        return where.ToString();
    }

    /// <summary>Counts the runs started in each bucket from the table of runs, which knows who sent the prompt of each: the runs of an origin, or the runs cut by origin.</summary>
    private List<AggRow> RunCounts(SqlSession sql, ResolvedQuery query, BucketPlan plan, bool byOrigin)
    {
        var args = new List<object?>();
        var where = RunWhere(query, plan.Range, args);
        var rows = new Dictionary<string, AggRow>(StringComparer.Ordinal);
        var join = RunJoin(query);
        foreach (var (startMs, sender) in sql.Query($"SELECT r.start_ms, r.sender FROM {P}run r{join}{where}", static reader => (reader.GetInt64(0), reader.GetInt64(1)), [.. args]))
        {
            var bucket = plan.OfQuarter((int)Math.Floor(startMs / (double)QuarterHour.Milliseconds));
            if (bucket >= 0)
            {
                Count(rows, bucket, byOrigin ? sender.ToString(CultureInfo.InvariantCulture) : string.Empty);
            }
        }

        return [.. rows.Values];
    }
}
