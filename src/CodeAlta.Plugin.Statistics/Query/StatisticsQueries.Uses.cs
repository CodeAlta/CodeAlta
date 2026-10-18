using System.Globalization;
using CodeAlta.Plugin.Statistics.Facts;
using CodeAlta.Plugin.Statistics.Store;

namespace CodeAlta.Plugin.Statistics.Query;

public sealed partial class StatisticsQueries
{
    private static readonly DetailList[] SkillLists = [DetailList.Skill, DetailList.SkillAttached, DetailList.SkillRead];

    /// <summary>
    /// The skills used in a period: each with its uses, split into the activations by agents, the attachments to prompts of yours or
    /// of agents and the reads of its <c>SKILL.md</c>, the sessions that used it, its last use and a line.
    /// </summary>
    /// <param name="request">The period, the frequency and the filters (only the project, the space and the session apply); the limit is the most skills returned.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The skills, the most used first.</returns>
    /// <exception cref="ArgumentException">The period or the frequency is not valid.</exception>
    public async ValueTask<SkillsResult> SkillsAsync(StatisticsRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var query = await ResolveAsync(request, cancellationToken).ConfigureAwait(false);
        return await _store.ReadAsync(sql => BuildSkills(sql, query), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The MCP tools called in a period, those called through <c>alta mcp tool call</c> included: each with its calls, the sessions
    /// that called it, its last call and a line, and the calls of each server.
    /// </summary>
    /// <param name="request">The period, the frequency and the filters (only the project, the space and the session apply); the limit is the most tools returned.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The tools, the most called first.</returns>
    /// <exception cref="ArgumentException">The period or the frequency is not valid.</exception>
    public async ValueTask<McpToolsResult> McpToolsAsync(StatisticsRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var query = await ResolveAsync(request, cancellationToken).ConfigureAwait(false);
        return await _store.ReadAsync(sql => BuildMcpTools(sql, query), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The recent use of each skill, per project and space: its uses before <paramref name="end"/> and within
    /// <paramref name="window"/>, each weighted by <c>0.5^(age / halfLife)</c>, an exponential moving average. Every way a skill is
    /// used counts the same: an activation by an agent, an attachment to a prompt, a read of its <c>SKILL.md</c>.
    /// </summary>
    /// <param name="end">The end of the period, excluded: the start of today, so that the uses of today do not move the scores.</param>
    /// <param name="window">How far back the uses are read.</param>
    /// <param name="halfLife">The age at which a use counts half.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The scores, sorted by project, space and name: the same facts always give the same list.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="window"/> or <paramref name="halfLife"/> is not positive.</exception>
    public ValueTask<IReadOnlyList<SkillUsageScore>> SkillUsageScoresAsync(DateTimeOffset end, TimeSpan window, TimeSpan halfLife, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(window, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(halfLife, TimeSpan.Zero);
        var upper = QuarterHour.Of(end).Index;
        var lower = QuarterHour.Of(end - window).Index;
        var endMs = end.ToUnixTimeMilliseconds();
        var halfLifeMs = halfLife.TotalMilliseconds;
        return _store.ReadAsync<IReadOnlyList<SkillUsageScore>>(sql =>
        {
            var table = FactTables.Details.TableName(P, RollupLevel.Quarter);
            var rows = sql.Query(
                $"SELECT COALESCE(s.project_ref, ''), COALESCE(s.space_id, ''), f.name, f.q, SUM(f.n) FROM {table} f"
                + $" LEFT JOIN {P}session s ON s.session_id = f.session_id"
                + $" WHERE f.q >= @p0 AND f.q < @p1 AND {ListsWhere(SkillLists)} AND f.name <> ''"
                + " GROUP BY 1, 2, 3, 4",
                static reader => (Project: reader.GetString(0), Space: reader.GetString(1), Name: reader.GetString(2), Quarter: reader.GetInt32(3), Uses: reader.GetInt64(4)),
                lower,
                upper);
            var scores = new SortedDictionary<(string, string, string), (double Score, long Uses)>();
            foreach (var row in rows)
            {
                // A use counts from the middle of its quarter hour.
                var ageMs = Math.Max(0, endMs - (new QuarterHour(row.Quarter).Start.ToUnixTimeMilliseconds() + 7.5 * 60_000));
                var key = (row.Project, row.Space, row.Name);
                scores.TryGetValue(key, out var score);
                scores[key] = (score.Score + row.Uses * Math.Pow(0.5, ageMs / halfLifeMs), score.Uses + row.Uses);
            }

            return [.. scores.Select(static pair => new SkillUsageScore(
                pair.Key.Item1.Length == 0 ? null : pair.Key.Item1,
                pair.Key.Item2.Length == 0 ? null : pair.Key.Item2,
                pair.Key.Item3,
                Math.Round(pair.Value.Score, 6),
                pair.Value.Uses))];
        }, cancellationToken);
    }

    private SkillsResult BuildSkills(SqlSession sql, ResolvedQuery query)
    {
        var uses = NamedUses(sql, query, SkillLists);
        var ranked = uses.Values
            .Select(static use => new SkillRow(use.Name, use.Total, use.Of(DetailList.Skill), use.Of(DetailList.SkillAttached), use.Of(DetailList.SkillRead),
                use.Sessions, use.LastUsed, []))
            .OrderByDescending(static row => row.Uses)
            .ThenBy(static row => row.Name, StringComparer.Ordinal)
            .ToList();
        var top = ranked.Take(LimitOf(query.Request)).ToList();
        var sparks = NamedSparks(sql, query, SkillLists, [.. top.Select(static row => row.Name)]);
        var rows = top.Select(row => row with { Spark = sparks.TryGetValue(row.Name, out var spark) ? spark : new double[query.Plan.Buckets.Count] }).ToList();
        return new SkillsResult(Header(query), BucketInfos(query.Plan), rows, ranked.Sum(static row => row.Uses), ranked.Count, ranked.Count > rows.Count);
    }

    private McpToolsResult BuildMcpTools(SqlSession sql, ResolvedQuery query)
    {
        var uses = NamedUses(sql, query, [DetailList.McpTool]);
        var ranked = uses.Values
            .Select(static use => SplitMcp(use.Name) is var (server, tool) ? new McpToolUseRow(server, tool, use.Total, use.Sessions, use.LastUsed, []) : null)
            .OfType<McpToolUseRow>()
            .OrderByDescending(static row => row.Calls)
            .ThenBy(static row => row.Server, StringComparer.Ordinal)
            .ThenBy(static row => row.Tool, StringComparer.Ordinal)
            .ToList();
        var total = ranked.Sum(static row => row.Calls);
        var servers = ranked.GroupBy(static row => row.Server, StringComparer.Ordinal)
            .Select(group => (Name: group.Key, Count: group.Sum(static row => row.Calls)))
            .OrderByDescending(static server => server.Count)
            .ThenBy(static server => server.Name, StringComparer.Ordinal)
            .Select(server => new NameCount(server.Name, server.Count, total > 0 ? (double)server.Count / total : 0))
            .ToList();
        var top = ranked.Take(LimitOf(query.Request)).ToList();
        var sparks = NamedSparks(sql, query, [DetailList.McpTool], [.. top.Select(static row => row.Server + "/" + row.Tool)]);
        var rows = top.Select(row => row with { Spark = sparks.TryGetValue(row.Server + "/" + row.Tool, out var spark) ? spark : new double[query.Plan.Buckets.Count] }).ToList();
        return new McpToolsResult(Header(query), BucketInfos(query.Plan), rows, servers, total, ranked.Count, ranked.Count > rows.Count);

        static (string Server, string Tool)? SplitMcp(string name)
        {
            var slash = name.IndexOf('/');
            return slash > 0 && slash < name.Length - 1 ? (name[..slash], name[(slash + 1)..]) : null;
        }
    }

    // The counts of the names of some lists over the period, per list, with the sessions and the last quarter hour of each name. A
    // filter the lists cannot honor is named as ignored, as for the other lists of names.
    private Dictionary<string, NamedUse> NamedUses(SqlSession sql, ResolvedQuery query, DetailList[] lists)
    {
        foreach (var (name, value) in new[] { ("provider", query.Filter.Provider), ("model", query.Filter.Model), ("effort", query.Filter.Effort), ("toolKind", query.Filter.ToolKind), ("origin", query.Filter.Origin) })
        {
            if (value is { Length: > 0 })
            {
                query.Ignored.Add(name);
            }
        }

        var single = BucketPlan.Single(query.Range, query.WeekStart, Days);
        var uses = new Dictionary<string, NamedUse>(StringComparer.Ordinal);
        // Grouped by session, the quarter hours are read: the sessions of a name and its last use come with its counts.
        foreach (var row in Aggregate(sql, query, single, FactTables.Details, ["SUM(f.n)", "MAX(f.q)"], ["list", "name", "session"], ListsWhere(lists)))
        {
            if (row.Values[0] <= 0 || !Enum.TryParse<DetailList>(row.Keys[0], out var list) || row.Keys[1].Length == 0)
            {
                continue;
            }

            if (!uses.TryGetValue(row.Keys[1], out var use))
            {
                uses[row.Keys[1]] = use = new NamedUse(row.Keys[1]);
            }

            use.Add(list, (long)row.Values[0], row.Keys[2], (int)row.Values[1]);
        }

        return uses;
    }

    private Dictionary<string, IReadOnlyList<double>> NamedSparks(SqlSession sql, ResolvedQuery query, DetailList[] lists, string[] names)
    {
        var result = new Dictionary<string, IReadOnlyList<double>>(StringComparer.Ordinal);
        if (names.Length == 0)
        {
            return result;
        }

        foreach (var row in Aggregate(sql, query, query.Plan, FactTables.Details, ["SUM(f.n)"], ["name"], ListsWhere(lists), Restrict("name", names)))
        {
            if (!result.TryGetValue(row.Keys[0], out var values))
            {
                result[row.Keys[0]] = values = new double[query.Plan.Buckets.Count];
            }

            ((double[])values)[row.Bucket] += row.Values[0];
        }

        return result;
    }

    private static string ListsWhere(DetailList[] lists)
        => "f.list IN (" + string.Join(", ", lists.Select(static list => ((int)list).ToString(CultureInfo.InvariantCulture))) + ")";

    // What a name of a list of names adds up to over a period.
    private sealed class NamedUse(string name)
    {
        private readonly Dictionary<DetailList, long> _byList = [];
        private readonly HashSet<string> _sessions = new(StringComparer.Ordinal);
        private int _lastQuarter = int.MinValue;

        public string Name { get; } = name;

        public long Total { get; private set; }

        public int Sessions => _sessions.Count;

        public string? LastUsed => _lastQuarter == int.MinValue ? null : new QuarterHour(_lastQuarter).ToString();

        public long Of(DetailList list) => _byList.TryGetValue(list, out var count) ? count : 0;

        public void Add(DetailList list, long count, string session, int quarter)
        {
            _byList[list] = Of(list) + count;
            Total += count;
            if (session.Length > 0)
            {
                _sessions.Add(session);
            }

            _lastQuarter = Math.Max(_lastQuarter, quarter);
        }
    }
}
