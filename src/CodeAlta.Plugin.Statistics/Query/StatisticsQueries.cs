using System.Globalization;
using System.Text;
using CodeAlta.Plugin.Statistics.Facts;
using CodeAlta.Plugin.Statistics.Store;

namespace CodeAlta.Plugin.Statistics.Query;

/// <summary>
/// The questions of the pages of the statistics, and of <c>alta statistics</c>, asked of the store: summaries, series, rankings,
/// distributions, tables, records and health, for any period, frequency, filter and comparison. A canvas and a command read the
/// same numbers because they ask the same methods.
/// </summary>
/// <remarks>
/// <para>
/// Every result starts with the request it answers (the period, the frequency that was used, the compared period) and says how much of
/// the history its numbers rest on (<see cref="StatisticsCoverage"/>), so that a page can hatch what is not read yet. Counts and sums
/// are read from the roll-ups of the local days, months and years; a question that needs the session (a project, a space) or the hour
/// reads the facts of the quarter hours. Every table and ranking has a limit.
/// </para>
/// <para>A space filter names the projects the space has <b>today</b>: the sessions do not record the spaces of the time.</para>
/// </remarks>
public sealed partial class StatisticsQueries
{
    /// <summary>The most rows a table or a ranking returns when the request sets no limit.</summary>
    public const int DefaultLimit = 50;

    /// <summary>The most rows a table or a ranking returns, whatever the request asks.</summary>
    public const int MaxLimit = 500;

    private readonly StatisticsStore _store;
    private readonly IProjectDirectory? _directory;
    private readonly TimeProvider _time;

    internal StatisticsQueries(StatisticsStore store, IProjectDirectory? directory = null, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        _directory = directory;
        _time = time ?? TimeProvider.System;
    }

    private LocalDays Days => _store.Days;

    private string P => _store.Prefix;

    /// <summary>Gets how much of the history the numbers rest on, for a period.</summary>
    /// <param name="request">The request; only its period matters.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The coverage.</returns>
    /// <exception cref="ArgumentException">The period is not one.</exception>
    public async ValueTask<StatisticsCoverage> CoverageAsync(StatisticsRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var query = await ResolveAsync(request, cancellationToken).ConfigureAwait(false);
        return query.Coverage;
    }

    internal async ValueTask<ResolvedQuery> ResolveAsync(StatisticsRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var filter = request.Filter ?? new StatisticsFilter();
        var projects = _directory is not null
            ? await _directory.ListProjectsAsync(cancellationToken).ConfigureAwait(false)
            : [];
        var spaces = _directory is not null && filter.Space is not null
            ? await _directory.ListSpacesAsync(cancellationToken).ConfigureAwait(false)
            : [];
        var meta = await _store.GetAllMetaAsync(cancellationToken).ConfigureAwait(false);
        var storedNames = await _store.GetProjectNamesAsync(cancellationToken).ConfigureAwait(false);
        var firstDay = await _store.ReadAsync(
            sql =>
            {
                var value = sql.ScalarLong($"SELECT MIN(p) FROM (SELECT MIN(p) AS p FROM {P}activity_day UNION ALL SELECT MIN(p) FROM {P}usage_day UNION ALL SELECT MIN(p) FROM {P}content_day UNION ALL SELECT MIN(p) FROM {P}tool_day)");
                return value > 0 ? (int?)value : null;
            },
            cancellationToken).ConfigureAwait(false);

        var weekStart = request.WeekStart ?? CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(_time.GetUtcNow().UtcDateTime, Days.TimeZone));
        var range = PeriodParser.Resolve(request.Period ?? "30d", today, firstDay is { } first ? LocalDays.ToDate(first) : null, weekStart);
        var frequency = request.Frequency == StatisticsFrequency.Auto ? BucketPlan.AutoFrequency(range) : request.Frequency;
        var plan = BucketPlan.Create(range, frequency, weekStart, Days);
        DayRange? compare = request.Comparison switch
        {
            StatisticsComparison.PreviousPeriod => new DayRange(range.From.AddDays(-range.Days), range.From.AddDays(-1)),
            StatisticsComparison.SamePeriodLastYear => new DayRange(range.From.AddYears(-1), range.To.AddYears(-1)),
            _ => null,
        };
        BucketPlan? comparePlan = compare is { } previous ? BucketPlan.Create(previous, frequency, weekStart, Days) : null;

        var names = new Dictionary<string, string>(storedNames, StringComparer.OrdinalIgnoreCase);
        foreach (var project in projects)
        {
            names[project.Id] = project.Name;
        }

        var notes = new List<string>();
        var refs = ResolveProjectRefs(filter, projects, spaces, notes);
        return new ResolvedQuery
        {
            Request = request,
            Today = today,
            Range = range,
            Compare = compare,
            Plan = plan,
            ComparePlan = comparePlan,
            WeekStart = weekStart,
            ProjectRefs = refs,
            ProjectNames = names,
            Coverage = ReadCoverage(meta, range),
            Notes = notes,
        };
    }

    private static IReadOnlyList<string>? ResolveProjectRefs(StatisticsFilter filter, IReadOnlyList<ProjectInfo> projects, IReadOnlyList<SpaceInfo> spaces, List<string> notes)
    {
        IEnumerable<string>? result = null;
        if (filter.Project is { Length: > 0 } project)
        {
            var found = projects.FirstOrDefault(item => string.Equals(item.Id, project, StringComparison.OrdinalIgnoreCase))
                ?? projects.FirstOrDefault(item => string.Equals(item.Slug, project, StringComparison.OrdinalIgnoreCase))
                ?? projects.FirstOrDefault(item => string.Equals(item.Name, project, StringComparison.OrdinalIgnoreCase))
                ?? projects.FirstOrDefault(item => item.Id.StartsWith(project, StringComparison.OrdinalIgnoreCase));
            result = [found?.Id ?? project];
        }

        if (filter.Space is { Length: > 0 } space)
        {
            var found = spaces.FirstOrDefault(item => string.Equals(item.Id, space, StringComparison.OrdinalIgnoreCase))
                ?? spaces.FirstOrDefault(item => string.Equals(item.Name, space, StringComparison.OrdinalIgnoreCase))
                ?? spaces.FirstOrDefault(item => item.Id.StartsWith(space, StringComparison.OrdinalIgnoreCase));
            if (found is null)
            {
                throw new ArgumentException($"No space has the id or the name '{space}'.");
            }

            notes.Add("space-membership-is-current");
            var members = projects.Where(item => found.IsDefault || item.SpaceIds.Contains(found.Id, StringComparer.Ordinal)).Select(static item => item.Id);
            result = result is null ? members : result.Intersect(members, StringComparer.OrdinalIgnoreCase);
        }

        return result?.ToArray();
    }

    private StatisticsCoverage ReadCoverage(IReadOnlyDictionary<string, string> meta, DayRange range)
    {
        var chosen = meta.GetValueOrDefault("history.choice");
        var state = string.IsNullOrEmpty(chosen) ? "needs-choice"
            : meta.GetValueOrDefault("history.stopped") == "1" ? "stopped"
            : meta.GetValueOrDefault("history.paused") == "1" ? "paused"
            : meta.GetValueOrDefault("history.done") == "1" ? "done"
            : "reading";
        int? completeFrom = meta.TryGetValue("history.complete_from_day", out var text) && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var day) && day > 0 ? day : null;
        string? completeFromText = completeFrom is { } value ? LocalDays.ToDate(value).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null;
        var complete = state != "needs-choice" && (completeFrom is null || LocalDays.ToDay(range.From) >= completeFrom);
        return new StatisticsCoverage(complete, state, completeFromText);
    }

    /// <summary>Everything a query learned about its request: the days, the buckets, the filters that were resolved.</summary>
    internal sealed class ResolvedQuery
    {
        public required StatisticsRequest Request { get; init; }

        public required DateOnly Today { get; init; }

        public required DayRange Range { get; init; }

        public DayRange? Compare { get; init; }

        public required BucketPlan Plan { get; init; }

        public BucketPlan? ComparePlan { get; init; }

        public required DayOfWeek WeekStart { get; init; }

        /// <summary>Gets the references of the projects the query is limited to; null for no project filter.</summary>
        public IReadOnlyList<string>? ProjectRefs { get; init; }

        public required IReadOnlyDictionary<string, string> ProjectNames { get; init; }

        public required StatisticsCoverage Coverage { get; init; }

        public required List<string> Notes { get; init; }

        public HashSet<string> Ignored { get; } = new(StringComparer.Ordinal);

        /// <summary>Gets or sets the sessions the query is limited to (the numbers of one session and its sub-agents); null for all.</summary>
        public IReadOnlyList<string>? SessionIds { get; set; }

        public StatisticsFilter Filter => Request.Filter ?? new StatisticsFilter();

        public string ProjectName(string reference)
            => reference.Length == 0 ? string.Empty : ProjectNames.TryGetValue(reference, out var name) ? name : reference.Length > 8 ? reference[..8] : reference;
    }

    private QueryHeader Header(ResolvedQuery query)
        => new(
            query.Request.Period,
            query.Range.From.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            query.Range.To.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            query.Plan.FrequencyName,
            Days.TimeZone.Id,
            query.Compare?.From.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            query.Compare?.To.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            query.Coverage,
            [.. query.Ignored.Order(StringComparer.Ordinal)],
            [.. query.Notes]);

    private static IReadOnlyList<BucketInfo> BucketInfos(BucketPlan plan)
        => [.. plan.Buckets.Select(static bucket => new BucketInfo(bucket.Index, bucket.Start.ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture), bucket.Label))];

    private static int LimitOf(StatisticsRequest request, int fallback = DefaultLimit)
        => Math.Clamp(request.Limit ?? fallback, 1, MaxLimit);

    private static double? Change(double value, double? previous)
        => previous is { } before && before > 0 ? (value - before) / before : null;

    // --- Aggregation over the tables of facts ---

    /// <summary>A row of an aggregation: the bucket, the values of the groups, and the aggregates.</summary>
    internal sealed record AggRow(int Bucket, string[] Keys, double[] Values);

    private static readonly string[] SessionOnlyGroups = ["project"];

    /// <summary>
    /// Adds a family of facts up per bucket and per group over a period, with the filters of the query. It reads the roll-ups of the
    /// days, months or years when they can answer, and the quarter hours when the query needs the session, the hour, or a project.
    /// </summary>
    internal List<AggRow> Aggregate(
        SqlSession sql,
        ResolvedQuery query,
        BucketPlan plan,
        FactTable family,
        string[] aggregates,
        string[] groups,
        string? extraWhere = null,
        IReadOnlyList<(string Group, IReadOnlyList<string> Keys)>? restrict = null,
        bool applyOrigin = true)
    {
        var args = new List<object?>();
        string Arg(object? value)
        {
            args.Add(value);
            return "@p" + (args.Count - 1).ToString(CultureInfo.InvariantCulture);
        }

        var needsSession = query.ProjectRefs is not null || groups.Contains("project", StringComparer.Ordinal) || groups.Contains("delegated", StringComparer.Ordinal);
        var needsQuarter = needsSession || query.SessionIds is not null || groups.Contains("session", StringComparer.Ordinal);
        var level = ChooseLevel(query, plan, needsQuarter);
        var periodColumn = level == RollupLevel.Quarter ? "f.q" : "f.p";
        var tableName = family.TableName(P, level);
        var groupExpressions = groups.Select(group => GroupExpression(family, group)).ToArray();

        var select = new StringBuilder("SELECT ").Append(periodColumn);
        foreach (var expression in groupExpressions)
        {
            select.Append(", ").Append(expression);
        }

        foreach (var aggregate in aggregates)
        {
            select.Append(", ").Append(aggregate);
        }

        select.Append(" FROM ").Append(tableName).Append(" f");
        if (needsSession)
        {
            select.Append(" LEFT JOIN ").Append(P).Append("session s ON s.session_id = f.session_id");
        }

        var (lower, upper) = PeriodBounds(plan.Range, level);
        select.Append(" WHERE ").Append(periodColumn).Append(" >= ").Append(Arg(lower)).Append(" AND ").Append(periodColumn).Append(" < ").Append(Arg(upper));
        AppendFilters(select, query, family, Arg, applyOrigin);
        if (query.SessionIds is { } sessionIds)
        {
            select.Append(sessionIds.Count == 0 ? " AND 0" : " AND f.session_id IN (" + string.Join(", ", sessionIds.Select(id => Arg(id))) + ")");
        }

        if (extraWhere is not null)
        {
            select.Append(" AND ").Append(extraWhere);
        }

        if (restrict is not null)
        {
            foreach (var (group, keys) in restrict)
            {
                var expression = GroupExpression(family, group);
                if (keys.Count == 0)
                {
                    select.Append(" AND 0");
                    continue;
                }

                select.Append(" AND ").Append(expression).Append(" IN (").Append(string.Join(", ", keys.Select(key => Arg(key)))).Append(')');
            }
        }

        select.Append(" GROUP BY ").Append(periodColumn);
        foreach (var expression in groupExpressions)
        {
            select.Append(", ").Append(expression);
        }

        var isMax = aggregates.Select(static aggregate => aggregate.StartsWith("MAX(", StringComparison.Ordinal)).ToArray();
        var merged = new Dictionary<string, AggRow>(StringComparer.Ordinal);
        foreach (var row in sql.Query(
                     select.ToString(),
                     reader => ReadAggregate(reader, level, plan, groups.Length, aggregates.Length),
                     [.. args]))
        {
            if (row.Bucket < 0)
            {
                continue;
            }

            var id = row.Bucket.ToString(CultureInfo.InvariantCulture) + "\u001f" + string.Join('\u001f', row.Keys);
            if (merged.TryGetValue(id, out var existing))
            {
                for (var index = 0; index < existing.Values.Length; index++)
                {
                    existing.Values[index] = isMax[index] ? Math.Max(existing.Values[index], row.Values[index]) : existing.Values[index] + row.Values[index];
                }
            }
            else
            {
                merged[id] = row;
            }
        }

        return [.. merged.Values];
    }

    private AggRow ReadAggregate(Microsoft.Data.Sqlite.SqliteDataReader reader, RollupLevel level, BucketPlan plan, int groupCount, int aggregateCount)
    {
        var period = (int)reader.GetInt64(0);
        var bucket = level switch
        {
            RollupLevel.Quarter => plan.OfQuarter(period),
            RollupLevel.Day => plan.OfDay(period),
            RollupLevel.Month => plan.OfMonth(period),
            _ => plan.OfYear(period),
        };
        var keys = new string[groupCount];
        for (var index = 0; index < groupCount; index++)
        {
            keys[index] = Convert.ToString(reader.GetValue(1 + index), CultureInfo.InvariantCulture) ?? string.Empty;
        }

        var values = new double[aggregateCount];
        for (var index = 0; index < aggregateCount; index++)
        {
            var position = 1 + groupCount + index;
            values[index] = reader.IsDBNull(position) ? 0 : reader.GetDouble(position);
        }

        return new AggRow(bucket, keys, values);
    }

    private RollupLevel ChooseLevel(ResolvedQuery query, BucketPlan plan, bool needsSession)
    {
        if (needsSession || plan.IsWeekHour || (plan.Frequency == StatisticsFrequency.Hour && !plan.IsSingle))
        {
            return RollupLevel.Quarter;
        }

        var range = plan.Range;
        var endsToday = range.To >= query.Today;
        var wholeMonths = range.From.Day == 1 && (range.To.AddDays(1).Day == 1 || endsToday);
        var wholeYears = range.From is { Month: 1, Day: 1 } && ((range.To.Month == 12 && range.To.Day == 31) || endsToday);
        if (wholeYears && (plan.IsSingle || plan.Frequency == StatisticsFrequency.Year))
        {
            return RollupLevel.Year;
        }

        if (wholeMonths && (plan.IsSingle || plan.Frequency is StatisticsFrequency.Month or StatisticsFrequency.Year))
        {
            return RollupLevel.Month;
        }

        return RollupLevel.Day;
    }

    private (long Lower, long Upper) PeriodBounds(DayRange range, RollupLevel level)
    {
        switch (level)
        {
            case RollupLevel.Quarter:
                var (from, to) = Days.QuarterRangeOfDays(LocalDays.ToDay(range.From), LocalDays.ToDay(range.To));
                return (from, to);
            case RollupLevel.Day:
                return (LocalDays.ToDay(range.From), (long)LocalDays.ToDay(range.To) + 1);
            case RollupLevel.Month:
                return (LocalDays.MonthOf(LocalDays.ToDay(range.From)), (long)LocalDays.MonthOf(LocalDays.ToDay(range.To)) + 1);
            default:
                return (range.From.Year, (long)range.To.Year + 1);
        }
    }

    private string GroupExpression(FactTable family, string group)
    {
        switch (group)
        {
            case "project":
                return "COALESCE(s.project_ref, '')";
            case "delegated":
                return "CASE WHEN s.parent_session_id IS NULL THEN 'direct' ELSE 'sub-agent' END";
            case "session":
                return "f.session_id";
            case "origin":
                return "f.sender";
            case "content-kind":
                return "f.kind";
            case "prompt-kind":
                return "f.prompt_kind";
            case "agent-prompt":
                return "f.agent_prompt";
            default:
                if (!family.Keys.Any(key => key.Name == group))
                {
                    throw new ArgumentException($"'{group}' is not a way to group {family.Name} facts.");
                }

                return "f." + group;
        }
    }

    private void AppendFilters(StringBuilder where, ResolvedQuery query, FactTable family, Func<object?, string> arg, bool applyOrigin)
    {
        var filter = query.Filter;
        bool Has(string column) => family.Keys.Any(key => key.Name == column);

        if (filter.Provider is { Length: > 0 } provider)
        {
            if (Has("provider"))
            {
                where.Append(" AND f.provider = ").Append(arg(provider)).Append(" COLLATE NOCASE");
            }
            else
            {
                query.Ignored.Add("provider");
            }
        }

        if (filter.Model is { Length: > 0 } model)
        {
            if (Has("model"))
            {
                where.Append(" AND f.model = ").Append(arg(model)).Append(" COLLATE NOCASE");
            }
            else
            {
                query.Ignored.Add("model");
            }
        }

        if (filter.Effort is { Length: > 0 } effort)
        {
            if (Has("effort"))
            {
                where.Append(" AND f.effort = ").Append(arg(string.Equals(effort, "none", StringComparison.OrdinalIgnoreCase) ? string.Empty : effort)).Append(" COLLATE NOCASE");
            }
            else
            {
                query.Ignored.Add("effort");
            }
        }

        if (filter.ToolKind is { Length: > 0 } kind)
        {
            if (family.Name == "tool" && TryParseEnum<ToolKind>(kind, out var toolKind))
            {
                where.Append(" AND f.kind = ").Append(arg((long)toolKind));
            }
            else
            {
                query.Ignored.Add("toolKind");
            }
        }

        if (filter.Origin is { Length: > 0 } origin)
        {
            if (family.Name == "content" && applyOrigin && TryParseOrigin(origin, out var sender))
            {
                where.Append(" AND f.sender = ").Append(arg((long)sender));
            }
            else
            {
                query.Ignored.Add("origin");
            }
        }

        if (query.ProjectRefs is { } refs)
        {
            where.Append(refs.Count == 0 ? " AND 0" : " AND s.project_ref IN (" + string.Join(", ", refs.Select(reference => arg(reference))) + ")");
        }
    }

    internal static bool TryParseEnum<T>(string text, out T value)
        where T : struct, Enum
        => Enum.TryParse(text.Replace("-", string.Empty, StringComparison.Ordinal), ignoreCase: true, out value) && Enum.IsDefined(value);

    internal static bool TryParseOrigin(string text, out PromptSender sender)
    {
        switch (text.Trim().ToLowerInvariant())
        {
            case "you":
            case "user":
                sender = PromptSender.You;
                return true;
            case "agent":
                sender = PromptSender.Agent;
                return true;
            case "automation":
                sender = PromptSender.Automation;
                return true;
            case "reminder":
                sender = PromptSender.Reminder;
                return true;
            case "other":
                sender = PromptSender.Other;
                return true;
            default:
                sender = PromptSender.None;
                return false;
        }
    }

    private string GroupLabel(ResolvedQuery query, string group, string key)
    {
        switch (group)
        {
            case "project":
                return key.Length == 0 ? "(no project)" : query.ProjectName(key);
            case "kind":
                return long.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var kind) && Enum.IsDefined((ToolKind)kind) ? ((ToolKind)kind).ToString().ToLowerInvariant() : key;
            case "origin":
                return long.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sender) && Enum.IsDefined((PromptSender)sender) ? ((PromptSender)sender) switch { PromptSender.You => "you", var other => other.ToString().ToLowerInvariant() } : key;
            case "prompt-kind":
                return long.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var promptKind) && Enum.IsDefined((PromptKind)promptKind) ? ((PromptKind)promptKind).ToString().ToLowerInvariant() : key;
            case "content-kind":
                return long.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var content) && Enum.IsDefined((ContentKind)content) ? ((ContentKind)content).ToString().ToLowerInvariant() : key;
            case "purpose":
                return key == "1" ? "compaction" : "turn";
            case "effort":
                return key.Length == 0 ? "none" : key;
            default:
                return key.Length == 0 ? "(none)" : key;
        }
    }
}
